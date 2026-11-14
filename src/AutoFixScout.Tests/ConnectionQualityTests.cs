using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class ConnectionQualityTests
{
    private static PingSample Ok(double ms) => new(true, ms);
    private static readonly PingSample Lost = new(false, 0);

    // Statistics built from the numbers directly (60 pings = 15 s at 250 ms).
    private static PingStats Stats(double median, double jitter, double loss, int sent = 60) =>
        new(sent, (int)Math.Round(sent * (1 - loss / 100)), median, jitter, loss);

    private static PingStats Good => Stats(3, 1, 0);
    private static PingStats Net => Stats(25, 3, 0);
    private static readonly PingStats Silent = new(60, 0, null, null, 100);

    private static QualityState State(PingStats? gateway, PingStats? a, PingStats? b, bool wifi = true, int? signal = 70) => new()
    {
        GatewayFound = true, Gateway = gateway, InternetA = a, InternetB = b, OnWifi = wifi, WifiSignalPercent = signal,
        Supports5GHz = Tri.Yes, LinkSpeedMbps = 866, DurationSeconds = 15.1,
    };

    private static FindingCode Code(QualityState s) => ConnectionQualityTriage.Evaluate(s)[0].Code;

    // ---- statistics ----

    [Fact]
    public void Median_OddEvenEmpty()
    {
        Assert.Equal(5, ConnectionStatistics.Median(new double[] { 9, 1, 5 }));
        Assert.Equal(6, ConnectionStatistics.Median(new double[] { 10, 2, 4, 8 }));
        Assert.Null(ConnectionStatistics.Median(Array.Empty<double>()));
    }

    [Fact]
    public void Jitter_IsMeanAbsoluteDifferenceOfConsecutiveReplies()
    {
        Assert.Equal((4 + 3 + 0) / 3.0, ConnectionStatistics.Jitter(new[] { Ok(10), Ok(14), Ok(11), Ok(11) }));
        Assert.Equal(0, ConnectionStatistics.Jitter(new[] { Ok(7), Ok(7), Ok(7) }));
    }

    [Fact]
    public void Jitter_LostSampleBreaksTheChain()
    {
        Assert.Equal(2, ConnectionStatistics.Jitter(new[] { Ok(10), Lost, Ok(50), Ok(52) }));
        Assert.Null(ConnectionStatistics.Jitter(new[] { Ok(10), Lost, Ok(50) }));
        Assert.Null(ConnectionStatistics.Jitter(new[] { Ok(10) }));
    }

    [Fact]
    public void Stats_FromSamples_LossMedianJitter()
    {
        var samples = new List<PingSample>();
        for (var i = 0; i < 100; i++) samples.Add(i % 10 == 9 ? Lost : Ok(i % 2 == 0 ? 20 : 22));
        var st = PingStats.From(samples);
        Assert.Equal(100, st.Sent);
        Assert.Equal(90, st.Received);
        Assert.Equal(10, st.LossPercent, 6);
        Assert.Equal(20, st.MedianMs!.Value, 6); // 50 replies of 20 and 40 of 22 (the lost ones were all 22)
    }

    [Fact]
    public void Stats_NothingAnswers()
    {
        var st = PingStats.From(Enumerable.Repeat(Lost, 40).ToList());
        Assert.False(st.Answers);
        Assert.Equal(100, st.LossPercent);
        Assert.Null(st.MedianMs);
        Assert.Null(st.JitterMs);
    }

    // ---- gateway thresholds (both sides) ----

    [Theory]
    [InlineData(0, 1, 5, false)]
    [InlineData(2.0, 1, 5, false)]
    [InlineData(2.1, 1, 5, true)]
    [InlineData(0, 15, 5, false)]
    [InlineData(0, 15.1, 5, true)]
    [InlineData(0, 1, 20, false)]
    [InlineData(0, 1, 20.5, true)]
    public void GatewayThresholds_OnWifi(double loss, double jitter, double median, bool bad) =>
        Assert.Equal(bad, ConnectionQualityTriage.GatewayBad(Stats(median, jitter, loss), onWifi: true));

    [Fact]
    public void GatewayDelay_OnlyCountsOnWifi() =>
        Assert.False(ConnectionQualityTriage.GatewayBad(Stats(60, 1, 0), onWifi: false));

    [Theory]
    [InlineData(0, 5, 20, false)]
    [InlineData(2.0, 5, 20, false)]
    [InlineData(2.1, 5, 20, true)]
    [InlineData(0, 5, 80, false)]
    [InlineData(0, 5, 80.5, true)]
    [InlineData(0, 30, 20, false)]
    [InlineData(0, 30.5, 20, true)]
    public void InternetThresholds(double loss, double jitter, double median, bool bad) =>
        Assert.Equal(bad, ConnectionQualityTriage.InternetBad(Stats(median, jitter, loss)));

    // ---- verdicts ----

    [Fact]
    public void BadGateway_IsTheLocalNetwork_EvenWhenTheInternetIsAlsoBad() =>
        Assert.Equal(FindingCode.NetQualityLocalNetwork, Code(State(Stats(8, 2, 6), Stats(200, 50, 10), Stats(200, 50, 10))));

    [Fact]
    public void GoodGateway_BadInternet_IsTheProvider() =>
        Assert.Equal(FindingCode.NetQualityInternet, Code(State(Good, Stats(120, 5, 0), Stats(110, 5, 0))));

    [Fact]
    public void OneInternetServerBad_OtherFine_IsNotAFault() =>
        Assert.Equal(FindingCode.NetQualitySteady, Code(State(Good, Stats(120, 50, 9), Net)));

    [Fact]
    public void AllGood_IsSteady()
    {
        var s = State(Good, Net, Stats(30, 4, 0));
        var f = ConnectionQualityTriage.Evaluate(s);
        Assert.Equal(FindingCode.NetQualitySteady, f[0].Code);
        Assert.Contains("steady", f[0].PlainMessage);
        Assert.Empty(ConnectionQualityTriage.Ladder(s, f));
    }

    [Fact]
    public void GatewayBlocksPing_IsUnknown_NotAFault()
    {
        var f = ConnectionQualityTriage.Evaluate(State(Silent, Net, Net));
        Assert.Equal(FindingCode.NetQualitySteady, f[0].Code);
        Assert.Contains("does not answer", f[0].PlainMessage);
        Assert.DoesNotContain("100", f[0].PlainMessage);
        Assert.Equal(FindingCode.NetQualitySteady, Code(State(null, Net, Net)));
    }

    [Fact]
    public void GatewayBlocksPing_InternetBad_SaysItCannotTell()
    {
        var f = ConnectionQualityTriage.Evaluate(State(Silent, Stats(150, 5, 0), Stats(140, 5, 0)));
        Assert.Equal(FindingCode.NetQualityInternetUnclear, f[0].Code);
        Assert.Contains("cannot be said", f[0].PlainMessage);
    }

    [Fact]
    public void NothingAnswers_IsNotMeasurable_NotAFault()
    {
        Assert.Equal(FindingCode.NetQualityNotMeasurable, Code(State(Silent, Silent, Silent)));
        Assert.Equal(FindingCode.NetQualityNotMeasurable, Code(State(Good, Silent, Silent)));
    }

    [Fact]
    public void StoppedEarlyOrMissing_IsIncomplete()
    {
        Assert.Equal(FindingCode.ProbeIncomplete, Code(State(Good, Stats(3, 1, 0, sent: 5), Net)));
        Assert.Equal(FindingCode.ProbeIncomplete, Code(State(Good, null, null)));
    }

    // ---- wording ----

    [Fact]
    public void LocalMessage_SaysHowManyPacketsWereLost()
    {
        var f = ConnectionQualityTriage.Evaluate(State(Stats(8, 2, 6), Net, Net));
        Assert.Contains("About 6 of every 100 packets were lost between this computer and the router.", f[0].PlainMessage);
        Assert.Contains("loss 6 %", f[0].TechnicalDetail);
        Assert.Contains("median 8 ms", f[0].TechnicalDetail);
    }

    [Fact]
    public void LocalMessage_WeakSignalAndSlowRouter()
    {
        var f = ConnectionQualityTriage.Evaluate(State(Stats(45, 3, 0), Net, Net, signal: 25));
        Assert.Contains("took about 45 ms", f[0].PlainMessage);
        Assert.Contains("weak (25 percent)", f[0].PlainMessage);
    }

    [Fact]
    public void Wired_GetsCableAdvice_NotWifiAdvice()
    {
        var s = State(Stats(2, 40, 0), Net, Net, wifi: false);
        var f = ConnectionQualityTriage.Evaluate(s);
        var ids = ConnectionQualityTriage.Ladder(s, f).Select(r => r.Id).ToList();
        Assert.Contains("check_cable_quality", ids);
        Assert.DoesNotContain("move_closer", ids);
        Assert.DoesNotContain("Wi-Fi", f[0].PlainMessage);
    }

    [Fact]
    public void WifiLadder_ReusesWifiRungs_And5GHzOnlyWhenSupported()
    {
        var s = State(Stats(8, 2, 6), Net, Net);
        var ids = ConnectionQualityTriage.Ladder(s, ConnectionQualityTriage.Evaluate(s)).Select(r => r.Id).ToList();
        Assert.Equal(new[] { "move_closer", "use_5ghz", "pause_traffic", "restart_router" }, ids);
        s.Supports5GHz = Tri.No;
        Assert.DoesNotContain("use_5ghz", ConnectionQualityTriage.Ladder(s, ConnectionQualityTriage.Evaluate(s)).Select(r => r.Id));
    }

    [Fact]
    public void InternetLadder_RestartsRouterAndGivesTheNumbersToTheProvider()
    {
        var s = State(Good, Stats(120, 5, 4), Stats(130, 6, 5));
        var f = ConnectionQualityTriage.Evaluate(s);
        var ladder = ConnectionQualityTriage.Ladder(s, f);
        Assert.Equal(new[] { "pause_traffic", "restart_router", "contact_provider" }, ladder.Select(r => r.Id));
        Assert.Contains("4 of every 100 packets", ladder[2].Say);
        Assert.Contains("120 ms", ladder[2].Say);
        Assert.NotNull(ConnectionQualityTriage.NotFixedHint(f));
    }

    private static IEnumerable<QualityState> Scenarios()
    {
        yield return State(Good, Net, Net);
        yield return State(Silent, Net, Net);
        yield return State(null, Net, Net);
        yield return State(Stats(8, 2, 6), Net, Net);
        yield return State(Stats(45, 20, 0), Net, Net, signal: 20);
        yield return State(Stats(2, 40, 3), Net, Net, wifi: false);
        yield return State(Good, Stats(150, 40, 6), Stats(140, 35, 7));
        yield return State(Silent, Stats(150, 40, 6), Stats(140, 35, 7));
        yield return State(Silent, Silent, Silent);
        yield return State(Good, Silent, Silent);
        yield return State(Good, null, null);
    }

    [Fact]
    public void AllText_FollowsTheWritingGuide_AndRungIdsAreUnique()
    {
        foreach (var s in Scenarios())
        {
            var f = ConnectionQualityTriage.Evaluate(s);
            var ladder = ConnectionQualityTriage.Ladder(s, f);
            Assert.Equal(ladder.Count, ladder.Select(r => r.Id).Distinct().Count());
            var texts = f.Select(x => x.PlainMessage).Concat(ladder.Select(r => r.Say)).Append(ConnectionQualityTriage.NotFixedHint(f) ?? "A.");
            foreach (var t in texts)
            {
                Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |'s |\bI\b|\bI'm\b|\bwe\b|!", t);
                Assert.Matches(@"^[A-Z]", t);
                Assert.EndsWith(".", t);
                foreach (var sentence in Regex.Split(t, @"(?<=\.)\s+").Where(x => x.Length > 0))
                    Assert.True(sentence.Split(' ').Length <= 35, sentence);
            }
        }
    }

    [Fact]
    public void NoAddress_InAnyTextOrState()
    {
        const string ip = @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b";
        foreach (var s in Scenarios())
        {
            var f = ConnectionQualityTriage.Evaluate(s);
            foreach (var t in f.SelectMany(x => new[] { x.PlainMessage, x.TechnicalDetail })
                .Concat(ConnectionQualityTriage.Ladder(s, f).Select(r => r.Say)))
                Assert.DoesNotMatch(ip, t);
            Assert.DoesNotMatch(ip, JsonSerializer.Serialize(s));
        }
    }

    // ---- measurement with a fake network ----

    private sealed class FakePinger : IPingSender
    {
        private readonly Func<IPAddress, PingSample> _reply;
        public FakePinger(Func<IPAddress, PingSample> reply) => _reply = reply;
        public Task<PingSample> SendAsync(IPAddress target, int timeoutMs, CancellationToken ct) => Task.FromResult(_reply(target));
    }

    [Fact]
    public async Task Measurement_PerTargetStatistics_AndNoAddressInTheResult()
    {
        var gw = IPAddress.Parse("192.0.2.7");
        var p = new RealConnectionQualityProbe(new FakePinger(ip =>
            ip.Equals(RealConnectionQualityProbe.PublicA) ? Ok(20)
            : ip.Equals(RealConnectionQualityProbe.PublicB) ? Lost
            : Ok(3)));
        var r = await p.MeasureAsync(new IPAddress?[] { gw, RealConnectionQualityProbe.PublicA, RealConnectionQualityProbe.PublicB },
            TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20), default);
        Assert.Equal(3, r[0].MedianMs);
        Assert.Equal(20, r[1].MedianMs);
        Assert.Equal(100, r[2].LossPercent);
        Assert.Equal(20, r[0].Sent);
        Assert.DoesNotContain("192.0.2", JsonSerializer.Serialize(r));
    }

    [Fact]
    public async Task Measurement_Cancel_Throws()
    {
        var p = new RealConnectionQualityProbe(new FakePinger(_ => Ok(1)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            p.MeasureAsync(new IPAddress?[] { RealConnectionQualityProbe.PublicA }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), cts.Token));
    }

    // ---- routing ----

    [Theory]
    [InlineData("internet slow", "netquality")]
    [InlineData("slow internet at night", "netquality")]
    [InlineData("I lag online in every game", "netquality")]
    [InlineData("ping high in valorant", "netquality")]
    [InlineData("packet loss", "netquality")]
    [InlineData("connection unstable", "netquality")]
    [InlineData("youtube is buffering", "netquality")]
    public void SymptomWords_RouteToTheConnectionQualityCheck(string text, string area) => Assert.Equal(area, SymptomRouter.Best(text).Area);

    [Fact]
    public void FlowConstants_AreFilledIn()
    {
        Assert.Equal("netquality", ConnectionQualityFlow.Topic);
        Assert.Equal("connection quality", ConnectionQualityFlow.What);
        Assert.EndsWith("?", ConnectionQualityFlow.TestQuestion);
        Assert.True(SymptomRouter.Covers.ContainsKey("netquality"));
    }
}

