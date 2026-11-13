using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class ScannerTests
{
    private static readonly PrinterInfo Laser = new("Office Laser", true, false, false, 3, 3, 0);
    private static readonly ScannerDevice Scanner = new("EPSON Scanner", @"USB\VID_04B8&PID_0001\1", 0);

    private static PrinterState Base() => new()
    {
        SpoolerRunning = Tri.Yes,
        Printers = new[] { Laser },
        WiaService = new UpdateServiceInfo("stisvc", "Stopped", "Manual"),
        ShellHardwareService = new UpdateServiceInfo("ShellHWDetection", "Running", "Automatic"),
        Scanners = new[] { Scanner },
    };

    public static IEnumerable<PrinterState> Styled()
    {
        foreach (var focus in new[] { false, true })
        {
            var off = Base(); off.ScanFocus = focus;
            off.WiaService = new UpdateServiceInfo("stisvc", "Stopped", "Disabled");
            off.ShellHardwareService = new UpdateServiceInfo("ShellHWDetection", "Stopped", "Automatic");
            yield return off;
            var bad = Base(); bad.ScanFocus = focus;
            bad.Scanners = new[] { new ScannerDevice("EPSON Scanner", "USB\\X", 10) };
            yield return bad;
            var both = Base(); both.ScanFocus = focus;
            both.Printers = new[] { Laser with { DetectedErrorState = 8 } };
            both.Scanners = bad.Scanners;
            yield return both;
        }
    }

    [Fact]
    public void Healthy_ScannerPresentWiaStoppedOnDemand_IsNormal()
    {
        var s = Base();
        Assert.Equal(FindingCode.LooksNormal, PrinterTriage.Evaluate(s)[0].Code);
        s.ScanFocus = true;
        Assert.Equal(FindingCode.LooksNormal, PrinterTriage.Evaluate(s)[0].Code);
    }

    [Fact]
    public void WiaDisabled_IsServiceOff_WithRestoreDefaultRung()
    {
        var s = Base();
        s.WiaService = new UpdateServiceInfo("stisvc", "Stopped", "Disabled");
        var f = PrinterTriage.Evaluate(s);
        Assert.Contains(f, x => x.Code == FindingCode.ScannerServiceOff);
        var l = PrinterTriage.Ladder(s, f);
        Assert.Contains(l, r => r.ActionId == "service.restore_default" && r.Arg == "stisvc");
        Assert.Contains(l, r => r.ActionId == "ui.open_settings_page" && r.Arg == "scanners");
        Assert.Contains(l, r => r.Id == "check_scanner");
    }

    [Fact]
    public void WiaAutomaticButStopped_WithScanner_IsServiceOff()
    {
        var s = Base();
        s.WiaService = new UpdateServiceInfo("stisvc", "Stopped", "Automatic");
        Assert.Contains(PrinterTriage.Evaluate(s), x => x.Code == FindingCode.ScannerServiceOff);
        s.Scanners = Array.Empty<ScannerDevice>();
        Assert.DoesNotContain(PrinterTriage.Evaluate(s), x => x.Code == FindingCode.ScannerServiceOff);
    }

    [Fact]
    public void ShellHardwareStopped_OnlyMattersWithScannerOrFocus()
    {
        var s = Base();
        s.ShellHardwareService = new UpdateServiceInfo("ShellHWDetection", "Stopped", "Automatic");
        Assert.Contains(PrinterTriage.Evaluate(s), x => x.Code == FindingCode.ScannerServiceOff);
        s.Scanners = Array.Empty<ScannerDevice>();
        Assert.DoesNotContain(PrinterTriage.Evaluate(s), x => x.Code == FindingCode.ScannerServiceOff);
        s.ScanFocus = true;
        Assert.Contains(PrinterTriage.Evaluate(s), x => x.Code == FindingCode.ScannerServiceOff);
    }

    [Fact]
    public void ScannerDeviceError_OffersRescanAndHumanStep()
    {
        var s = Base();
        s.Scanners = new[] { new ScannerDevice("EPSON Scanner", "USB\\X", 43) };
        var f = PrinterTriage.Evaluate(s);
        Assert.Equal(FindingCode.ScannerDeviceError, f[0].Code);
        var l = PrinterTriage.Ladder(s, f);
        Assert.Contains(l, r => r.ActionId == "device.rescan" && r.Arg == "devices");
        Assert.Contains(Rescan.Areas, a => a == "devices");
    }

    [Fact]
    public void NotConnectedScanner_Code45_IsNoise()
    {
        var s = Base();
        s.Scanners = new[] { new ScannerDevice("EPSON Scanner", "USB\\X", 45) };
        Assert.Equal(FindingCode.LooksNormal, PrinterTriage.Evaluate(s)[0].Code);
    }

    [Fact]
    public void PrinterFirst_UnlessScanFocus()
    {
        var s = Base();
        s.Printers = new[] { Laser with { DetectedErrorState = 8 } };
        s.Scanners = new[] { new ScannerDevice("EPSON Scanner", "USB\\X", 10) };
        var f = PrinterTriage.Evaluate(s);
        Assert.Equal(FindingCode.PrinterError, f[0].Code);
        Assert.Equal(FindingCode.ScannerDeviceError, f[^1].Code);
        Assert.Equal("printer_error", PrinterTriage.Ladder(s, f)[0].Id);

        s.ScanFocus = true;
        f = PrinterTriage.Evaluate(s);
        Assert.Equal(FindingCode.ScannerDeviceError, f[0].Code);
        Assert.Contains(f, x => x.Code == FindingCode.PrinterError);
        Assert.Equal("scanners", PrinterTriage.Ladder(s, f)[0].Id);
    }

    [Fact]
    public void ScanFocus_NoPrinter_StillReportsScanner()
    {
        var s = Base();
        s.Printers = Array.Empty<PrinterInfo>();
        s.ScanFocus = true;
        s.Scanners = new[] { new ScannerDevice("EPSON Scanner", "USB\\X", 10) };
        var f = PrinterTriage.Evaluate(s);
        Assert.Single(f);
        Assert.Equal(FindingCode.ScannerDeviceError, f[0].Code);
    }

    [Fact]
    public void ScannerReadError_FailsClosed()
    {
        var s = Base();
        s.WiaService = new UpdateServiceInfo("stisvc", "Stopped", "Disabled");
        s.ScannerErrors = new[] { "Win32_PnPEntity: boom" };
        var f = PrinterTriage.Evaluate(s);
        Assert.DoesNotContain(f, x => x.Code == FindingCode.ScannerServiceOff);
        Assert.Empty(PrinterTriage.Ladder(s, f).Where(r => r.Id is "scanners" or "check_scanner"));
    }

    [Fact]
    public void WiaServiceDefault_IsManual_AndRestorable()
    {
        var d = ServiceDefaults.Find("stisvc");
        Assert.NotNull(d);
        Assert.Equal(ServiceDefaults.Start.Manual, d!.Start);
        Assert.False(d.ShouldRun);
        Assert.True(ServiceDefaults.IsFault("stisvc", "Stopped", "Disabled"));
        Assert.False(ServiceDefaults.IsFault("stisvc", "Stopped", "Manual"));
    }

    [Fact]
    public void CameraNames_AreNotScanners()
    {
        Assert.True(PrinterRules.IsCamera("Integrated Camera", null));
        Assert.True(PrinterRules.IsCamera("USB2.0 HD UVC WebCam", null));
        Assert.True(PrinterRules.IsCamera("USB Video Device", "usbvideo"));
        Assert.False(PrinterRules.IsCamera("EPSON Scanner", "usbscan"));
    }

    [Fact]
    public void Rungs_HaveUniqueIds_AndFollowTheWritingGuide()
    {
        var contractions = new[] { "n't", "'d ", "'ll ", "I'm", "it's", "that's", "'re ", "'ve " };
        foreach (var s in Styled())
        {
            var f = PrinterTriage.Evaluate(s);
            var l = PrinterTriage.Ladder(s, f);
            Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
            var texts = f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).ToList();
            if (PrinterTriage.NotFixedHint(f) is { } h) texts.Add(h);
            foreach (var t in texts)
            {
                foreach (var c in contractions) Assert.DoesNotContain(c, t, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("!", t);
                Assert.DoesNotMatch(new Regex(@"(^|[\s""(])I\s"), t);
                Assert.DoesNotMatch(new Regex(@"\bwe\b", RegexOptions.IgnoreCase), t);
            }
            Assert.All(l.Where(r => r.ActionId is "service.restore_default" or "device.rescan"),
                r => Assert.Contains("Windows will ask for your permission", r.Say));
        }
    }
}

/// <summary>Reads the real machine only (AFS_LIVE set): the scanner part must read without errors. Read-only.</summary>
[Collection("Live machine")]
public class LiveScannerTests
{
    [Fact]
    public void ScannerPart_ReadsThisMachine_WithoutProbeErrors()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = new RealPrinterProbe().Read();
        var f = PrinterTriage.Evaluate(s);
        var lines = new List<string>
        {
            "scanner errors: " + string.Join(" | ", s.ScannerErrors),
            $"stisvc: {s.WiaService?.Status}/{s.WiaService?.StartType}; ShellHWDetection: {s.ShellHardwareService?.Status}/{s.ShellHardwareService?.StartType}",
            "scanners: " + string.Join("; ", s.Scanners.Select(d => $"{d.Name} err={d.ErrorCode}")),
        };
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage}"));
        File.AppendAllLines(output, lines);
        Assert.Empty(s.ScannerErrors);
        Assert.NotNull(s.WiaService);
        Assert.NotEqual("Unknown", s.WiaService!.Status);
        Assert.NotNull(s.ShellHardwareService);
    }
}
