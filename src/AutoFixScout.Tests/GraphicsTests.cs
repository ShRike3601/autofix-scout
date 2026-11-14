using System.Text.RegularExpressions;
using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class GraphicsParserTests
{
    private const string Row =
        "NVIDIA GeForce RTX 2060, 51, 300, 1950, 405, 7000, 12.50, 80.00, 1, 3, 16, 16, 0x0000000000000001, Not Active, Not Active, Not Active";

    [Fact]
    public void Csv_ParsesAllFields()
    {
        var r = Assert.Single(GraphicsParsers.ParseNvidiaCsv(Row + "\r\n"));
        Assert.Equal("NVIDIA GeForce RTX 2060", r.Name);
        Assert.Equal(51, r.TemperatureC);
        Assert.Equal(1950, r.ClockGraphicsMaxMhz);
        Assert.Equal(12.5, r.PowerDrawW);
        Assert.Equal(3, r.PcieGenMax);
        Assert.Equal(16, r.PcieWidthCurrent);
        Assert.Equal(1UL, r.ThrottleReasons);
        Assert.False(r.ThermalSlowdown);
        Assert.False(r.PowerBrake);
        Assert.False(r.PcieWidthReduced);
    }

    [Fact]
    public void Csv_NotAvailableFieldsBecomeNull()
    {
        var r = Assert.Single(GraphicsParsers.ParseNvidiaCsv(
            "NVIDIA GeForce GTX 1650, 60, 1500, 1900, 3000, 3500, [N/A], [N/A], 3, 3, 16, 16, [N/A], [N/A], [Not Supported], [N/A]"));
        Assert.Null(r.PowerDrawW);
        Assert.Null(r.PowerLimitW);
        Assert.Null(r.ThrottleReasons);
        Assert.Null(r.HwThermalSlowdown);
        Assert.Null(r.SwThermalSlowdown);
        Assert.Null(r.HwPowerBrake);
        Assert.Equal(60, r.TemperatureC);
        Assert.False(r.ThermalSlowdown);
    }

    [Fact]
    public void Csv_ThermalAndPowerReasons()
    {
        var hot = GraphicsParsers.ParseNvidiaCsv("GPU, 88, 900, 1950, 405, 7000, 120, 130, 3, 3, 16, 16, 0x0000000000000040, Active, Not Active, Not Active")[0];
        Assert.True(hot.ThermalSlowdown);
        // Only the reasons mask says it (older tools): bit 0x20 is software thermal slowdown.
        var mask = GraphicsParsers.ParseNvidiaCsv("GPU, 80, 900, 1950, 405, 7000, 120, 130, 3, 3, 16, 16, 0x20, [N/A], [N/A], [N/A]")[0];
        Assert.True(mask.ThermalSlowdown);
        var brake = GraphicsParsers.ParseNvidiaCsv("GPU, 50, 900, 1950, 405, 7000, 120, 130, 3, 3, 16, 16, 0x80, Not Active, Not Active, Active")[0];
        Assert.True(brake.PowerBrake);
    }

    [Fact]
    public void Csv_NameWithCommaAndShortRows()
    {
        var r = GraphicsParsers.ParseNvidiaCsv("NVIDIA RTX A2000, 12GB, 40, 300, 1700, 405, 6000, 10, 70, 1, 4, 16, 16, 0x1, Not Active, Not Active, Not Active");
        Assert.Equal("NVIDIA RTX A2000, 12GB", Assert.Single(r).Name);
        Assert.Empty(GraphicsParsers.ParseNvidiaCsv("No devices were found"));
        Assert.Empty(GraphicsParsers.ParseNvidiaCsv(null));
    }

    [Fact]
    public void PcieWidth_IdleStepsAreNotFlagged()
    {
        NvidiaReading W(int cur, int max) => GraphicsParsers.ParseNvidiaCsv($"G, 40, 300, 1700, 405, 7000, 10, 70, 1, 4, {cur}, {max}, 0x1, Not Active, Not Active, Not Active")[0];
        Assert.True(W(8, 16).PcieWidthReduced);
        Assert.True(W(4, 16).PcieWidthReduced);
        Assert.False(W(1, 16).PcieWidthReduced);
        Assert.False(W(16, 16).PcieWidthReduced);
    }

    [Fact]
    public void Query_UsesBothFieldNames()
    {
        Assert.Contains("clocks_throttle_reasons.hw_power_brake_slowdown", GraphicsParsers.NvidiaQuery("clocks_throttle_reasons"));
        Assert.Contains("clocks_event_reasons.active", GraphicsParsers.NvidiaQuery("clocks_event_reasons"));
        Assert.Equal(GraphicsParsers.NvidiaFields, GraphicsParsers.NvidiaQuery("x").Split(',').Length);
    }

    private const string Memory = @"
GPU 00000000:01:00.0
    FB Memory Usage
        Total                                          : 6144 MiB
        Used                                           : 0 MiB
    BAR1 Memory Usage
        Total                                          : 256 MiB
        Used                                           : 2 MiB
        Free                                           : 254 MiB
";

    [Fact]
    public void Bar1_ParsedAndInterpreted()
    {
        Assert.Equal(256, GraphicsParsers.ParseBar1TotalMiB(Memory));
        Assert.Equal(Tri.No, GraphicsParsers.ResizableBarFromBar1(256));
        Assert.Equal(Tri.Yes, GraphicsParsers.ResizableBarFromBar1(8192));
        Assert.Equal(8192, GraphicsParsers.ParseBar1TotalMiB(Memory.Replace("256 MiB", "8192 MiB")));
        Assert.Null(GraphicsParsers.ParseBar1TotalMiB("BAR1 Memory Usage\n        Total : N/A"));
        Assert.Equal(Tri.Unknown, GraphicsParsers.ResizableBarFromBar1(null));
    }

    [Fact]
    public void Afterburner_StartupSectionWithOffsets()
    {
        var text = "[Startup]\r\nFormat=2\r\nPowerLimit=110\r\nCoreClkBoost=150000\r\nMemClkBoost=400000\r\nCoreVoltageBoost=0\r\n[Settings]\r\nSomething=1\r\n";
        var p = GraphicsParsers.ParseAfterburnerProfile("hw.cfg", text);
        Assert.True(p.InStartupSection);
        Assert.Equal(150000, p.CoreClockKhz);
        Assert.Equal(400000, p.MemoryClockKhz);
        Assert.Equal(0, p.VoltageMv);
        Assert.True(p.HasOffsets);
    }

    [Fact]
    public void Afterburner_ZeroAndEmptyValuesAreNoOverclock()
    {
        var p = GraphicsParsers.ParseAfterburnerProfile("Profile1.cfg", "[Startup]\nFormat=2\nPowerLimit=\nCoreClk=\nMemClk=0\nCoreVoltage=0\nVFCurve=0000\n");
        Assert.False(p.HasOffsets);
        Assert.False(p.InStartupSection);
        Assert.False(GraphicsParsers.ParseAfterburnerProfile("x", null).HasOffsets);
        Assert.False(GraphicsParsers.ParseAfterburnerProfile("x", "CoreClk=abc\nMemClk=1.5").HasOffsets);
    }

    [Fact]
    public void Afterburner_SavedProfileOutsideStartupIsNotApplied_AndCurveCounts()
    {
        var saved = GraphicsParsers.ParseAfterburnerProfile("Profile2.cfg", "[Profile]\nCoreClk=-100000\nCoreVoltage=25\n");
        Assert.False(saved.InStartupSection);
        Assert.Equal(-100000, saved.CoreClockKhz);
        Assert.Equal(25, saved.VoltageMv);
        var curve = GraphicsParsers.ParseAfterburnerProfile("hw.cfg", "[Startup]\nVFCurve=" + Curve(0, 75, 0) + "\n");
        Assert.True(curve.CustomCurve);
        Assert.True(curve.InStartupSection);
    }

    /// <summary>Afterburner's curve format as found on a real install: header (0x20000, count), then (offset, mV, MHz) per point, then a zero block.</summary>
    private static string Curve(params float[] offsets)
    {
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes(0x20000u)); b.AddRange(BitConverter.GetBytes((uint)offsets.Length));
        for (var i = 0; i < offsets.Length; i++)
        {
            b.AddRange(BitConverter.GetBytes(offsets[i])); b.AddRange(BitConverter.GetBytes(450f + i * 6.25f)); b.AddRange(BitConverter.GetBytes(405f));
        }
        b.AddRange(new byte[48]);
        return Convert.ToHexString(b.ToArray());
    }

    [Fact]
    public void Afterburner_StockCurveIsNotAnOverclock()
    {
        // Regression from the bench (2026-11-14): a fresh install stores the stock curve, which is not an overclock.
        Assert.False(GraphicsParsers.VfCurveHasOffsets(Curve(0, 0, 0, 0)));
        Assert.False(GraphicsParsers.ParseAfterburnerProfile("VEN_10DE.cfg", "[Startup]\nFormat=2\nCoreClkBoost=0\nVFCurve=" + Curve(0, 0) + "\nMemClkBoost=0\n").HasOffsets);
        Assert.True(GraphicsParsers.VfCurveHasOffsets(Curve(0, -50, 0)));
        Assert.False(GraphicsParsers.VfCurveHasOffsets("00"));
        Assert.False(GraphicsParsers.VfCurveHasOffsets("zz00020080000000"));
        Assert.False(GraphicsParsers.VfCurveHasOffsets("0000020080000000")); // says 128 points, has none
    }

    [Theory]
    [InlineData("[Settings]\nStartWithWindows=1\n", true)]
    [InlineData("[Settings]\r\nStartWithWindows=0\r\n", false)]
    [InlineData(null, false)]
    public void Afterburner_StartWithWindowsSetting(string? text, bool expected) =>
        Assert.Equal(expected, GraphicsParsers.AfterburnerStartsWithWindows(text));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2060", GpuKind.Dedicated)]
    [InlineData("Intel(R) UHD Graphics", GpuKind.Integrated)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuKind.Integrated)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuKind.Dedicated)]
    [InlineData("AMD Radeon RX 6700 XT", GpuKind.Dedicated)]
    [InlineData("AMD Radeon(TM) Graphics", GpuKind.Integrated)]
    [InlineData("AMD Radeon 780M Graphics", GpuKind.Integrated)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", GpuKind.Integrated)]
    [InlineData("Microsoft Basic Display Adapter", GpuKind.Virtual)]
    [InlineData("Parsec Virtual Display Adapter", GpuKind.Virtual)]
    public void Classify(string name, GpuKind kind) => Assert.Equal(kind, GpuInfo.Classify(name));

    [Fact]
    public void Desktop_FromChassisAndBattery()
    {
        Assert.Equal(Tri.Yes, GraphicsParsers.IsDesktop(new[] { 3 }, false));
        Assert.Equal(Tri.No, GraphicsParsers.IsDesktop(new[] { 3 }, true));
        Assert.Equal(Tri.No, GraphicsParsers.IsDesktop(new[] { 10 }, false));
        Assert.Equal(Tri.Unknown, GraphicsParsers.IsDesktop(Array.Empty<int>(), false));
        Assert.Equal(Tri.Unknown, GraphicsParsers.IsDesktop(new[] { 2 }, false));
    }
}