/// <summary>Runs the real 15 second measurement on this machine (AFS_LIVE set); writes numbers and findings, never addresses, to that file.</summary>
[Collection("Live machine")]
public class LiveConnectionQualityTests
{
    [Fact]
    public async Task RealMeasurement_WritesNumbersAndFindings()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = await ConnectionQualityFlow.DiagnoseAsync();
        var s = (QualityState)d.State;
        static string P(string n, PingStats? p) => p == null ? $"{n}: not measured"
            : $"{n}: sent {p.Sent} received {p.Received} median {p.MedianMs:0.0} ms jitter {p.JitterMs:0.00} ms loss {p.LossPercent:0.0} %";
        var lines = new List<string>
        {
            $"took {sw.Elapsed.TotalSeconds:0.0} s (test length {s.DurationSeconds} s)",
            $"gateway found {s.GatewayFound}; wifi {s.OnWifi}; signal {s.WifiSignalPercent}; 5GHz {s.Supports5GHz}; link {s.LinkSpeedMbps} Mbps",
            P("router", s.Gateway), P("internet 1", s.InternetA), P("internet 2", s.InternetB), "errors: " + string.Join(" | ", s.ProbeErrors),
        };
        lines.AddRange(d.Findings.Select(x => $"FINDING {x.Code}: {x.PlainMessage} [{x.TechnicalDetail}]"));
        lines.AddRange(d.Ladder.Select(r => $"RUNG {r.Id}: {r.Say}"));
        File.WriteAllLines(output, lines);
        Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", string.Join("\n", lines));
        Assert.DoesNotContain(s.ProbeErrors, e => e.StartsWith("gateway"));
        Assert.InRange(s.DurationSeconds, 13, 20);
        Assert.Equal(60, s.InternetA!.Sent);
    }
}

public class ResultKindTests
{
    [Theory]
    [InlineData(AutoFixScout.Triage.FindingCode.LooksNormal, true, false)]
    [InlineData(AutoFixScout.Triage.FindingCode.NetQualitySteady, true, false)]
    [InlineData(AutoFixScout.Triage.FindingCode.NetQualityNotMeasurable, false, true)]
    [InlineData(AutoFixScout.Triage.FindingCode.PowerBatteryAgeing, false, true)]
    [InlineData(AutoFixScout.Triage.FindingCode.NetQualityInternet, false, false)]
    public void AllClear_And_Information(AutoFixScout.Triage.FindingCode code, bool clear, bool info)
    {
        Xunit.Assert.Equal(clear, AutoFixScout.Triage.LadderRunner.IsAllClear(code));
        Xunit.Assert.Equal(info, AutoFixScout.Triage.LadderRunner.IsInformation(code));
    }
}
