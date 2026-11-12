using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class NetworkExtrasTests
{
    private static readonly NetAdapterInfo Wifi = new("Intel Wi-Fi 6 AX201", @"PCI\VEN_8086&DEV_A0F0\3&1", true, true, 0);

    private static NetworkState Working() => new()
    {
        Adapters = new[] { Wifi }, AirplaneMode = Tri.No,
        GatewayReachable = Tri.Yes, DnsResolving = Tri.Yes, InternetReachable = Tri.Yes,
    };

    private static NetworkState Down() => new()
    {
        Adapters = new[] { Wifi }, AirplaneMode = Tri.No,
        GatewayReachable = Tri.No, DnsResolving = Tri.No, InternetReachable = Tri.No,
    };

    private static FindingCode[] Codes(NetworkState s) => NetworkTriage.Evaluate(s).Select(f => f.Code).ToArray();
    private static Rung[] Ladder(NetworkState s) => NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s)).ToArray();

    // ---- parsing helpers ----

    [Theory]
    [InlineData("169.254.12.7", true)]
    [InlineData("169.254.0.1", true)]
    [InlineData("169.253.1.1", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("fe80::1", false)]
    [InlineData("not an address", false)]
    public void IsApipa_Parses(string ip, bool apipa) => Assert.Equal(apipa, NetworkChecks.IsApipa(ip));

    [Fact]
    public void AddressObtained_Cases()
    {
        Assert.Equal(Tri.No, NetworkChecks.AddressObtained(new[] { "169.254.3.4" }, true));
        Assert.Equal(Tri.No, NetworkChecks.AddressObtained(new[] { "192.168.1.5" }, false));
        Assert.Equal(Tri.Yes, NetworkChecks.AddressObtained(new[] { "192.168.1.5" }, true));
        Assert.Equal(Tri.Yes, NetworkChecks.AddressObtained(new[] { "169.254.3.4", "192.168.1.5" }, true));
    }

    [Theory]
    [InlineData(4, Tri.Yes)]
    [InlineData(8, Tri.Yes)]
    [InlineData(10, Tri.Yes)]
    [InlineData(7, Tri.Unknown)]
    [InlineData(6, Tri.Unknown)]
    public void Supports5GHz_ByPhyType(int phy, Tri expected) => Assert.Equal(expected, NetworkChecks.Supports5GHz(phy));

    [Theory]
    [InlineData(1u, Tri.No)]
    [InlineData(2u, Tri.Yes)]
    [InlineData(4u, Tri.Yes)]
    [InlineData(0x20001u, Tri.No)]
    [InlineData(0x40002u, Tri.Yes)]
    public void IsMetered_ByCostFlags(uint cost, Tri expected) => Assert.Equal(expected, NetworkChecks.IsMetered(cost));

    [Theory]
    [InlineData(1, Tri.No)]
    [InlineData(2, Tri.Yes)]
    [InlineData(3, Tri.Yes)]
    [InlineData(null, Tri.Unknown)]
    [InlineData("x", Tri.Unknown)]
    public void IsMeteredRegistryValue_Maps(object? v, Tri expected) => Assert.Equal(expected, NetworkChecks.IsMeteredRegistryValue(v));

    [Fact]
    public void ConflictQuery_TargetsTcpip4199Last24Hours()
    {
        Assert.Contains("Tcpip", NetworkChecks.ConflictQuery);
        Assert.Contains("4199", NetworkChecks.ConflictQuery);
        Assert.Contains("86400000", NetworkChecks.ConflictQuery);
    }

    // ---- triage ----

    [Fact]
    public void NoExtras_NothingChanges()
    {
        Assert.Equal(new[] { FindingCode.LooksNormal }, Codes(Working()));
        Assert.Equal(new[] { FindingCode.GatewayUnreachable }, Codes(Down()));
    }

    [Fact]
    public void NoAddress_ReplacesGatewayUnreachable_WithRenewLadder()
    {
        var s = Down(); s.AddressObtained = Tri.No;
        Assert.Equal(new[] { FindingCode.NetNoAddress }, Codes(s));
        var l = Ladder(s);
        Assert.Equal(new[] { "renew_address", "toggle_adapter", "restart_router" }, l.Select(r => r.Id));
        Assert.Equal(("network.renew_address", Wifi.InstanceId), (l[0].ActionId, l[0].Arg));
    }

    [Fact]
    public void NoAddress_BeforeWeakSignal_AndMainFindingStaysFirst()
    {
        var s = Working(); s.AddressObtained = Tri.No; s.WifiSignalPercent = 20; s.IpConflictRecent = true; s.Metered = Tri.Yes;
        Assert.Equal(new[] { FindingCode.LooksNormal, FindingCode.NetNoAddress, FindingCode.NetIpConflict, FindingCode.NetWeakWifi, FindingCode.NetMetered }, Codes(s));
        var down = Down(); down.WifiSignalPercent = 20; down.AddressObtained = Tri.No;
        Assert.Equal(new[] { FindingCode.NetNoAddress, FindingCode.NetWeakWifi }, Codes(down));
    }

    [Fact]
    public void BiggerReasons_StayTheOnlyFinding()
    {
        var s = new NetworkState { Adapters = new[] { Wifi with { Connected = false } }, AirplaneMode = Tri.Yes, WifiSignalPercent = 10, Metered = Tri.Yes, IpConflictRecent = true };
        Assert.Equal(new[] { FindingCode.AirplaneModeOn }, Codes(s));
        var dns = Down(); dns.GatewayReachable = Tri.Yes; dns.IpConflictRecent = true;
        Assert.Equal(FindingCode.DnsFailing, Codes(dns)[0]);
        var unknown = Down(); unknown.GatewayReachable = Tri.Unknown; unknown.WifiSignalPercent = 5;
        Assert.Equal(new[] { FindingCode.ProbeIncomplete }, Codes(unknown));
    }

    [Theory]
    [InlineData(39, true)]
    [InlineData(40, false)]
    [InlineData(100, false)]
    public void WeakWifi_BelowFortyPercentOnly(int pct, bool weak)
    {
        var s = Working(); s.WifiSignalPercent = pct;
        Assert.Equal(weak, Codes(s).Contains(FindingCode.NetWeakWifi));
    }

    [Fact]
    public void WeakWifi_Rungs_MentionFiveGhzOnlyWhenSupported()
    {
        var s = Working(); s.WifiSignalPercent = 25;
        Assert.Equal(new[] { "move_closer", "restart_router" }, Ladder(s).Select(r => r.Id));
        s.Supports5GHz = Tri.Yes;
        Assert.Equal(new[] { "move_closer", "use_5ghz", "restart_router" }, Ladder(s).Select(r => r.Id));
        Assert.Contains("5 GHz", Ladder(s)[1].Say);
        Assert.Equal("wifi", Ladder(s)[1].Arg);
    }

    [Fact]
    public void IpConflict_RenewThenRestartRouter()
    {
        var s = Working(); s.IpConflictRecent = true;
        Assert.Equal(new[] { "renew_address", "restart_router" }, Ladder(s).Select(r => r.Id));
    }

    [Fact]
    public void Metered_OpensWifiSettings()
    {
        var s = Working(); s.Metered = Tri.Yes;
        var r = Assert.Single(Ladder(s));
        Assert.Equal(("ui.open_settings_page", "wifi"), (r.ActionId, r.Arg));
        Assert.Contains("Metered connection", r.Say);
    }

    [Fact]
    public void RungIds_AreUniqueInEveryLadder()
    {
        var s = Down(); s.AddressObtained = Tri.No; s.WifiSignalPercent = 10; s.Supports5GHz = Tri.Yes; s.IpConflictRecent = true; s.Metered = Tri.Yes;
        Assert.Equal(Ladder(s).Length, Ladder(s).Select(r => r.Id).Distinct().Count());
        var w = Working(); w.AddressObtained = Tri.No; w.WifiSignalPercent = 10; w.Supports5GHz = Tri.Yes; w.IpConflictRecent = true; w.Metered = Tri.Yes;
        Assert.Equal(Ladder(w).Length, Ladder(w).Select(r => r.Id).Distinct().Count());
    }

    // ---- executor and routing ----

    [Fact]
    public void RenewAddress_IsAnExecutorAction_WithValidatedArgument()
    {
        Assert.Contains("network.renew_address", FixActions.ElevatedActions);
        Assert.Equal(2, ExecutorProgram.Main(new[] { "network.renew_address", "Wi-Fi\" & whoami" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "network.renew_address", "/release" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "network.renew_address" }));
    }
}

