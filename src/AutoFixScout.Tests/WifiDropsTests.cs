using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class WifiDropsTests
{
    private static readonly NetAdapterInfo Wifi = new("Intel Wi-Fi 6 AX201", @"PCI\VEN_8086&DEV_A0F0\3&1", true, true, 0);

    private static string Xml(int id, int? code, string? reason) =>
        $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>{id}</EventID></System><EventData>" +
        "<Data Name=\"SSID\">SecretNet</Data><Data Name=\"ProfileName\">SecretNet</Data>" +
        (reason != null ? $"<Data Name=\"Reason\">{reason}</Data>" : "") +
        (code != null ? $"<Data Name=\"ReasonCode\">{code}</Data>" : "") + "</EventData></Event>";

    internal static NetworkState Working(int unintentional, int person = 0, Tri power = Tri.Unknown) => new()
    {
        Adapters = new[] { Wifi }, AirplaneMode = Tri.No,
        GatewayReachable = Tri.Yes, DnsResolving = Tri.Yes, InternetReachable = Tri.Yes,
        WifiDrops = new WifiDropStats(unintentional + person, 10, person, 0, unintentional, 0),
        WifiPowerSavingMayTurnOff = power,
    };

    [Fact]
    public void ParseEvent_ReadsIdCodeAndReason_Only()
    {
        var (id, code, reason) = WifiDrops.ParseEvent(Xml(8003, 13, "The network is disconnected due to a temporary-disconnect request."));
        Assert.Equal((8003, 13), (id, code));
        Assert.DoesNotContain("SecretNet", reason);
        Assert.Equal(-1, WifiDrops.ParseEvent(Xml(8001, null, null)).ReasonCode);
    }

    [Theory]
    [InlineData(2, "The network is disconnected by the user.", WifiDrops.Category.Person)]
    [InlineData(3, null, WifiDrops.Category.Person)]
    [InlineData(0, "The network is disconnected by the driver.", WifiDrops.Category.Driver)]
    [InlineData(13, "The network is disconnected due to a temporary-disconnect request.", WifiDrops.Category.Other)]
    [InlineData(99, "The signal was lost.", WifiDrops.Category.SignalLost)]
    [InlineData(99, "link quality dropped", WifiDrops.Category.SignalLost)]
    [InlineData(11, null, WifiDrops.Category.Other)]
    public void Categorize_ByCodeAndText(int code, string? text, WifiDrops.Category expected) =>
        Assert.Equal(expected, WifiDrops.Categorize(code, text));

    [Fact]
    public void Count_SplitsConnectsAndDisconnects()
    {
        var events = new (int, int, string?)[]
        {
            (8001, -1, null), (8001, -1, null), (8003, 2, null), (8003, 0, "by the driver"), (8003, 0, "by the driver"),
            (8003, 13, "temporary"), (8003, 5, "signal lost"), (8002, 1, null),
        };
        var c = WifiDrops.Count(events);
        Assert.Equal(new WifiDropStats(5, 2, 1, 1, 2, 1), c);
        Assert.Equal(4, c.Unintentional);
    }

    [Theory]
    [InlineData(null, Tri.Yes)]
    [InlineData(0, Tri.Yes)]
    [InlineData(8, Tri.No)]
    [InlineData(16, Tri.No)]
    [InlineData(24, Tri.No)]
    [InlineData(1, Tri.Yes)]
    [InlineData("x", Tri.Unknown)]
    public void PnPCapabilities_Decoding(object? value, Tri expected) => Assert.Equal(expected, WifiDrops.MayTurnOffToSavePower(value));

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(40, true)]
    public void Triage_ThresholdIsSixUnintentional(int n, bool raised) =>
        Assert.Equal(raised, NetworkTriage.Evaluate(Working(n)).Any(f => f.Code == FindingCode.NetWifiDrops));

    [Fact]
    public void Triage_DisconnectsByThePersonDoNotCount()
    {
        Assert.DoesNotContain(NetworkTriage.Evaluate(Working(2, person: 20)), f => f.Code == FindingCode.NetWifiDrops);
        var none = new NetworkState { Adapters = new[] { Wifi }, AirplaneMode = Tri.No, GatewayReachable = Tri.Yes, DnsResolving = Tri.Yes, InternetReachable = Tri.Yes };
        Assert.DoesNotContain(NetworkTriage.Evaluate(none), f => f.Code == FindingCode.NetWifiDrops);
    }

    [Fact]
    public void Triage_ConnectionDownStaysFirst_AndDetailHasCounts()
    {
        var s = Working(8, power: Tri.Yes);
        s.InternetReachable = Tri.No; s.GatewayReachable = Tri.No; s.DnsResolving = Tri.No;
        var f = NetworkTriage.Evaluate(s);
        Assert.Equal(FindingCode.GatewayUnreachable, f[0].Code);
        var drops = f.Single(x => x.Code == FindingCode.NetWifiDrops);
        Assert.Contains("8 disconnects", drops.TechnicalDetail);
        Assert.Contains("Yes", drops.TechnicalDetail);
    }

    [Fact]
    public void Ladder_PowerStepOnlyWhenPowerSavingMayApply_AndIdsUnique()
    {
        var s = Working(8, power: Tri.Yes);
        var on = NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s)).ToArray();
        Assert.Equal(new[] { "wifi_power_saving", "wifi_distance_band", "wifi_driver_update", "restart_router" }, on.Select(r => r.Id));
        Assert.Contains("administrator", on[0].Say);
        var off = Working(8, power: Tri.No);
        Assert.DoesNotContain(NetworkTriage.Ladder(off, NetworkTriage.Evaluate(off)), r => r.Id == "wifi_power_saving");
        var all = Working(8, power: Tri.Unknown); all.WifiSignalPercent = 10; all.Metered = Tri.Yes; all.Supports5GHz = Tri.Yes; all.IpConflictRecent = true;
        var l = NetworkTriage.Ladder(all, NetworkTriage.Evaluate(all));
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Texts_FollowTheWritingGuide()
    {
        var s = Working(8, power: Tri.Yes);
        var f = NetworkTriage.Evaluate(s);
        var texts = f.Select(x => x.PlainMessage).Concat(NetworkTriage.Ladder(s, f).Select(r => r.Say)).ToList();
        foreach (var t in texts)
            Assert.DoesNotMatch(new Regex(@"n't|'ll |'re |'ve |'d |\bI\b|!"), t);
    }
}

