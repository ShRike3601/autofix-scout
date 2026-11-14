using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>Blue screen explainer: stop code and module mappers, report parsing, recurrence, wording and steps.</summary>
public class BlueScreenTests
{
    private const string Contractions = @"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b";

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

    [Theory]
    [InlineData(0x0Au, CauseGroup.Driver)] [InlineData(0xD1u, CauseGroup.Driver)] [InlineData(0x50u, CauseGroup.Driver)]
    [InlineData(0x1Au, CauseGroup.Memory)] [InlineData(0x19u, CauseGroup.Memory)] [InlineData(0x3Bu, CauseGroup.Driver)]
    [InlineData(0x7Eu, CauseGroup.Driver)] [InlineData(0x8Eu, CauseGroup.Driver)] [InlineData(0x1Eu, CauseGroup.Driver)]
    [InlineData(0x3Du, CauseGroup.Driver)] [InlineData(0xC2u, CauseGroup.Driver)] [InlineData(0x9Fu, CauseGroup.Driver)]
    [InlineData(0x116u, CauseGroup.Graphics)] [InlineData(0x117u, CauseGroup.Graphics)] [InlineData(0x124u, CauseGroup.Hardware)]
    [InlineData(0x133u, CauseGroup.Driver)] [InlineData(0x139u, CauseGroup.Driver)] [InlineData(0xEFu, CauseGroup.Files)]
    [InlineData(0x7Au, CauseGroup.Disk)] [InlineData(0x77u, CauseGroup.Disk)]
    public void CauseGroups_OfCommonStopCodes(uint code, CauseGroup group)
    {
        Assert.Equal(group, BlueScreenReports.GroupOf(code));
        var c = BlueScreenReports.CausesOf(code)!;
        Assert.DoesNotMatch(Contractions, c.Kind);
        Assert.NotNull(StopCodes.Meaning(code)); // every listed code is also explained in plain words
    }

    [Fact]
    public void CauseGroups_SecondPossibilities_AndUnknownCode()
    {
        Assert.Equal(CauseGroup.Memory, BlueScreenReports.CausesOf(0x50)!.Secondary);
        Assert.Equal(CauseGroup.Disk, BlueScreenReports.CausesOf(0x133)!.Secondary);
        Assert.Equal(CauseGroup.Files, BlueScreenReports.CausesOf(0x139)!.Secondary);
        Assert.Null(BlueScreenReports.CausesOf(0xDEAD));
        Assert.Equal(CauseGroup.Unknown, BlueScreenReports.GroupOf(0xDEAD));
    }

    [Theory]
    [InlineData("nvlddmkm.sys", "NVIDIA graphics driver", false)]
    [InlineData("NVLDDMKM.SYS", "NVIDIA graphics driver", false)]
    [InlineData("atikmdag.sys", "AMD graphics driver", false)]
    [InlineData("amdkmdag.sys", "AMD graphics driver", false)]
    [InlineData("igdkmd64.sys", "Intel graphics driver", false)]
    [InlineData("Netwtw10.sys", "Intel Wi-Fi driver", false)]
    [InlineData("Netwtw06.sys", "Intel Wi-Fi driver", false)]
    [InlineData("rt640x64.sys", "Realtek network driver", false)]
    [InlineData("rtwlane.sys", "Realtek network driver", false)]
    [InlineData("ntoskrnl.exe", "Windows itself (not a specific driver)", true)]
    [InlineData("C:\\Windows\\System32\\drivers\\nvlddmkm.sys", "NVIDIA graphics driver", false)]
    [InlineData("stornvme.sys", "Windows storage driver", false)]
    public void Modules_GetAFriendlyName(string module, string display, bool windows)
    {
        var d = BlueScreenReports.NameModule(module)!;
        Assert.Equal(display, d.Display);
        Assert.Equal(windows, d.IsWindowsItself);
    }

    [Fact]
    public void Modules_UnknownIsShownAsItsFileName_EmptyIsNull()
    {
        var d = BlueScreenReports.NameModule(@"C:\Windows\System32\drivers\MyVendorXyz.sys")!;
        Assert.Equal("MyVendorXyz.sys", d.Display);
        Assert.Equal("MyVendorXyz.sys", d.Module);
        Assert.False(d.IsWindowsItself);
        Assert.Null(BlueScreenReports.NameModule(null));
        Assert.Null(BlueScreenReports.NameModule("  "));
    }

