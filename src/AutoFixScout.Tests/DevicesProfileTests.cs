using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class DevicesProfileTests
{
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

    private static readonly int[] Codes = { 1, 3, 10, 12, 14, 18, 19, 21, 22, 24, 28, 31, 32, 37, 39, 40, 41, 43, 48, 52 };

    [Fact]
    public void Meaning_IsShortAndFormal_ForEveryListedCode()
    {
        foreach (var code in Codes)
        {
            var m = DeviceErrors.Meaning(code);
            Assert.NotEqual(DeviceErrors.Meaning(9999), m);
            Assert.True(m.Length < 120);
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!", m);
        }
    }

    [Fact]
    public void Meaning_KnownWording()
    {
        Assert.Contains("no driver", DeviceErrors.Meaning(28));
        Assert.Contains("reported a problem", DeviceErrors.Meaning(43));
        Assert.Contains("cannot start", DeviceErrors.Meaning(10));
        Assert.Equal("reports a problem", DeviceErrors.Meaning(12345));
        Assert.True(DeviceErrors.IsNoise(45));
    }

    [Fact]
    public void Healthy_HasNoDeviceOrProfileFinding()
    {
        var (f, _) = Run(Healthy());
        Assert.Equal(FindingCode.LooksNormal, f[0].Code);
    }

    [Fact]
    public void DeviceProblem_NamesFirstDevice_ListsAll_AndOffersRescan()
    {
        var s = Healthy();
        s.DeviceProblems = new[]
        {
            new DeviceProblem("Unknown Gadget", "PCI-X", "Unknown", 28),
            new DeviceProblem("Other Thing", "ACPI-Y", "System", 10),
            new DeviceProblem("Switched Off", "USB-Z", "USB", 22),
        };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthDeviceProblem, f[0].Code);
        Assert.Contains("\"Unknown Gadget\"", f[0].PlainMessage);
        Assert.Contains("has no driver installed", f[0].PlainMessage);
        Assert.Contains("1 other device has", f[0].PlainMessage);
        Assert.Contains("Other Thing", f[0].TechnicalDetail);
        Assert.Contains("information only", f[0].TechnicalDetail);
        var rescan = l.Single(r => r.ActionId == "device.rescan");
        Assert.Equal("devices", rescan.Arg);
        Assert.Contains("Windows will ask for your permission", rescan.Say);
        Assert.Contains(l, r => r.ActionId == "ui.open_settings_page" && r.Arg == "windowsupdate");
        Assert.Contains(l, r => r.Id == "device_driver_manufacturer" && r.ActionId == "ui.human_step");
        Assert.True(l.Where(r => r.ActionId != "device.rescan").All(r => r.Tier == 0));
        Assert.False(HealthTriage.ProbablyHardware(s));
    }

    [Fact]
    public void DisabledOnly_IsInformationOnly_NoFinding()
    {
        var s = Healthy();
        s.DeviceProblems = new[] { new DeviceProblem("Switched Off", "USB-Z", "USB", 22) };
        Assert.Equal(FindingCode.LooksNormal, Run(s).F[0].Code);
    }

    [Fact]
    public void Code10_OffersRescanOnly_NoDriverSteps()
    {
        var s = Healthy();
        s.DeviceProblems = new[] { new DeviceProblem("Other Thing", "ACPI-Y", "System", 10) };
        var (_, l) = Run(s);
        Assert.Equal(new[] { "device.rescan" }, l.Select(r => r.ActionId));
    }

    [Fact]
    public void Code43_OnUsb_AddsReplugStep()
    {
        var s = Healthy();
        s.DeviceProblems = new[] { new DeviceProblem("USB Device", "USB-VID_1", "USB", 43) };
        Assert.Contains(Run(s).L, r => r.Id == "device_replug" && r.Say.Contains("different USB port"));
        var s2 = Healthy();
        s2.DeviceProblems = new[] { new DeviceProblem("Display", "PCI-VEN", "Display", 43) };
        Assert.DoesNotContain(Run(s2).L, r => r.Id == "device_replug");
    }

    [Fact]
    public void DeviceRungs_NeverToggleOrChangeDrivers()
    {
        var s = Healthy();
        s.DeviceProblems = new[] { new DeviceProblem("GPU", "PCI-A", "Display", 28), new DeviceProblem("Disk", "SCSI-B", "DiskDrive", 1) };
        var (_, l) = Run(s);
        Assert.DoesNotContain(l, r => r.ActionId.StartsWith("driver.") || r.ActionId.StartsWith("device.disable") || r.ActionId == "device.enable");
    }

    [Theory]
    [InlineData(@"C:\Users\TEMP", true)]
    [InlineData(@"C:\Users\TEMP.PC", true)]
    [InlineData(@"c:\users\temp.000", true)]
    [InlineData(@"C:\Users\Temple", false)]
    [InlineData(@"C:\Users\Anna", false)]
    [InlineData(@"C:\Temp", false)]
    [InlineData(null, false)]
    public void TempProfilePath(string? path, bool expected) => Assert.Equal(expected, TempProfiles.IsTemporaryPath(path));

    [Fact]
    public void TempProfile_RestartFirst_ThenHelperStep_NotHardware()
    {
        var s = Healthy();
        s.TempProfile = true; s.TempProfileDetail = "profile folder is a TEMP folder";
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthTempProfile, f[0].Code);
        Assert.Contains("temporary profile", f[0].PlainMessage);
        Assert.Contains("nothing is deleted", f[0].PlainMessage);
        Assert.Equal(new[] { "restart_sign_in_again", "profile_helper_steps" }, l.Select(r => r.Id));
        Assert.All(l, r => Assert.Equal("ui.human_step", r.ActionId));
        Assert.Contains(".bak", l[1].Say);
        Assert.False(HealthTriage.ProbablyHardware(s));
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var s = Healthy();
        s.TempProfile = true;
        s.DeviceProblems = new[]
        {
            new DeviceProblem("A", "USB-1", "USB", 43), new DeviceProblem("B", "PCI-2", "Net", 28), new DeviceProblem("C", "X-3", "System", 22),
        };
        var (f, l) = Run(s);
        var texts = f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).ToList();
        foreach (var c in Codes)
            texts.Add("Windows reports a problem with the device \"X\": it " + DeviceErrors.Meaning(c) + ".");
        foreach (var t in texts)
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
            Assert.Contains("Windows will ask for your permission", r.Say);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void RescanAreas_IncludeDevices_AndMatchExecutor()
    {
        Assert.Contains("devices", Rescan.Areas);
        Assert.Contains("devices", ExecutorProgram.RescanAreas);
        Assert.Equal(Rescan.Areas, ExecutorProgram.RescanAreas);
    }
}
