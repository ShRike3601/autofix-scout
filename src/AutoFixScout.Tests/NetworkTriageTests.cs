using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class NetworkTriageTests
{
    private static readonly NetAdapterInfo WifiUp = new("Intel Wi-Fi 6 AX201", @"PCI\VEN_8086&DEV_A0F0\3&1", true, true, 0);
    private static readonly NetAdapterInfo WifiDown = WifiUp with { Connected = false };

    private static NetworkState Online(Tri gw, Tri dns, Tri net) =>
        new() { Adapters = new[] { WifiUp }, AirplaneMode = Tri.No, GatewayReachable = gw, DnsResolving = dns, InternetReachable = net };

    private static FindingCode Code(NetworkState s) => NetworkTriage.Evaluate(s)[0].Code;
    private static string[] Ladder(NetworkState s) => NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s)).Select(r => r.Id).ToArray();

    [Fact]
    public void Working_LooksNormal_NoFixes()
    {
        var s = Online(Tri.Yes, Tri.Yes, Tri.Yes);
        Assert.Equal(FindingCode.LooksNormal, Code(s));
        Assert.Empty(Ladder(s));
    }

    [Fact]
    public void RouterIgnoresPing_ButInternetWorks_LooksNormal() =>
        Assert.Equal(FindingCode.LooksNormal, Code(Online(Tri.No, Tri.Yes, Tri.Yes)));

    [Fact]
    public void ProbeError_FailsClosed() =>
        Assert.Equal(FindingCode.ProbeIncomplete, Code(new NetworkState { ProbeErrors = new[] { "WMI" } }));

    [Fact]
    public void NoAdapters_ProbablyHardware()
    {
        var s = new NetworkState();
        Assert.Equal(FindingCode.NoNetworkAdapter, Code(s));
        Assert.True(NetworkTriage.ProbablyHardware(s));
    }

    [Fact]
    public void AirplaneMode_OpensSettings() =>
        Assert.Equal(new[] { "airplane" }, Ladder(new() { Adapters = new[] { WifiDown }, AirplaneMode = Tri.Yes }));

    [Fact]
    public void DisabledAdapter_EnablesThatAdapter()
    {
        var s = new NetworkState { Adapters = new[] { WifiDown with { ProblemCode = 22 } }, AirplaneMode = Tri.No };
        Assert.Equal(FindingCode.NetAdapterDisabled, Code(s));
        var rung = NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s)).Single();
        Assert.Equal(("device.enable", WifiUp.InstanceId), (rung.ActionId, rung.Arg));
        Assert.False(NetworkTriage.ProbablyHardware(s)); // disabled isn't broken
    }

    [Fact]
    public void NotConnected_Wifi_PickNetwork_Ethernet_CheckCable()
    {
        Assert.Equal(new[] { "pick_wifi" }, Ladder(new() { Adapters = new[] { WifiDown }, AirplaneMode = Tri.No }));
        var eth = new NetAdapterInfo("Realtek PCIe GbE", "PCI\\X", false, false, 0);
        Assert.Equal(new[] { "check_cable" }, Ladder(new() { Adapters = new[] { eth }, AirplaneMode = Tri.No }));
    }

    [Fact]
    public void DnsFailing_FlushFirst_ThenAdapter_ThenRouter()
    {
        var s = Online(Tri.Yes, Tri.No, Tri.No);
        Assert.Equal(FindingCode.DnsFailing, Code(s));
        Assert.Equal(new[] { "flush_dns", "toggle_adapter", "restart_router" }, Ladder(s));
    }

    [Fact]
    public void DnsFailing_HandTypedServer_OffersResetAfterFlush()
    {
        // Test step 3d: a bogus DNS server survives a flush and an adapter restart.
        var s = Online(Tri.Yes, Tri.No, Tri.No);
        s.StaticDnsServers = new[] { "192.0.2.1" };
        s.StaticDnsAdapterId = WifiUp.InstanceId;
        var ladder = NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s));
        Assert.Equal(new[] { "flush_dns", "reset_dns", "toggle_adapter", "restart_router" }, ladder.Select(r => r.Id));
        Assert.Equal(WifiUp.InstanceId, ladder[1].Arg);
        Assert.Contains("192.0.2.1", ladder[1].Say);
    }

    [Fact]
    public void DnsFailing_VpnLoopbackServers_NotReset()
    {
        // Cloudflare WARP puts 127.0.2.2/.3 on the Wi-Fi adapter and manages them itself.
        var s = Online(Tri.Yes, Tri.No, Tri.No);
        s.StaticDnsServers = new[] { "127.0.2.2", "127.0.2.3" };
        s.StaticDnsAdapterId = WifiUp.InstanceId;
        Assert.DoesNotContain("reset_dns", Ladder(s));
    }

    [Fact]
    public void RouterSilentAndDnsFails_GatewayUnreachable_HintNamesRouter()
    {
        var f = NetworkTriage.Evaluate(Online(Tri.No, Tri.No, Tri.No));
        Assert.Equal(FindingCode.GatewayUnreachable, f[0].Code);
        Assert.Contains("router", NetworkTriage.NotFixedHint(f)!);
    }

    [Fact]
    public void DnsOkButNoInternet_HintNamesProvider() =>
        Assert.Contains("provider", NetworkTriage.NotFixedHint(NetworkTriage.Evaluate(Online(Tri.Yes, Tri.Yes, Tri.No)))!);

    [Fact]
    public void Executor_RejectsEnableForNonsenseId() =>
        Assert.Equal(2, AutoFixScout.Executor.Program.Main(new[] { "device.enable", "a b" }));
}
