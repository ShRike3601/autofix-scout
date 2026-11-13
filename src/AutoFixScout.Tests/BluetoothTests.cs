using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class BluetoothTests
{
    private const string AdapterId = @"USB\VID_8087&PID_0026\5&1234";

    private static BluetoothState Healthy() => new()
    {
        Adapters = new[] { new BtAdapterInfo("Intel(R) Wireless Bluetooth(R)", AdapterId, 0) },
        RadioOn = Tri.Yes, ServiceStatus = "Running",
        Paired = new[] { new BtPairedDevice("Headphones", @"BTHENUM\DEV_001122334455\1", true, Tri.Yes) },
    };

    private static (FindingCode Code, IReadOnlyList<Rung> L) Run(BluetoothState s)
    {
        var f = BluetoothTriage.Evaluate(s);
        return (f[0].Code, BluetoothTriage.Ladder(s, f));
    }

    [Fact]
    public void Working_LooksNormal_StillOffersSettingsAndAdapterRestart()
    {
        var (code, l) = Run(Healthy());
        Assert.Equal(FindingCode.LooksNormal, code);
        Assert.Equal(new[] { "ui.open_settings_page", "device.disable_enable" }, l.Select(r => r.ActionId));
        Assert.True(BluetoothTriage.Ready(Healthy()));
    }

    [Fact]
    public void RadioOff_OpensSettings_NeverSwitchesItItself()
    {
        var s = Healthy(); s.RadioOn = Tri.No;
        var (code, l) = Run(s);
        Assert.Equal(FindingCode.BluetoothOff, code);
        Assert.Equal("bluetooth", l[0].Arg);
        Assert.DoesNotContain(l, r => r.ActionId == "device.disable_enable");
    }

    [Fact]
    public void DisabledAdapter_IsEnabled()
    {
        var s = Healthy(); s.Adapters = new[] { new BtAdapterInfo("BT", AdapterId, 22) };
        var (code, l) = Run(s);
        Assert.Equal(FindingCode.BluetoothAdapterDisabled, code);
        Assert.Contains(l, r => r.ActionId == "device.enable" && r.Arg == AdapterId);
    }

    [Fact]
    public void NoAdapter_Rescans_AndPointsAtHardware()
    {
        var s = Healthy(); s.Adapters = Array.Empty<BtAdapterInfo>();
        var (code, l) = Run(s);
        Assert.Equal(FindingCode.NoBluetoothAdapter, code);
        Assert.Contains(l, r => r.ActionId == "device.rescan" && r.Arg == "bluetooth");
        Assert.True(BluetoothTriage.ProbablyHardware(s));
    }

    [Fact]
    public void StoppedOrDisabledService_IsStartedOrRestored()
    {
        var s = Healthy(); s.ServiceStatus = "Stopped";
        Assert.Equal(("service.restart", "bthserv"), (Run(s).L[0].ActionId, Run(s).L[0].Arg));
        s.ServiceStatus = "Disabled";
        Assert.Equal("service.restore_default", Run(s).L[0].ActionId);
    }

    [Fact]
    public void AudioPairedButDisconnected_GuidesToConnect()
    {
        var s = Healthy(); s.Paired = new[] { new BtPairedDevice("Headphones", @"BTHENUM\DEV_1\1", true, Tri.No) };
        var (code, l) = Run(s);
        Assert.Equal(FindingCode.BluetoothAudioNotConnected, code);
        Assert.Equal("connect_audio", l[0].Id);
    }

    [Fact]
    public void ProbeError_FailsClosed()
    {
        var (code, l) = Run(new BluetoothState { ProbeErrors = new[] { "WMI" } });
        Assert.Equal(FindingCode.ProbeIncomplete, code);
        Assert.Empty(l);
    }

    [Fact]
    public void Executor_RestartsOnlyBluetoothService_AndRescanAcceptsBluetooth()
    {
        Assert.Equal(2, ExecutorProgram.Main(new[] { "service.restart", "bthserv & calc" }));
        Assert.Contains("bluetooth", ExecutorProgram.RescanAreas);
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var states = new List<BluetoothState> { Healthy() };
        var a = Healthy(); a.RadioOn = Tri.No; a.ServiceStatus = "Disabled"; states.Add(a);
        var b = Healthy(); b.Adapters = new[] { new BtAdapterInfo("BT", AdapterId, 10) }; states.Add(b);
        var c = Healthy(); c.Paired = Array.Empty<BtPairedDevice>(); states.Add(c);
        var d = Healthy(); d.Adapters = Array.Empty<BtAdapterInfo>(); states.Add(d);
        var e = Healthy(); e.Paired = new[] { new BtPairedDevice("H", @"BTHENUMDEV_1", true, Tri.No) }; states.Add(e);
        foreach (var s in states)
        {
            var f = BluetoothTriage.Evaluate(s);
            var l = BluetoothTriage.Ladder(s, f);
            foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).Append(BluetoothTriage.NotFixedHint(f) ?? ""))
                Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
            foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
                Assert.Contains("Windows will ask for your permission", r.Say);
            Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        }
    }
}