    [Fact]
    public void Event1001Text_IsParsed()
    {
        const string text = "The computer has rebooted from a bugcheck.  The bugcheck was: 0x00000133 (0x0000000000000001, 0x0000000000001e00, 0x0000000000000000, 0x0000000000000000). A dump was saved in: C:\\Windows\\MEMORY.DMP. Report Id: 6a1e5c4b-1111-2222-3333-444455556666.";
        Assert.Equal(0x133u, StopCodes.Parse(text));
    }

    [Fact]
    public void WerReport_ReadsOnlyTheNeededFields()
    {
        const string wer = "Version=1\r\nEventType=BlueScreen\r\nEventTime=134076000000000000\r\nReportType=4\r\nConsent=1\r\n" +
                           "Sig[0].Name=BCCode\r\nSig[0].Value=1000007e\r\nSig[1].Name=BCP1\r\nSig[1].Value=FFFFFFFFC0000005\r\n" +
                           "DynamicSig[1].Name=OS Version\r\nDynamicSig[1].Value=10.0.26100.2.0.0.256.48\r\n" +
                           "DynamicSig[20].Name=Faulting Module Name\r\nDynamicSig[20].Value=nvlddmkm.sys\r\nUI[2]=C:\\Users\\Someone\\secret.txt\r\n";
        var r = BlueScreenReports.ParseReport(wer)!;
        Assert.Equal("BlueScreen", r.EventType);
        Assert.Equal(0x7Eu, r.Code);
        Assert.Equal("nvlddmkm.sys", r.Module);
        Assert.NotNull(r.WhenUtc);
    }

    [Fact]
    public void WerReport_WithoutModuleAndOtherTypes()
    {
        var live = BlueScreenReports.ParseReport("EventType=LiveKernelEvent\nSig[0].Name=Code\nSig[0].Value=141\nSig[1].Name=Parameter 1\nSig[1].Value=abc\n")!;
        Assert.Equal(0x141u, live.Code);
        Assert.Null(live.Module);
        Assert.Null(BlueScreenReports.ParseReport("EventType=APPCRASH\nSig[0].Name=Application Name\nSig[0].Value=x.exe\n"));
        Assert.Null(BlueScreenReports.ParseReport(""));
        Assert.Null(BlueScreenReports.ParseReport(null));
        Assert.Null(BlueScreenReports.ParseReport("EventType=BlueScreen\nSig[0].Name=BCCode\nSig[0].Value=zz\n")!.Code);
    }

    [Theory]
    [InlineData("0x133", 0x133u)] [InlineData("133", 0x133u)] [InlineData("1000007e", 0x7Eu)] [InlineData("0x10000124", 0x124u)]
    public void ReportCodes_AreCleaned(string text, uint expected) => Assert.Equal(expected, BlueScreenReports.ParseCode(text));

    [Fact]
    public void Modules_AreGroupedMostFrequentFirst_IgnoringFolders()
    {
        var c = BlueScreenReports.CountModules(new string?[] { "nvlddmkm.sys", @"C:\x\NVLDDMKM.sys", "rt640x64.sys", null, "" });
        Assert.Equal(2, c.Count);
        Assert.Equal(2, c[0].Count);
        Assert.Equal("rt640x64.sys", c[1].Module);
    }

    [Fact]
    public void Recurrence_KeepsHappening_Logic()
    {
        var d = new DateTime(2026, 11, 10, 9, 0, 0);
        var one = new[] { new StopCodeCount(0x133, 1) };
        Assert.False(BlueScreenReports.Recur(1, new[] { d }, one).KeepsHappening);
        Assert.False(BlueScreenReports.Recur(2, new[] { d, d.AddHours(1) }, new[] { new StopCodeCount(0x133, 2) }).KeepsHappening);
        Assert.True(BlueScreenReports.Recur(2, new[] { d, d.AddDays(1) }, one).KeepsHappening);
        Assert.True(BlueScreenReports.Recur(3, new[] { d, d, d }, one).KeepsHappening);
        var r = BlueScreenReports.Recur(3, new[] { d, d.AddDays(2), d.AddDays(3) }, new[] { new StopCodeCount(0x133, 3) });
        Assert.Equal(3, r.Days);
        Assert.True(r.SameCodeEachTime);
        Assert.False(BlueScreenReports.Recur(3, new[] { d }, new[] { new StopCodeCount(0x133, 2), new StopCodeCount(0x1A, 1) }).SameCodeEachTime);
    }