public class GraphicsTriageTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);

    private static GpuInfo Dgpu(DateTime? date = null, bool display = true, string name = "NVIDIA GeForce RTX 3070") =>
        new(name, "31.0.15.3623", date ?? Now.AddDays(-60), null, GpuKind.Dedicated, display);
    private static GpuInfo Igpu(bool display = false) =>
        new("Intel(R) UHD Graphics 770", "31.0.101.4146", Now.AddDays(-90), null, GpuKind.Integrated, display);

    private static NvidiaReading Nv(int temp = 55, int width = 16, bool thermal = false, bool brake = false, ulong reasons = 1) =>
        new("NVIDIA GeForce RTX 3070", temp, 300, 1950, 405, 7000, 20, 220, 1, 4, width, 16, reasons, thermal, false, brake);

    private static GraphicsState State(params GpuInfo[] gpus) => new() { Gpus = gpus, IsDesktop = Tri.Yes };

    private static OverclockToolState Oc(bool running = true, bool startup = true, int core = 150000) => new()
    {
        AfterburnerInstalled = Tri.Yes, AfterburnerRunning = running ? Tri.Yes : Tri.No, AfterburnerStartsWithWindows = Tri.Yes,
        Profiles = new[] { new AfterburnerProfile("hw.cfg", core, 0, 0, false, startup) },
    };

    private static FindingCode First(GraphicsState s) => GraphicsTriage.Evaluate(s, Now)[0].Code;
    private static bool Has(GraphicsState s, FindingCode c) => GraphicsTriage.Evaluate(s, Now).Any(f => f.Code == c);

    [Fact]
    public void NoGpuOrErrors_IsIncompleteAndFailsClosed()
    {
        var s = new GraphicsState { ProbeErrors = new[] { "video controllers: WMI" } };
        var f = GraphicsTriage.Evaluate(s, Now);
        Assert.Equal(FindingCode.ProbeIncomplete, f[0].Code);
        Assert.Empty(GraphicsTriage.Ladder(s, f));
    }

    [Fact]
    public void Healthy_LooksNormal()
    {
        var s = State(Dgpu(), Igpu());
        s.Nvidia = new[] { Nv() };
        s.ResizableBar = Tri.Yes;
        Assert.Equal(FindingCode.LooksNormal, First(s));
    }

    [Fact]
    public void Overheating_ByFlagOrTemperature()
    {
        var s = State(Dgpu());
        s.Nvidia = new[] { Nv(temp: 70, thermal: true) };
        Assert.Equal(FindingCode.GpuOverheating, First(s));
        s.Nvidia = new[] { Nv(temp: 87) };
        Assert.Equal(FindingCode.GpuOverheating, First(s));
        s.Nvidia = new[] { Nv(temp: 86) };
        Assert.False(Has(s, FindingCode.GpuOverheating));
        Assert.True(GraphicsTriage.ProbablyHardware(new GraphicsState { Nvidia = new[] { Nv(temp: 90) } }));
    }

    [Fact]
    public void ResetsWithOverclock_AreUnstableOverclock_ResetsAlone_AreCleanDriver()
    {
        var s = State(Dgpu());
        s.GraphicsResets = 3;
        s.Tools = Oc();
        Assert.Equal(FindingCode.GpuOverclockUnstable, First(s));
        Assert.Contains(GraphicsTriage.Ladder(s, GraphicsTriage.Evaluate(s, Now)), r => r.Id == "gpu_reset_overclock");
        s.Tools = new OverclockToolState();
        Assert.Equal(FindingCode.GpuDriverResets, First(s));
        var rungs = GraphicsTriage.Ladder(s, GraphicsTriage.Evaluate(s, Now));
        Assert.Contains(rungs, r => r.Id == "gpu_clean_driver" && r.Say.Contains("Perform a clean installation"));
        Assert.DoesNotContain(rungs, r => r.Id == "gpu_reset_overclock");
    }

    [Fact]
    public void OverclockInStartupButNotRunningOrStarting_IsNotActive()
    {
        var s = State(Dgpu());
        var t = Oc(running: false);
        t.AfterburnerStartsWithWindows = Tri.No;
        s.Tools = t;
        s.GraphicsResets = 2;
        Assert.Equal(FindingCode.GpuDriverResets, First(s));
        s.Tools = Oc(startup: false);
        Assert.False(s.Tools.OverclockActive);
        s.GraphicsResets = 0;
        Assert.Equal(FindingCode.GpuOverclockToolInstalled, First(s));
    }

    [Fact]
    public void OverclockActiveWithoutResets_IsReported()
    {
        var s = State(Dgpu());
        s.Tools = Oc();
        Assert.Equal(FindingCode.GpuOverclockActive, First(s));
    }

    [Fact]
    public void PcieWidth_OnlyReducedWidthIsFlagged()
    {
        var s = State(Dgpu());
        s.Nvidia = new[] { Nv(width: 8) };
        Assert.Equal(FindingCode.GpuPcieLinkNarrow, First(s));
        s.Nvidia = new[] { Nv(width: 1) };
        Assert.False(Has(s, FindingCode.GpuPcieLinkNarrow));
        s.Nvidia = new[] { Nv(width: 16) };
        Assert.False(Has(s, FindingCode.GpuPcieLinkNarrow));
    }

    [Fact]
    public void PowerBrake_IsReported() =>
        Assert.Equal(FindingCode.GpuPowerBrake, First(new GraphicsState { Gpus = new[] { Dgpu() }, Nvidia = new[] { Nv(brake: true) } }));

    [Fact]
    public void MonitorOnMotherboard_OnlyOnDesktopWithBothGpus()
    {
        var s = State(Dgpu(display: false), Igpu(display: true));
        Assert.Equal(FindingCode.GpuMonitorOnIntegrated, First(s));
        Assert.Contains(GraphicsTriage.Ladder(s, GraphicsTriage.Evaluate(s, Now)), r => r.Id == "gpu_cable");
        s.IsDesktop = Tri.No; // Optimus laptop: the card renders and the processor graphics shows the picture; that is normal
        Assert.False(Has(s, FindingCode.GpuMonitorOnIntegrated));
        s.IsDesktop = Tri.Unknown;
        Assert.False(Has(s, FindingCode.GpuMonitorOnIntegrated));
        Assert.False(Has(new GraphicsState { Gpus = new[] { Dgpu(display: true), Igpu(display: false) }, IsDesktop = Tri.Yes }, FindingCode.GpuMonitorOnIntegrated));
    }

    [Fact]
    public void OldDriver_OnlyForDedicated()
    {
        var s = State(Dgpu(date: Now.AddDays(-400)));
        Assert.Equal(FindingCode.GpuDriverOld, First(s));
        Assert.False(Has(State(Dgpu(date: Now.AddDays(-300))), FindingCode.GpuDriverOld));
        var old = new GpuInfo("Intel(R) UHD Graphics", "26.20", Now.AddDays(-2000), null, GpuKind.Integrated, true);
        Assert.False(Has(State(old), FindingCode.GpuDriverOld));
    }

    [Fact]
    public void ResizableBar_OnlyWhenKnownOff()
    {
        var s = State(Dgpu());
        s.ResizableBar = Tri.No;
        Assert.Equal(FindingCode.GpuResizableBarOff, First(s));
        Assert.Contains(GraphicsTriage.Ladder(s, GraphicsTriage.Evaluate(s, Now)), r => r.Id == "gpu_enable_rebar" && r.Say.Contains("Above 4G Decoding"));
        s.ResizableBar = Tri.Unknown;
        Assert.Equal(FindingCode.LooksNormal, First(s));
        var amd = State(Dgpu(name: "AMD Radeon RX 6700 XT"));
        amd.ResizableBar = Tri.No; // only NVIDIA can be read; a stray value is never reported for other makers
        Assert.False(Has(amd, FindingCode.GpuResizableBarOff));
    }

    [Fact]
    public void ProbeErrorsWithoutFindings_DoNotLookNormal()
    {
        var s = State(Dgpu());
        s.ProbeErrors = new[] { "nvidia-smi: failed" };
        Assert.Equal(FindingCode.ProbeIncomplete, First(s));
    }

    /// <summary>States that together produce every finding, rung and hint.</summary>
    public static IEnumerable<GraphicsState> StyleStates()
    {
        var a = State(Dgpu(date: Now.AddDays(-500), display: false), Igpu(display: true));
        a.Nvidia = new[] { Nv(temp: 90, thermal: true, brake: true, width: 8) };
        a.ResizableBar = Tri.No;
        a.GraphicsResets = 4;
        a.Tools = Oc();
        yield return a;
        var b = State(Dgpu());
        b.GraphicsResets = 1;
        yield return b;
        var c = State(Dgpu());
        c.Tools = Oc();
        yield return c;
        var d = State(Dgpu());
        d.Tools = new OverclockToolState { OtherTools = new[] { "RivaTuner Statistics Server" } };
        yield return d;
        yield return State(Dgpu());
        yield return new GraphicsState { ProbeErrors = new[] { "x" } };
    }

    public static IEnumerable<string> AllText()
    {
        foreach (var s in StyleStates())
        {
            var f = GraphicsTriage.Evaluate(s, Now);
            foreach (var x in f) yield return x.PlainMessage;
            foreach (var r in GraphicsTriage.Ladder(s, f)) yield return r.Say;
            if (GraphicsTriage.NotFixedHint(f) is { } h) yield return h;
        }
    }

    [Fact]
    public void Text_FollowsWritingGuide()
    {
        var texts = AllText().Distinct().ToList();
        Assert.True(texts.Count >= 15, $"only {texts.Count} sentences");
        var bad = new Regex(@"n't|'ll|'re|'ve|'s |'d |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase);
        foreach (var t in texts) Assert.DoesNotMatch(bad, t);
    }

    [Fact]
    public void Ladder_RungIdsAreUnique_AndOnlyHumanStepsAndSettings()
    {
        foreach (var s in StyleStates())
        {
            var l = GraphicsTriage.Ladder(s, GraphicsTriage.Evaluate(s, Now));
            Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
            Assert.All(l, r => Assert.True(r.Tier == 0 && r.ActionId is "ui.human_step" or "ui.open_settings_page"));
        }
        var all = StyleStates().First();
        var ladder = GraphicsTriage.Ladder(all, GraphicsTriage.Evaluate(all, Now));
        Assert.Contains(ladder, r => r.ActionId == "ui.open_settings_page" && r.Arg == "graphics");
    }

    [Fact]
    public void Router_SendsGraphicsWordsToGraphics()
    {
        foreach (var t in new[] { "my game keeps crashing and the screen has artifacts", "msi afterburner overclock", "gpu driver crashed", "black screen in game" })
            Assert.Equal("graphics", SymptomRouter.Best(t).Area);
        Assert.True(SymptomRouter.Covers.ContainsKey("graphics"));
        Assert.Equal("display", SymptomRouter.Best("second monitor is not detected").Area);
    }
}

