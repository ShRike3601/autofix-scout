using System.Text.RegularExpressions;
using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class PerfTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
    private const long Gib = 1024L * 1024 * 1024;

    private static RamModule Stick(int gb = 8, int? rated = 3200, int? configured = 3200, int form = 8) =>
        new(gb * Gib, rated, configured, form, 26);

    /// <summary>A healthy desktop: nothing to report.</summary>
    private static PerfState Healthy() => new()
    {
        PerformanceLimitPercent = 100, ProcessorPerformancePercent = 40, CpuName = "AMD Ryzen 5 5600X 6-Core Processor",
        BiosDateUtc = Now.AddDays(-200), ActiveSchemeGuid = PowerTriage.BalancedGuid, OverlayGuid = Guid.Empty.ToString(),
        Ram = new[] { Stick(), Stick() }, RamRead = true, InstabilityRead = true,
    };

    private static bool Has(PerfState s, FindingCode c) => PerfTriage.Evaluate(s, Now).Any(f => f.Code == c);

    [Fact]
    public void Healthy_LooksNormal_WithNoRungs()
    {
        var f = PerfTriage.Evaluate(Healthy(), Now);
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(f).Code);
        Assert.Empty(PerfTriage.Ladder(Healthy(), f));
        Assert.NotNull(PerfTriage.NotFixedHint(f));
    }

    [Fact]
    public void NothingRead_IsProbeIncomplete_AndFailsClosed()
    {
        var s = new PerfState { ProbeErrors = new[] { "x" } };
        var f = PerfTriage.Evaluate(s, Now);
        Assert.Equal(FindingCode.ProbeIncomplete, Assert.Single(f).Code);
        Assert.Empty(PerfTriage.Ladder(s, f));
    }

    [Theory]
    [InlineData(100.0, 0, false)]
    [InlineData(80.0, 0, false)]
    [InlineData(79.0, 0, true)]
    [InlineData(100.0, 2, false)]
    [InlineData(100.0, 3, true)]
    [InlineData(null, 5, true)]
    public void Throttling_Thresholds(double? limit, int events, bool expected)
    {
        var s = Healthy(); s.PerformanceLimitPercent = limit; s.FirmwareLimitEvents = events;
        Assert.Equal(expected, Has(s, FindingCode.PerfProcessorThrottled));
    }

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700KF", true)]
    [InlineData("14th Gen Intel(R) Core(TM) i9-14900KS", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13600", true)]
    [InlineData("14th Gen Intel(R) Core(TM) i7-14700F", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700H", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13980HX", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-1335U", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-1355U", false)]
    [InlineData("14th Gen Intel(R) Core(TM) i5-14500T", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i3-13100", false)]
    [InlineData("12th Gen Intel(R) Core(TM) i9-12900K", false)]
    [InlineData("11th Gen Intel(R) Core(TM) i7-11800H", false)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", false)]
    [InlineData(null, false)]
    public void IntelAffectedDesktop_Detection(string? name, bool expected) =>
        Assert.Equal(expected, PerfTriage.IsIntelAffectedDesktop(name));

    [Fact]
    public void Microcode_ParsedFromUpperFourBytes()
    {
        Assert.Equal(0x125u, PerfTriage.ParseMicrocode(new byte[] { 0, 0, 0, 0, 0x25, 0x01, 0, 0 }));
        Assert.Equal(0x12Bu, PerfTriage.ParseMicrocode(new byte[] { 0x2B, 0x01, 0, 0 }));
        Assert.Null(PerfTriage.ParseMicrocode(new byte[] { 1, 2 }));
        Assert.Null(PerfTriage.ParseMicrocode(null));
    }

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", 0x125u, true)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", 0x12Au, true)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", 0x12Bu, false)]
    [InlineData("14th Gen Intel(R) Core(TM) i7-14700K", 0x129u, true)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700H", 0x100u, false)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", 0u, false)]
    public void Microcode_Compare(string cpu, uint rev, bool tooOld)
    {
        var s = Healthy(); s.CpuName = cpu; s.MicrocodeRevision = rev;
        Assert.Equal(tooOld, PerfTriage.MicrocodeTooOld(cpu, rev));
        Assert.Equal(tooOld, Has(s, FindingCode.PerfIntelMicrocode));
    }

    [Theory]
    [InlineData(3200, 3200, false)]
    [InlineData(3200, 2933, false)]  // 8 percent below
    [InlineData(3200, 2666, true)]
    [InlineData(6000, 4800, true)]
    [InlineData(3200, null, false)]
    public void RamBelowRated_Desktop(int rated, int? configured, bool expected)
    {
        var s = Healthy(); s.Ram = new[] { Stick(rated: rated, configured: configured), Stick(rated: rated, configured: configured) };
        Assert.Equal(expected, Has(s, FindingCode.PerfRamBelowRated));
    }

    [Fact]
    public void RamBelowRated_NotReportedOnLaptopOrSolderedMemory()
    {
        var laptop = Healthy(); laptop.HasBattery = true;
        laptop.Ram = new[] { Stick(rated: 3200, configured: 2400, form: 12), Stick(rated: 3200, configured: 2400, form: 12) };
        Assert.False(Has(laptop, FindingCode.PerfRamBelowRated));
        var soldered = Healthy(); soldered.Ram = new[] { Stick(rated: 4267, configured: 2000, form: 11) };
        Assert.False(Has(soldered, FindingCode.PerfRamBelowRated));
    }

    [Fact]
    public void SingleChannel_OnlyForOneRemovableModule()
    {
        var one = Healthy(); one.Ram = new[] { Stick() };
        Assert.True(Has(one, FindingCode.PerfRamSingleChannel));
        Assert.False(Has(Healthy(), FindingCode.PerfRamSingleChannel));
        var soldered = Healthy(); soldered.Ram = new[] { Stick(form: 11) };
        Assert.False(Has(soldered, FindingCode.PerfRamSingleChannel));
        var unread = Healthy(); unread.Ram = new List<RamModule>(); unread.RamRead = false;
        Assert.False(Has(unread, FindingCode.PerfRamSingleChannel));
    }

    [Fact]
    public void LowRam_Below8Gb()
    {
        var s = Healthy(); s.Ram = new[] { Stick(4), Stick(2) };
        Assert.True(Has(s, FindingCode.PerfLowRam));
        s.Ram = new[] { Stick(4), Stick(4) };
        Assert.False(Has(s, FindingCode.PerfLowRam));
    }

    [Theory]
    [InlineData(-365 * 3 + 1, false)]
    [InlineData(-365 * 3 - 1, true)]
    public void Bios_AgeThreshold(int days, bool old)
    {
        var s = Healthy(); s.BiosDateUtc = Now.AddDays(days);
        Assert.Equal(old, Has(s, FindingCode.PerfBiosOld));
    }

    [Fact]
    public void PowerFindings()
    {
        var saver = Healthy(); saver.ActiveSchemeGuid = PerfTriage.PowerSaverGuid;
        Assert.True(Has(saver, FindingCode.PerfPowerSaver));
        var overlay = Healthy(); overlay.OverlayGuid = PerfTriage.OverlayBestEfficiencyGuid;
        Assert.True(Has(overlay, FindingCode.PerfPowerSaver));
        var battery = Healthy(); battery.HasBattery = true; battery.OnBattery = true;
        Assert.True(Has(battery, FindingCode.PerfOnBattery));
        battery.BatterySaverOn = true;
        Assert.True(Has(battery, FindingCode.PerfBatterySaver));
        Assert.False(Has(battery, FindingCode.PerfOnBattery));
        var plugged = Healthy(); plugged.HasBattery = true; plugged.OnBattery = false;
        Assert.False(Has(plugged, FindingCode.PerfOnBattery));
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 1, 0, true)]
    [InlineData(0, 0, 1, false)]
    [InlineData(0, 0, 2, true)]
    public void Instability_Thresholds(int whea, int blue, int shutdowns, bool expected)
    {
        var s = Healthy(); s.HardwareErrors = whea; s.BlueScreens = blue; s.UnexpectedShutdowns = shutdowns;
        Assert.Equal(expected, Has(s, FindingCode.PerfHardwareInstability));
        Assert.Equal(expected, PerfTriage.Evaluate(s, Now)[0].Code == FindingCode.PerfHardwareInstability);
    }

    [Fact]
    public void Instability_NotReportedWhenNotRead()
    {
        var s = Healthy(); s.InstabilityRead = false; s.HardwareErrors = 5;
        Assert.False(Has(s, FindingCode.PerfHardwareInstability));
    }

    [Fact]
    public void ProbablyHardware_NeedsBlueScreenAndHardwareError()
    {
        var s = Healthy(); s.HardwareErrors = 2;
        Assert.False(PerfTriage.ProbablyHardware(s));
        s.BlueScreens = 1;
        Assert.True(PerfTriage.ProbablyHardware(s));
    }

    private static IEnumerable<PerfState> Everything()
    {
        yield return Healthy();
        yield return new PerfState { ProbeErrors = new[] { "x" } };
        var all = Healthy();
        all.PerformanceLimitPercent = 50; all.FirmwareLimitEvents = 9; all.CpuName = "13th Gen Intel(R) Core(TM) i9-13900K"; all.MicrocodeRevision = 0x125;
        all.BiosDateUtc = new DateTime(2020, 1, 1); all.ActiveSchemeGuid = PerfTriage.PowerSaverGuid; all.Ram = new[] { Stick(4, 3200, 2133) };
        all.HardwareErrors = 3; all.BlueScreens = 2; all.UnexpectedShutdowns = 4;
        yield return all;
        var laptop = Healthy(); laptop.HasBattery = true; laptop.OnBattery = true; laptop.BatterySaverOn = true; laptop.PerformanceLimitPercent = 60;
        yield return laptop;
        var onBattery = Healthy(); onBattery.HasBattery = true; onBattery.OnBattery = true;
        yield return onBattery;
        var ram = Healthy(); ram.Ram = new[] { Stick(rated: 3600, configured: 2133) };
        yield return ram;
    }

    public static IEnumerable<object[]> Sentences()
    {
        var texts = new List<string>();
        foreach (var s in Everything())
        {
            var f = PerfTriage.Evaluate(s, Now);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(PerfTriage.Ladder(s, f).Select(r => r.Say));
            if (PerfTriage.NotFixedHint(f) is { } h) texts.Add(h);
        }
        foreach (var c in new[] { FindingCode.PerfHardwareInstability, FindingCode.PerfIntelMicrocode, FindingCode.PerfProcessorThrottled, FindingCode.LooksNormal })
            if (PerfTriage.NotFixedHint(new[] { new Finding(c, "x", "x") }) is { } h) texts.Add(h);
        return texts.Distinct().Select(t => new object[] { t });
    }

    [Fact]
    public void ScanProducesEnoughSentences() => Assert.True(Sentences().Count() >= 20);

    [Theory]
    [MemberData(nameof(Sentences))]
    public void FollowsWritingGuide(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        foreach (var c in new[] { "n't", "'d ", "'ll ", "I'm", "it's", "that's", "'re ", "'ve " })
            Assert.DoesNotContain(c, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"(^|[\s""(])I\s"), text);
        Assert.DoesNotContain("!", text);
        Assert.DoesNotMatch(new Regex(@"\bwe\b", RegexOptions.IgnoreCase), text);
    }

    [Fact]
    public void RungIds_AreUnique_AndOnlyHumanOrSettings()
    {
        var all = Everything().SelectMany(s => PerfTriage.Ladder(s, PerfTriage.Evaluate(s, Now))).ToList();
        Assert.NotEmpty(all);
        foreach (var s in Everything())
        {
            var ids = PerfTriage.Ladder(s, PerfTriage.Evaluate(s, Now)).Select(r => r.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
        foreach (var g in all.GroupBy(r => r.Id)) Assert.Single(g.Select(r => (r.ActionId, r.Arg, r.Say)).Distinct());
        Assert.All(all, r => { Assert.Equal(0, r.Tier); Assert.True(r.ActionId is "ui.human_step" or "ui.open_settings_page"); Assert.StartsWith("perf_", r.Id); });
        Assert.Contains(all, r => r.Arg == "powersleep");
    }

    [Fact]
    public void Router_SendsPerformanceWordsToPerformance()
    {
        foreach (var t in new[] { "my games stutter", "fps drop in games", "pc is overheating and fans are loud", "performance dropped suddenly", "cpu throttling" })
            Assert.Equal("performance", SymptomRouter.Best(t).Area);
        Assert.True(SymptomRouter.Covers.ContainsKey("performance"));
        Assert.Equal("performance", PerfFlow.Topic);
    }
}

/// <summary>Reads the real machine (read-only). Runs only when AFS_LIVE is set; writes what it found to AFS_LIVE (a file path).</summary>
[Collection("Live machine")]
public class LivePerfTests
{
    [Fact]
    public void PerfProbe_ReadsThisMachine()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new RealPerfProbe().Read();
        var f = PerfTriage.Evaluate(s);
        var lines = new List<string>
        {
            $"probe took {sw.Elapsed.TotalSeconds:0.0} s", "errors: " + string.Join(" | ", s.ProbeErrors),
            $"cpu: {s.CpuName}; microcode: {(s.MicrocodeRevision is { } m ? "0x" + m.ToString("X") : "n/a")}; intel affected desktop: {PerfTriage.IsIntelAffectedDesktop(s.CpuName)}",
            $"performance limit {s.PerformanceLimitPercent}%; processor performance {s.ProcessorPerformancePercent}%; firmware limit events {s.FirmwareLimitEvents}",
            $"bios date {s.BiosDateUtc:yyyy-MM-dd}; plan {s.ActiveSchemeGuid}; overlay {s.OverlayGuid}; battery {s.HasBattery}; on battery {s.OnBattery}; battery saver {s.BatterySaverOn}",
            $"ram: {string.Join(", ", s.Ram.Select(r => $"{r.CapacityBytes >> 30} GB rated {r.RatedSpeed} configured {r.ConfiguredSpeed} form {r.FormFactor} type {r.MemoryType}"))}",
            $"bluescreens {s.BlueScreens}; shutdowns {s.UnexpectedShutdowns}; whea {s.HardwareErrors}",
        };
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage} [{x.TechnicalDetail}]"));
        lines.AddRange(PerfTriage.Ladder(s, f).Select(r => $"RUNG {r.Id} {r.ActionId}({r.Arg})"));
        File.WriteAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
    }
}