    private static HealthState ThreeNvidia()
    {
        var s = Healthy();
        var d = new DateTime(2026, 11, 8, 10, 0, 0);
        s.BlueScreens = 3;
        s.BlueScreenTimes = new[] { d, d.AddDays(2), d.AddDays(4) };
        s.StopCodes = new[] { new StopCodeCount(0x116, 3) };
        s.BlueScreenModules = new[] { new ModuleCount("nvlddmkm.sys", 3) };
        return s;
    }

    [Fact]
    public void Finding_NamesTheDriver_AndSaysItKeepsHappening()
    {
        var (f, l) = Run(ThreeNvidia());
        var m = f.First(x => x.Code == FindingCode.HealthBlueScreens).PlainMessage;
        Assert.Contains("3 times", m);
        Assert.Contains("Each time, Windows named the NVIDIA graphics driver (\"nvlddmkm.sys\") as the cause.", m);
        Assert.Contains("keeps happening, on 3 different days", m);
        Assert.Contains("the same every time", m);
        Assert.Contains(l, r => r.Id == "bluescreen_named_driver" && r.Say.Contains("NVIDIA graphics driver"));
        Assert.Contains(l, r => r.Id == "gpu_clean_driver");
        Assert.Contains(l, r => r.Id == "bluescreen_driver_updates");
        Assert.Contains(l, r => r.Id == "perf_cooling"); // 0x116 may also be heat
    }

    [Fact]
    public void Finding_WindowsItself_DoesNotBlameADriver_AndOnceIsOnce()
    {
        var s = Healthy();
        s.BlueScreens = 1;
        s.StopCodes = new[] { new StopCodeCount(0xEF, 1) };
        s.BlueScreenModules = new[] { new ModuleCount("ntoskrnl.exe", 1) };
        var (f, l) = Run(s);
        var m = f.First(x => x.Code == FindingCode.HealthBlueScreens).PlainMessage;
        Assert.Contains("does not point to one specific driver", m);
        Assert.Contains("only once so far", m);
        Assert.DoesNotContain(l, r => r.Id == "bluescreen_named_driver");
        Assert.Contains(l, r => r.ActionId == "repair.sfc");
        Assert.Contains(l, r => r.ActionId == "disk.scan"); // 0xEF: damaged files or the drive
    }

    [Fact]
    public void Finding_UnknownModule_IsShownAsFileName_AndSomeOfTheTimes()
    {
        var s = ThreeNvidia();
        s.BlueScreenModules = new[] { new ModuleCount("MyVendorXyz.sys", 2) };
        var m = Run(s).F.First(x => x.Code == FindingCode.HealthBlueScreens).PlainMessage;
        Assert.Contains("In 2 of these, Windows named the file \"MyVendorXyz.sys\" as the cause.", m);
    }

    [Fact]
    public void Hardware124_GivesHeatAndPowerAdvice_NotDriverAdvice()
    {
        var s = Healthy();
        s.BlueScreens = 1;
        s.StopCodes = new[] { new StopCodeCount(0x124, 1) };
        var (_, l) = Run(s);
        Assert.Contains(l, r => r.Id == "perf_cooling");
        Assert.Contains(l, r => r.Id == "bluescreen_hardware_settings" && r.Say.Contains("overclocked"));
        Assert.DoesNotContain(l, r => r.Id == "bluescreen_driver_updates");
    }

