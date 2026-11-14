using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class DnsSpeedTests
{
    // ---- packet builder ----

    [Fact]
    public void Build_ProducesTheExactQuery()
    {
        var p = DnsQuery.Build(0x1234, "www.bbc.co.uk");
        var expected = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in new[] { "www", "bbc", "co", "uk" }) { expected.Add((byte)label.Length); expected.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
        expected.AddRange(new byte[] { 0, 0, 1, 0, 1 });
        Assert.Equal(expected.ToArray(), p);
    }

    [Fact]
    public void Build_TrailingDotIsAccepted_BadNamesThrow()
    {
        Assert.Equal(DnsQuery.Build(1, "example.com"), DnsQuery.Build(1, "example.com."));
        Assert.Throws<ArgumentException>(() => DnsQuery.Build(1, ""));
        Assert.Throws<ArgumentException>(() => DnsQuery.Build(1, "a..b"));
        Assert.Throws<ArgumentException>(() => DnsQuery.Build(1, new string('a', 64) + ".com"));
        Assert.Throws<ArgumentException>(() => DnsQuery.Build(1, new string('a', 254)));
        Assert.Throws<ArgumentException>(() => DnsQuery.Build(1, "b\u00fccher.de"));
    }

    // ---- reply parser ----

    private static byte[] Reply(ushort id, ushort flags, ushort answers = 1)
    {
        var b = new byte[12 + 20];
        b[0] = (byte)(id >> 8); b[1] = (byte)id;
        b[2] = (byte)(flags >> 8); b[3] = (byte)flags;
        b[4] = 0; b[5] = 1; b[6] = (byte)(answers >> 8); b[7] = (byte)answers;
        return b;
    }

    [Fact]
    public void Parse_NormalAnswer_IsOk()
    {
        var r = DnsQuery.Parse(Reply(0xBEEF, 0x8180), 0xBEEF);
        Assert.Equal(DnsReplyStatus.Ok, r.Status);
        Assert.True(r.Answered);
        Assert.Equal(1, r.AnswerCount);
    }

    [Fact]
    public void Parse_Nxdomain_IsNotAnAnswer()
    {
        var r = DnsQuery.Parse(Reply(7, 0x8183, 0), 7);
        Assert.Equal(DnsReplyStatus.NameError, r.Status);
        Assert.Equal(3, r.RCode);
        Assert.False(r.Answered);
    }

    [Theory]
    [InlineData(0x8182, DnsReplyStatus.ServerFailure)]
    [InlineData(0x8185, DnsReplyStatus.Refused)]
    [InlineData(0x8184, DnsReplyStatus.OtherError)]
    public void Parse_OtherErrorCodes(int flags, DnsReplyStatus expected)
    {
        var r = DnsQuery.Parse(Reply(9, (ushort)flags, 0), 9);
        Assert.Equal(expected, r.Status);
        Assert.False(r.Answered);
    }

    [Fact]
    public void Parse_TruncatedAnswer_StillCountsAsAnswered()
    {
        var r = DnsQuery.Parse(Reply(5, 0x8380), 5);
        Assert.Equal(DnsReplyStatus.Truncated, r.Status);
        Assert.True(r.Answered);
    }

    [Fact]
    public void Parse_WrongId_QueryPacket_AndShortPacket()
    {
        Assert.Equal(DnsReplyStatus.WrongId, DnsQuery.Parse(Reply(1, 0x8180), 2).Status);
        Assert.False(DnsQuery.Parse(Reply(1, 0x8180), 2).Answered);
        Assert.Equal(DnsReplyStatus.NotAResponse, DnsQuery.Parse(DnsQuery.Build(3, "www.google.com"), 3).Status);
        Assert.Equal(DnsReplyStatus.Malformed, DnsQuery.Parse(new byte[5], 0).Status);
        Assert.Equal(DnsReplyStatus.Malformed, DnsQuery.Parse(null, 0).Status);
    }

    // ---- statistics ----

    [Fact]
    public void Stats_MedianOfAnsweredAndFailurePercent()
    {
        var s = DnsStats.From(new[] { new DnsSample(true, 30), new DnsSample(true, 10), new DnsSample(false, 0), new DnsSample(true, 20) });
        Assert.Equal(4, s.Sent);
        Assert.Equal(3, s.Answered);
        Assert.Equal(20, s.MedianMs);
        Assert.Equal(25, s.FailurePercent);
        Assert.Null(DnsStats.From(new[] { new DnsSample(false, 0) }).MedianMs);
        Assert.Equal(0, DnsStats.From(Array.Empty<DnsSample>()).FailurePercent);
    }

    // ---- analysis ----

    private static DnsStats S(double? median, int sent = 12, int answered = 12) => new(sent, answered, median);
    private static DnsMeasurement M(DnsStats? current, DnsStats? a, DnsStats? b) => new() { Current = current, PublicA = a, PublicB = b, Names = 6 };
    private static FindingCode? Code(DnsMeasurement? m) => DnsQualityTriage.Evaluate(m)?.Code;

    [Fact]
    public void Median_AtTheLimit_IsFine_JustAboveIsSlow()
    {
        Assert.Null(Code(M(S(150), S(20), S(20))));
        Assert.Equal(FindingCode.NetQualityDnsSlow, Code(M(S(150.1), S(20), S(20))));
    }

    [Fact]
    public void Failures_AtThirtyPercent_AreFine_AboveAreSlow()
    {
        Assert.Null(Code(M(S(20, 10, 7), S(20), S(20))));
        Assert.Equal(FindingCode.NetQualityDnsSlow, Code(M(S(20, 10, 6), S(20), S(20))));
        Assert.Equal(FindingCode.NetQualityDnsSlow, Code(M(S(null, 12, 0), S(20), S(20))));
    }

    [Fact]
    public void FineOrNotMeasured_GivesNoFinding()
    {
        Assert.Null(Code(M(S(20), S(20), S(20))));
        Assert.Null(Code(M(null, S(20), S(20))));
        Assert.Null(Code(M(S(0, 0, 0), S(20), S(20))));
        Assert.Null(Code(null));
    }

    [Fact]
    public void FasterPublicServer_IsNamed_OnlyWhen50msAndTwiceAsFast()
    {
        Assert.Contains("A public DNS server answered in about 100 ms", DnsQualityTriage.Evaluate(M(S(200), S(100), S(110)))!.PlainMessage);
        Assert.DoesNotContain("A public DNS server", DnsQualityTriage.Evaluate(M(S(200), S(101), S(120)))!.PlainMessage); // not twice as fast
        Assert.DoesNotContain("A public DNS server", DnsQualityTriage.Evaluate(M(S(200), S(149), S(149)))!.PlainMessage); // less than 50 ms and not twice
        // The best of the two public servers is the one named.
        Assert.Contains("about 40 ms", DnsQualityTriage.Evaluate(M(S(300), S(90), S(40)))!.PlainMessage);
    }

    [Fact]
    public void GainOfExactly50ms_ButNotTwice_IsNotNamed_AndGainRuleIsReal()
    {
        // current 160, public 110: gain 50 ms, factor 1.45 -> not named. current 160, public 80: gain and factor both fine.
        Assert.DoesNotContain("A public DNS server", DnsQualityTriage.Evaluate(M(S(160), S(110), S(110)))!.PlainMessage);
        Assert.Contains("A public DNS server answered in about 80 ms", DnsQualityTriage.Evaluate(M(S(160), S(80), S(110)))!.PlainMessage);
    }

    [Fact]
    public void CurrentFailing_PublicAnswering_IsSaid()
    {
        var f = DnsQualityTriage.Evaluate(M(S(null, 12, 0), S(25), S(30)))!;
        Assert.Equal(FindingCode.NetQualityDnsSlow, f.Code);
        Assert.Contains("did not answer any", f.PlainMessage);
        Assert.Contains("answered the lookups that your current one did not", f.PlainMessage);
        var g = DnsQualityTriage.Evaluate(M(S(40, 12, 6), S(25), S(30)))!;
        Assert.Contains("did not answer about 50 of every 100 lookups", g.PlainMessage);
    }

    [Fact]
    public void EverythingSlow_SaysTheWholeConnectionIsSlow_NotDns()
    {
        var f = DnsQualityTriage.Evaluate(M(S(300), S(250), S(400)))!;
        Assert.Equal(FindingCode.NetQualityDnsSlowEverywhere, f.Code);
        Assert.Contains("whole internet connection is slow", f.PlainMessage);
        Assert.Contains("not the cause", f.PlainMessage);
        // One public server fast: DNS is the problem again.
        Assert.Equal(FindingCode.NetQualityDnsSlow, Code(M(S(300), S(250), S(30))));
        // Public servers failing most lookups also count as slow.
        Assert.Equal(FindingCode.NetQualityDnsSlowEverywhere, Code(M(S(300), S(20, 12, 4), S(30, 12, 5))));
    }

    [Fact]
    public void PublicServersBlocked_StillReportsSlowCurrent_WithoutComparison()
    {
        var f = DnsQualityTriage.Evaluate(M(S(300), S(null, 12, 0), S(null, 12, 0)))!;
        Assert.Equal(FindingCode.NetQualityDnsSlow, f.Code);
        Assert.DoesNotContain("A public DNS server", f.PlainMessage);
        Assert.Equal(FindingCode.NetQualityDnsSlow, Code(M(S(300), null, null)));
    }

    [Fact]
    public void TechnicalDetail_HasTheNumbersAndTheLabels()
    {
        var d = DnsQualityTriage.Evaluate(M(S(210.4), S(25), S(31, 12, 11)))!.TechnicalDetail;
        Assert.Contains("your current DNS server: sent 12, answered 12, median 210.4 ms, failed 0 %", d);
        Assert.Contains("public DNS server 1: sent 12, answered 12, median 25 ms", d);
        Assert.Contains("public DNS server 2: sent 12, answered 11", d);
    }

    // ---- integration with the connection quality triage ----

    private static QualityState State(DnsMeasurement? dns, PingStats? gw = null) => new()
    {
        GatewayFound = true, Gateway = gw ?? new PingStats(60, 60, 3, 1, 0), OnWifi = false, DurationSeconds = 15,
        InternetA = new PingStats(60, 60, 20, 3, 0), InternetB = new PingStats(60, 60, 22, 3, 0), Dns = dns,
    };

    private static IEnumerable<QualityState> Scenarios()
    {
        yield return State(M(S(210), S(25), S(30)));
        yield return State(M(S(null, 12, 0), S(25), S(30)));
        yield return State(M(S(300), S(250), S(400)));
        yield return State(M(S(300), S(null, 12, 0), S(null, 12, 0)));
        yield return State(M(S(20), S(25), S(30)));
        yield return State(null);
        yield return State(M(S(210), S(25), S(30)), new PingStats(60, 50, 40, 30, 17)); // bad router: DNS only in the detail
    }

    [Fact]
    public void SlowDns_WithSteadyPings_ReplacesTheAllClear_AndOffersTheSettingsStep()
    {
        var s = State(M(S(210), S(25), S(30)));
        var f = ConnectionQualityTriage.Evaluate(s);
        Assert.Single(f);
        Assert.Equal(FindingCode.NetQualityDnsSlow, f[0].Code);
        Assert.False(LadderRunner.IsAllClear(f[0].Code));
        var ladder = ConnectionQualityTriage.Ladder(s, f);
        var step = ladder.Single(r => r.Id == "dns_change_server");
        Assert.Equal("ui.open_settings_page", step.ActionId);
        Assert.Equal("network_status", step.Arg);
        Assert.Contains("DNS server assignment", step.Say);
        Assert.Contains("does not change this setting by itself", step.Say);
        Assert.NotNull(ConnectionQualityTriage.NotFixedHint(f));
    }

    [Fact]
    public void FineDns_KeepsTheSteadyFinding_AndPingProblemsWin()
    {
        Assert.Equal(FindingCode.NetQualitySteady, ConnectionQualityTriage.Evaluate(State(M(S(20), S(25), S(30))))[0].Code);
        Assert.Equal(FindingCode.NetQualitySteady, ConnectionQualityTriage.Evaluate(State(null))[0].Code);
        var bad = ConnectionQualityTriage.Evaluate(State(M(S(210), S(25), S(30)), new PingStats(60, 50, 40, 30, 17)));
        Assert.Equal(FindingCode.NetQualityLocalNetwork, bad[0].Code);
        Assert.Contains("your current DNS server", bad[0].TechnicalDetail);
    }

    [Fact]
    public void EverythingSlow_Ladder_HasNoDnsStep()
    {
        var s = State(M(S(300), S(250), S(400)));
        var f = ConnectionQualityTriage.Evaluate(s);
        Assert.Equal(FindingCode.NetQualityDnsSlowEverywhere, f[0].Code);
        Assert.DoesNotContain(ConnectionQualityTriage.Ladder(s, f), r => r.Id == "dns_change_server");
    }

    [Fact]
    public void NewCodes_AreAtTheEndOfTheEnum()
    {
        var values = Enum.GetValues<FindingCode>().ToList();
        var last = values.Count - 1;
        Assert.Equal(FindingCode.NetQualityDnsSlow, values[last - 1]);
        Assert.Equal(FindingCode.NetQualityDnsSlowEverywhere, values[last]);
    }

    [Fact]
    public void AllDnsText_FollowsTheWritingGuide_AndHasNoAddress()
    {
        const string ip = @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b";
        foreach (var s in Scenarios())
        {
            var f = ConnectionQualityTriage.Evaluate(s);
            var ladder = ConnectionQualityTriage.Ladder(s, f);
            Assert.Equal(ladder.Count, ladder.Select(r => r.Id).Distinct().Count());
            var texts = f.Select(x => x.PlainMessage).Concat(ladder.Select(r => r.Say)).Append(ConnectionQualityTriage.NotFixedHint(f) ?? "A.").ToList();
            foreach (var t in texts)
            {
                Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |'s |\bI\b|\bwe\b|!", t);
                Assert.Matches(@"^[A-Z]", t);
                Assert.EndsWith(".", t);
                foreach (var sentence in Regex.Split(t, @"(?<=\.)\s+").Where(x => x.Length > 0))
                    Assert.True(sentence.Split(' ').Length <= 35, sentence);
            }
            foreach (var t in f.SelectMany(x => new[] { x.PlainMessage, x.TechnicalDetail }).Concat(texts))
                Assert.DoesNotMatch(ip, t);
            Assert.DoesNotMatch(ip, JsonSerializer.Serialize(s));
        }
    }

    // ---- measurement with a fake network ----

    private sealed class FakeDns : IDnsQuerySender
    {
        private readonly Func<IPAddress, string, DnsSample> _reply;
        public int Calls;
        public readonly List<IPAddress> Servers = new();
        public FakeDns(Func<IPAddress, string, DnsSample> reply) => _reply = reply;
        public Task<DnsSample> QueryAsync(IPAddress server, string name, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Servers) { Calls++; Servers.Add(server); }
            return Task.FromResult(_reply(server, name));
        }
    }

    private static readonly IPAddress Router = IPAddress.Parse("192.0.2.1");

    [Fact]
    public async Task Measurement_TwoQueriesPerName_PerServer_AndMedianOfAll()
    {
        var fake = new FakeDns((ip, _) => new DnsSample(true, ip.Equals(Router) ? 200 : ip.Equals(RealConnectionQualityProbe.PublicA) ? 20 : 30));
        var m = await new DnsSpeedProbe(fake).MeasureAsync(new[] { Router });
        Assert.Equal(12, m.Current!.Sent);
        Assert.Equal(12, m.PublicA!.Sent);
        Assert.Equal(12, m.PublicB!.Sent);
        Assert.Equal(200, m.Current.MedianMs);
        Assert.Equal(20, m.PublicA.MedianMs);
        Assert.Equal(30, m.PublicB.MedianMs);
        Assert.Equal(36, fake.Calls);
        Assert.Equal(6, m.Names);
        Assert.DoesNotContain("192.0.2", JsonSerializer.Serialize(m));
        Assert.Equal(FindingCode.NetQualityDnsSlow, Code(m));
    }

    [Fact]
    public async Task Measurement_SeveralConfiguredServers_ArePooled()
    {
        var fake = new FakeDns((_, _) => new DnsSample(true, 10));
        var m = await new DnsSpeedProbe(fake).MeasureAsync(new[] { Router, IPAddress.Parse("192.0.2.2") });
        Assert.Equal(24, m.Current!.Sent);
        Assert.Equal(12, m.PublicA!.Sent);
    }

    [Fact]
    public async Task Measurement_NoConfiguredServer_LeavesCurrentNull()
    {
        var m = await new DnsSpeedProbe(new FakeDns((_, _) => new DnsSample(true, 10))).MeasureAsync(Array.Empty<IPAddress>());
        Assert.Null(m.Current);
        Assert.Equal(12, m.PublicA!.Sent);
    }

    [Fact]
    public async Task Measurement_ServerThatNeverAnswers_IsGivenUpOn_ButCountsAllAsFailed()
    {
        var fake = new FakeDns((ip, _) => ip.Equals(Router) ? new DnsSample(false, 0) : new DnsSample(true, 10));
        var m = await new DnsSpeedProbe(fake).MeasureAsync(new[] { Router });
        Assert.Equal(12, m.Current!.Sent);
        Assert.Equal(0, m.Current.Answered);
        Assert.Equal(100, m.Current.FailurePercent);
        Assert.Equal(DnsSpeedProbe.GiveUpAfterFailures, fake.Servers.Count(s => s.Equals(Router)));
    }

    [Fact]
    public async Task Measurement_ReportsOneActivityLine_WithoutAddresses()
    {
        var lines = new List<string>();
        void On(ActivityItem i) { lock (lines) lines.Add(i.Text); }
        Activity.Reported += On;
        try { await new DnsSpeedProbe(new FakeDns((_, _) => new DnsSample(true, 5))).MeasureAsync(new[] { Router }); }
        finally { Activity.Reported -= On; }
        Assert.Contains("Measuring name lookups: 6 names, 3 servers (no addresses are kept)", lines);
        Assert.All(lines.Where(l => l.Contains("lookup")), l => Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", l));
    }

    [Fact]
    public async Task Measurement_AlreadyCancelled_Throws_AndAsksNothing()
    {
        var fake = new FakeDns((_, _) => new DnsSample(true, 5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DnsSpeedProbe(fake).MeasureAsync(new[] { Router }, cts.Token));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Measurement_CancelledHalfWay_StopsEarly()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeDns((_, _) => { cts.Cancel(); return new DnsSample(true, 5); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DnsSpeedProbe(fake).MeasureAsync(new[] { Router }, cts.Token));
        Assert.True(fake.Calls < 36);
    }
}

/// <summary>One read-only live run (AFS_LIVE set): sends DNS questions only, writes numbers only to C:\afs-bench\out\live-dns.txt.</summary>
[Collection("Live machine")]
public class LiveDnsSpeedTests
{
    [Fact]
    public async Task RealMeasurement_WritesNumbersOnly()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        var configured = DnsSpeedProbe.ConfiguredServers();
        var m = await new DnsSpeedProbe().MeasureAsync(configured);
        var f = DnsQualityTriage.Evaluate(m);
        var lines = new List<string>
        {
            $"configured IPv4 DNS servers: {configured.Count}",
            DnsQualityTriage.Detail(m),
            "finding: " + (f == null ? "none" : f.Code + " - " + f.PlainMessage),
        };
        Directory.CreateDirectory(@"C:\afs-bench\out");
        File.WriteAllLines(@"C:\afs-bench\out\live-dns.txt", lines);
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", string.Join("\n", lines));
    }
}