/// <summary>Read-only: runs the real graphics probe (AFS_LIVE set). Writes no serials or personal data.</summary>
[Collection("Live machine")]
public class LiveGraphicsTests
{
    [Fact]
    public void GraphicsProbe_ReadsThisMachine()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new RealGraphicsProbe().Read();
        var f = GraphicsTriage.Evaluate(s);
        var lines = new List<string> { $"probe took {sw.Elapsed.TotalSeconds:0.0} s", "errors: " + string.Join(" | ", s.ProbeErrors),
            $"desktop {s.IsDesktop}; nvidia-smi present {s.NvidiaToolPresent}; BAR1 {s.Bar1TotalMiB} MiB; resizable BAR {s.ResizableBar}; graphics resets {s.GraphicsResets}" };
        lines.AddRange(s.Gpus.Select(g => $"GPU {g.Name} driver {g.DriverVersion} {g.DriverDate:yyyy-MM-dd} {g.Kind} drivesDisplay={g.DrivesDisplay} ram={g.AdapterRamBytes}"));
        lines.AddRange(s.Nvidia.Select(n => $"NVIDIA {n.Name} temp={n.TemperatureC} clk={n.ClockGraphicsMhz}/{n.ClockGraphicsMaxMhz} mem={n.ClockMemoryMhz}/{n.ClockMemoryMaxMhz} " +
            $"power={n.PowerDrawW}/{n.PowerLimitW} pcie gen {n.PcieGenCurrent}/{n.PcieGenMax} width {n.PcieWidthCurrent}/{n.PcieWidthMax} reasons=0x{n.ThrottleReasons:X} thermal={n.ThermalSlowdown} brake={n.PowerBrake}"));
        var t = s.Tools;
        lines.Add($"Afterburner installed {t.AfterburnerInstalled} running {t.AfterburnerRunning} startup {t.AfterburnerStartsWithWindows}; profiles {t.Profiles.Count}; other tools: {string.Join("; ", t.OtherTools)}; overclock active {t.OverclockActive}");
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage} [{x.TechnicalDetail}]"));
        lines.AddRange(GraphicsTriage.Ladder(s, f).Select(r => $"RUNG {r.Id} {r.ActionId}({r.Arg})"));
        File.WriteAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
        Assert.NotEmpty(s.Gpus);
    }
}