    [Fact]
    public void Disk77_GivesDiskCheck_Memory1A_GivesMemoryDiagnostic()
    {
        var s = Healthy(); s.BlueScreens = 1;
        s.StopCodes = new[] { new StopCodeCount(0x77, 1) };
        Assert.Contains(Run(s).L, r => r.ActionId == "disk.scan");
        s.StopCodes = new[] { new StopCodeCount(0x1A, 1) };
        Assert.Contains(Run(s).L, r => r.Id == "memory_diagnostic");
    }

    [Fact]
    public void KernelPowerCode_CountsWhenThereIsNoBugcheckEvent()
    {
        var s = Healthy();
        s.KernelPowerBugchecks = 2;
        s.StopCodes = new[] { new StopCodeCount(0x133, 2) };
        var f = HealthTriage.Evaluate(s).First(x => x.Code == FindingCode.HealthBlueScreens);
        Assert.Contains("twice", f.PlainMessage);
        Assert.Contains("0x133", f.PlainMessage);
    }

    [Fact]
    public void Wording_AndRungIds_AreClean()
    {
        var codes = new uint[] { 0x0A, 0xD1, 0x50, 0x1A, 0x19, 0x3B, 0x7E, 0x8E, 0x1E, 0x3D, 0xC2, 0x9F, 0x116, 0x117, 0x124, 0x133, 0x139, 0xEF, 0x7A, 0x77, 0xDEAD };
        foreach (var c in codes)
            foreach (var module in new[] { "nvlddmkm.sys", "ntoskrnl.exe", "Odd.sys" })
            {
                var s = Healthy(); s.BlueScreens = 4; s.BlueScreenTimes = new[] { DateTime.Today, DateTime.Today.AddDays(-3) };
                s.StopCodes = new[] { new StopCodeCount(c, 4) };
                s.BlueScreenModules = new[] { new ModuleCount(module, 3) };
                var (f, l) = Run(s);
                foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)))
                {
                    Assert.DoesNotMatch(Contractions, t);
                    foreach (var sentence in Regex.Split(t, @"(?<=[.])\s+")) Assert.True(sentence.Split(' ').Length <= 60, sentence);
                }
                Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
            }
    }

    [Fact]
    public void ActivityLines_AreOnePerStep_AndFollowTheGuide()
    {
        var got = new List<ActivityItem>();
        void On(ActivityItem i) { if (i.Area == "bsod-test") lock (got) got.Add(i); }
        Activity.Reported += On;
        try { using var _ = Activity.Scope("bsod-test"); RealHealthProbe.ReadStopCodes(new HealthState()); }
        finally { Activity.Reported -= On; }
        Assert.Contains(got, i => i.Text.Contains("Windows Error Reporting"));
        Assert.Contains(got, i => i.Text.Contains("Minidump"));
        foreach (var i in got)
        {
            Assert.DoesNotMatch(Contractions, i.Text);
            Assert.DoesNotContain(Environment.UserName, i.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Reads the real blue screen sources (read-only) on the test bench. Runs only when AFS_LIVE is set.</summary>
[Collection("Live machine")]
public class LiveBlueScreenTests
{
    [Fact]
    public void BlueScreenSources_AreReadable()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var state = new HealthState();
        RealHealthProbe.ReadStopCodes(state);
        var n = HealthTriage.BlueScreenCount(state);
        var lines = new List<string>
        {
            $"kernel-power bugchecks (code not 0): {state.KernelPowerBugchecks}",
            "stop codes: " + string.Join(", ", state.StopCodes.Select(c => $"{StopCodes.Hex(c.Code)} {StopCodes.Meaning(c.Code)?.Name} x{c.Count} ({BlueScreenReports.GroupOf(c.Code)})")),
            "modules: " + string.Join(", ", state.BlueScreenModules.Select(m => $"{m.Module} ({BlueScreenReports.NameModule(m.Module)?.Display}) x{m.Count}")),
            $"blue screen times: {state.BlueScreenTimes.Count}",
            $"minidump files in the last 14 days: {(state.MinidumpCount?.ToString() ?? "not readable")}",
        };
        lines.Add("SENTENCES" + HealthTriage.BlueScreenDriverSentence(state, n) + HealthTriage.BlueScreenRecurrenceSentence(state, n));
        File.WriteAllLines(output, lines);
        Assert.NotNull(state.MinidumpCount);
    }
}