/// <summary>Read-only check of the new parts of the internet check on the real machine. Runs only when AFS_LIVE is set.</summary>
[Collection("Live machine")]
public class LiveNetworkExtrasTests
{
    [Fact]
    public async Task NetworkExtras_ReadWithoutErrors()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = await new RealNetworkProbe().ReadAsync();
        var f = NetworkTriage.Evaluate(s);
        // Only codes and numbers are written: no adapter names, network names or addresses.
        var lines = new List<string>
        {
            "errors: " + string.Join(" | ", s.ProbeErrors), "extra errors: " + string.Join(" | ", s.ExtraProbeErrors),
            $"address obtained {s.AddressObtained}; wifi signal {s.WifiSignalPercent?.ToString() ?? "none"}%; 5 GHz {s.Supports5GHz}; ip conflict {s.IpConflictRecent}; metered {s.Metered}",
        };
        lines.AddRange(f.Select(x => $"FINDING {x.Code}"));
        lines.AddRange(NetworkTriage.Ladder(s, f).Select(r => $"RUNG {r.Id} {r.ActionId}"));
        File.WriteAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
        Assert.Empty(s.ExtraProbeErrors);
        if (s.Adapters.Any(a => a.Connected))
        {
            Assert.NotEqual(Tri.Unknown, s.Metered);
            if (s.Adapters.Where(a => a.Connected).Any(a => a.IsWireless) && s.WifiSignalPercent != null)
                Assert.InRange(s.WifiSignalPercent.Value, 0, 100);
        }
    }
}
