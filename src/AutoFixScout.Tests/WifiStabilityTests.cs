using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

/// <summary>The bench case (2026-11-13): old Intel AX201 driver on university WPA2-Enterprise Wi-Fi, no saved sign-in.</summary>
public class WifiStabilityTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> IntelEnums = new Dictionary<string, IReadOnlyCollection<string>>
    {
        ["IEEE11nMode"] = new[] { "0", "1", "2", "3" }, ["RoamAggressiveness"] = new[] { "0", "1", "2", "3", "4" },
        ["MIMOPowerSaveMode"] = new[] { "0", "1", "2", "3" }, ["RoamingPreferredBandType"] = new[] { "0", "1", "2" },
    };

    private static NetworkState BenchCase()
    {
        var s = WifiDropsTests.Working(24);
        s.WifiDrops = s.WifiDrops! with { SignInFailures = 6 };
        s.WifiEnterprise = Tri.Yes;
        s.WifiDriverDate = new DateTime(2021, 6, 29);
        s.WifiDriverVersion = "22.70.0.6";
        s.WifiInstanceId = @"PCI\VEN_8086&DEV_06F0&SUBSYS_00748086&REV_00\4&1";
        s.WifiTuningPending = WifiTuning.Plan(new Dictionary<string, string> { ["IEEE11nMode"] = "3", ["RoamAggressiveness"] = "2", ["MIMOPowerSaveMode"] = "0", ["RoamingPreferredBandType"] = "0" }, IntelEnums)
            .Select(p => WifiTuning.Targets.First(t => t.Keyword == p.Keyword).Meaning).ToList();
        return s;
    }

    [Fact]
    public void Plan_ChangesOnlySupportedDifferentValues()
    {
        var plan = WifiTuning.Plan(new Dictionary<string, string> { ["IEEE11nMode"] = "3", ["RoamAggressiveness"] = "1", ["MIMOPowerSaveMode"] = "0" }, IntelEnums);
        Assert.Equal(new[] { ("IEEE11nMode", "3", "2"), ("MIMOPowerSaveMode", "0", "3") }, plan);
        Assert.Empty(WifiTuning.Plan(new Dictionary<string, string> { ["IEEE11nMode"] = "3" }, new Dictionary<string, IReadOnlyCollection<string>> { ["IEEE11nMode"] = new[] { "0", "1" } }));
        Assert.Empty(WifiTuning.Plan(new Dictionary<string, string>(), IntelEnums)); // not this kind of adapter
    }

    [Theory]
    [InlineData("6-29-2021", 2021, 6, 29)]
    [InlineData("2021-06-29", 2021, 6, 29)]
    public void DriverDate_Parses(string text, int y, int m, int d) => Assert.Equal(new DateTime(y, m, d), WifiTuning.ParseDriverDate(text));

    [Theory]
    [InlineData("    Authentication         : WPA2-Enterprise", Tri.Yes)]
    [InlineData("    Authentication         : WPA3-Personal", Tri.No)]
    [InlineData("", Tri.Unknown)]
    public void Enterprise_IsDetected(string netsh, Tri expected) => Assert.Equal(expected, NetworkChecks.IsEnterprise(netsh));

    [Fact]
    public void BenchCase_SignInThenTuningThenDriver_NoRouterOnCampus()
    {
        var s = BenchCase();
        var f = NetworkTriage.Evaluate(s);
        var drop = Assert.Single(f, x => x.Code == FindingCode.NetWifiDrops);
        Assert.Contains("June 2021", drop.PlainMessage);
        var l = NetworkTriage.Ladder(s, f).Select(r => r.Id).ToList();
        Assert.True(l.IndexOf("wifi_save_signin") < l.IndexOf("wifi_stability"));
        Assert.True(l.IndexOf("wifi_stability") < l.IndexOf("wifi_old_driver"));
        Assert.DoesNotContain("restart_router", l);
        Assert.Contains("wifi_enterprise_support", l);
        var tune = NetworkTriage.Ladder(s, f).First(r => r.Id == "wifi_stability");
        Assert.Equal(s.WifiInstanceId, tune.Arg);
        Assert.Contains("Windows will ask for your permission", tune.Say);
        Assert.Contains("802.11ac", tune.Say);
    }

    [Fact]
    public void HomeWifi_NewDriver_AlreadyTuned_NoTuningStep()
    {
        var s = WifiDropsTests.Working(12);
        s.WifiEnterprise = Tri.No; s.WifiDriverDate = DateTime.Now.AddMonths(-3);
        var l = NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s)).Select(r => r.Id).ToList();
        Assert.DoesNotContain("wifi_stability", l);
        Assert.DoesNotContain("wifi_old_driver", l);
        Assert.Contains("restart_router", l);
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide_AndIdsAreUnique()
    {
        var s = BenchCase();
        var f = NetworkTriage.Evaluate(s);
        var l = NetworkTriage.Ladder(s, f);
        foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)))
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        Assert.Contains("network.wifi_stability", FixActions.ElevatedActions);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"PCI\VEN_8086 & calc")]
    [InlineData(@"ROOT\NOTANADAPTER\0000")]
    public void Executor_RejectsAnythingButAnInstalledNetworkAdapter(string arg) =>
        Assert.Equal(2, ExecutorProgram.Main(new[] { "network.wifi_stability", arg }));
}

[Collection("Live machine")] // read-only
public class LiveWifiStabilityTests
{
    [Fact]
    public async Task ReadsDriverEnterpriseAndTuning_WithoutErrors()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = await new RealNetworkProbe().ReadAsync();
        Assert.DoesNotContain(s.ExtraProbeErrors, e => e.StartsWith("Wi-Fi"));
        var f = NetworkTriage.Evaluate(s);
        var l = NetworkTriage.Ladder(s, f);
        File.WriteAllLines(output, new[]
        {
            $"driver {s.WifiDriverVersion} {s.WifiDriverDate:yyyy-MM-dd} old={NetworkTriage.OldWifiDriver(s)} enterprise={s.WifiEnterprise} tuningPending=[{string.Join("; ", s.WifiTuningPending)}] instance={(s.WifiInstanceId is null ? "none" : "found")}",
            $"drops {s.WifiDrops?.Describe()} unintentional={s.WifiDrops?.Unintentional} signInFailures={s.WifiDrops?.SignInFailures}",
        }.Concat(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage}")).Concat(l.Select(r => $"RUNG {r.Id} {r.ActionId}")));
    }
}