/// <summary>Read-only. Runs only when AFS_LIVE is set. Writes counts and codes only, never a network name.</summary>
[Collection("Live machine")]
public class LiveWifiDropsTests
{
    [Fact]
    public async Task WifiDrops_ReadWithoutErrors()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = await new RealNetworkProbe().ReadAsync();
        var f = NetworkTriage.Evaluate(s);
        var rungs = NetworkTriage.Ladder(s, f);
        var lines = new List<string>
        {
            "errors: " + string.Join(" | ", s.ProbeErrors), "extra errors: " + string.Join(" | ", s.ExtraProbeErrors),
            "drops: " + (s.WifiDrops?.Describe() ?? "not read"), "power saving may turn adapter off: " + s.WifiPowerSavingMayTurnOff,
        };
        lines.AddRange(f.Select(x => $"FINDING {x.Code}"));
        lines.AddRange(rungs.Select(r => $"RUNG {r.Id} {r.ActionId}"));
        File.WriteAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
        Assert.Empty(s.ExtraProbeErrors);
        if (s.Adapters.Any(a => a.IsWireless))
        {
            Assert.NotNull(s.WifiDrops);
            Assert.True(s.WifiDrops!.Disconnects >= s.WifiDrops.ByPerson);
        }
        // No finding or rung text may contain the network name.
        var name = WifiNameForTest();
        if (!string.IsNullOrEmpty(name))
        {
            var all = f.SelectMany(x => new[] { x.PlainMessage, x.TechnicalDetail }).Concat(rungs.Select(r => r.Say));
            Assert.DoesNotContain(all, t => t.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The current network name, held in memory only to prove it never reaches the output.</summary>
    private static string? WifiNameForTest()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        var m = Regex.Match(text, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }
}

public class WifiSleepTests
{
    [Fact]
    public void SleepsAndShutdowns_AreNotCountedAsDrops()
    {
        var s = new AutoFixScout.Probes.WifiDropStats(Disconnects: 8, Connects: 8, ByPerson: 1, SignalLost: 0, Driver: 7, Other: 0, SleepsAndShutdowns: 5);
        Assert.Equal(2, s.Unintentional);
        Assert.Equal(0, (s with { SleepsAndShutdowns = 20 }).Unintentional);
    }
}

public class WifiSignInTests
{
    private static AutoFixScout.Probes.NetworkState WithDrops(int signInFailures)
    {
        var s = WifiDropsTests.Working(12);
        s.WifiDrops = s.WifiDrops! with { SignInFailures = signInFailures };
        return s;
    }

    // Regression from the bench: enterprise Wi-Fi without a saved sign-in turned short drops into long outages.
    [Fact]
    public void SignInFailures_PutTheSaveSignInStepFirst()
    {
        var s = WithDrops(3);
        var f = AutoFixScout.Triage.NetworkTriage.Evaluate(s);
        var drop = Assert.Single(f, x => x.Code == AutoFixScout.Triage.FindingCode.NetWifiDrops);
        Assert.Contains("sign in", drop.PlainMessage);
        var ladder = AutoFixScout.Triage.NetworkTriage.Ladder(s, f).Select(r => r.Id).ToList();
        Assert.True(ladder.IndexOf("wifi_save_signin") >= 0 && ladder.IndexOf("wifi_save_signin") < ladder.IndexOf("wifi_driver_update"));
    }

    [Fact]
    public void NoSignInFailures_NoSaveSignInStep() =>
        Assert.DoesNotContain(AutoFixScout.Triage.NetworkTriage.Ladder(WithDrops(0), AutoFixScout.Triage.NetworkTriage.Evaluate(WithDrops(0))), r => r.Id == "wifi_save_signin");
}
