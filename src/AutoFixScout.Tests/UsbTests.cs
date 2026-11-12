using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class UsbTests
{
    private const string Serial = @"USB\VID_0781&PID_5581\4C530001230101108494";

    private static HealthState Healthy() => new()
    {
        ClockOffsetSeconds = 2,
        Services = ServiceDefaults.All.Select(d => new UpdateServiceInfo(d.Name, d.ShouldRun ? "Running" : "Stopped", d.Start.ToString())).ToList(),
        RebootPending = Tri.No,
    };

    private static (IReadOnlyList<Finding> F, IReadOnlyList<Rung> L) Run(HealthState s)
    {
        var f = HealthTriage.Evaluate(s);
        return (f, HealthTriage.Ladder(s, f));
    }

    private static IEnumerable<UsbPnpEvent> Events(string id, int n) => Enumerable.Range(0, n).Select(i => new UsbPnpEvent(id, DateTime.UtcNow.AddMinutes(-i)));

    [Fact]
    public void Grouping_AppliesThresholdAndIgnoresNonUsb()
    {
        var events = Events(Serial, 5).Concat(Events(@"USB\VID_1&PID_2\X", 4)).Concat(Events(@"PCI\VEN_1\Y", 9));
        var r = UsbHealth.Repeated(events);
        Assert.Single(r);
        Assert.Equal(5, r[0].Count);
        Assert.Equal(6, UsbHealth.Repeated(Events(Serial, 5).Concat(Events(Serial.ToLowerInvariant(), 1)))[0].Count);
    }

    [Fact]
    public void Powercfg_ParsesAcDcFromHexOnly()
    {
        const string text = "  Subgroup GUID: 2a737441\n    Power Setting Index: 0x00000001\n    Possible Setting Index: 000\n    Current AC Power Setting Index: 0x00000001\n    Current DC Power Setting Index: 0x00000000\n";
        Assert.Equal((true, false), UsbHealth.ParseSelectiveSuspend(text));
        Assert.Null(UsbHealth.ParseSelectiveSuspend("nothing"));
        Assert.Null(UsbHealth.ParseSelectiveSuspend("0x00000005 0x00000007"));
    }

    [Fact]
    public void SafeName_RemovesInstanceIdsAndSerials()
    {
        Assert.Equal("a USB device", UsbHealth.SafeName(Serial));
        Assert.Equal("USB Mass Storage Device", UsbHealth.SafeName("USB Mass Storage Device"));
        Assert.DoesNotContain("4C53", UsbHealth.SafeName("Stick 4C530001230101108494"));
    }

    [Fact]
    public void NoUsbProblem_LooksNormal_AndUnreadableUsbIsIgnored()
    {
        var s = Healthy();
        s.Usb = new UsbState { HubErrors = 2, SelectiveSuspendAc = true };
        Assert.Equal(FindingCode.LooksNormal, Run(s).F[0].Code);
        s.Usb = null;
        Assert.Equal(FindingCode.LooksNormal, Run(s).F[0].Code);
    }

    [Fact]
    public void RepeatedDevice_NamesTheDevice_AndOffersTheLadder()
    {
        var s = Healthy();
        s.Usb = new UsbState { Churn = new[] { new UsbChurn("SanDisk Ultra USB Device", "USB", 7) }, HubErrors = 4, PnpEvents = 20, SelectiveSuspendAc = true, SelectiveSuspendDc = true };
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.HealthUsbDisconnects && x.PlainMessage.Contains("SanDisk Ultra USB Device") && x.PlainMessage.Contains("7 times"));
        Assert.Contains(f, x => x.Code == FindingCode.HealthUsbErrors);
        Assert.Contains(l, r => r.Id == "usb_port_cable" && r.ActionId == "ui.human_step");
        Assert.Contains(l, r => r.Id == "usb_selective_suspend_off" && r.Say.Contains("Disabled"));
        Assert.Contains(l, r => r.Id == "usb_driver_updates" && r.Arg == "windowsupdate");
        Assert.Contains(l, r => r.Id == "rescan" && r.ActionId == "device.rescan");
    }

    [Fact]
    public void SelectiveSuspendStep_OnlyWhenOn()
    {
        var s = Healthy();
        s.Usb = new UsbState { HubErrors = 5, SelectiveSuspendAc = false, SelectiveSuspendDc = false };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthUsbErrors, f[0].Code);
        Assert.DoesNotContain(l, r => r.Id == "usb_selective_suspend_off");
    }

    [Fact]
    public void RescanIsNotDuplicated_WhenADeviceAlsoHasAProblem()
    {
        var s = Healthy();
        s.DeviceProblems = new[] { new DeviceProblem("A", "USB-1", "USB", 43) };
        s.Usb = new UsbState { Churn = new[] { new UsbChurn("Mouse", "HIDClass", 5) } };
        var (_, l) = Run(s);
        Assert.Equal(1, l.Count(r => r.Id == "rescan"));
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void TextFollowsWritingGuide_AndShowsNoInstanceIds()
    {
        var s = Healthy();
        s.Usb = new UsbState { Churn = new[] { new UsbChurn(UsbHealth.SafeName(Serial), "USB", 6), new UsbChurn("Webcam", "Camera", 5) }, HubErrors = 9, SelectiveSuspendDc = true };
        var (f, l) = Run(s);
        var texts = f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).ToList();
        foreach (var t in texts)
        {
            Assert.DoesNotMatch(new Regex(@"n't|'ll|'re|'ve|'d |'s |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase), t);
            Assert.DoesNotMatch(@"VID_|PID_|USB\\", t);
        }
        foreach (var x in f) { Assert.DoesNotContain("VID_", x.TechnicalDetail); Assert.DoesNotContain(@"USB\", x.TechnicalDetail); }
        Assert.Contains("1 other USB device has", f.First(x => x.Code == FindingCode.HealthUsbDisconnects).PlainMessage);
        foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
            Assert.Contains("Windows will ask for your permission", r.Say);
    }
}

/// <summary>Reads the real machine only (AFS_LIVE set). Read-only.</summary>
[Collection("Live machine")]
public class LiveUsbTests
{
    [Fact]
    public void UsbPart_ReadsThisMachine()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = new RealHealthProbe().Read();
        Assert.DoesNotContain(s.ProbeErrors, e => e.StartsWith("USB devices"));
        Assert.NotNull(s.Usb);
        var u = s.Usb!;
        var f = HealthTriage.Evaluate(s).Where(x => x.Code is FindingCode.HealthUsbDisconnects or FindingCode.HealthUsbErrors);
        File.AppendAllLines(output, new[] { $"USB: pnp events {u.PnpEvents}; hub errors {u.HubErrors}; churn {u.Churn.Count}; selective suspend AC={u.SelectiveSuspendAc} DC={u.SelectiveSuspendDc}" }
            .Concat(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage}")));
        Assert.NotNull(u.SelectiveSuspendAc);
    }
}

public class UsbThresholdTests
{
    // Regression (bench): a built-in webcam that only restarts at each start-up/wake was reported as "disconnecting".
    [Theory]
    [InlineData(0, 5)]
    [InlineData(3, 11)]
    [InlineData(-1, 5)]
    public void Threshold_GrowsWithStartsAndWakes(int sessions, int expected) =>
        Assert.Equal(expected, AutoFixScout.Triage.UsbHealth.ThresholdFor(sessions));
}
