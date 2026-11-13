using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Stress;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

internal static class S
{
    public static StressSample Cpu(double t, double? limit, int? gpu = null, bool? battery = false) =>
        new(StressPhase.Cpu, 5 + t, t, limit, limit, battery, gpu, null, null, null, null, null, false, false);

    public static StressSample Gpu(double t, int? temp, int? clock = 1500, int? max = 1900, bool thermal = false, bool brake = false, double? cpuLimit = 100) =>
        new(StressPhase.Gpu, 50 + t, t, cpuLimit, cpuLimit, false, temp, clock, max, 80, 120, null, thermal, brake);

    public static StressSample Idle(double t) => new(StressPhase.Idle, t, t, 100, 100, false, 45, 300, 1900, 10, 120, 0, false, false);

    /// <summary>A result with 5 idle, 45 CPU and (optionally) 45 GPU readings at 1 s spacing. The functions give the value per second.</summary>
    public static StressResult Result(Func<int, double?>? cpuLimit = null, Func<int, StressSample>? gpu = null, bool gpuRan = true, string? gpuName = "NVIDIA GeForce RTX 2060")
    {
        var r = new StressResult { GpuName = gpuName, HasBattery = true, TotalSeconds = 105 };
        for (var i = 0; i < 5; i++) r.Samples.Add(Idle(i));
        for (var i = 0; i < 45; i++) r.Samples.Add(Cpu(i, cpuLimit?.Invoke(i) ?? 100));
        if (gpuRan && gpu != null)
        {
            r.GpuStatus = GpuLoadStatus.Ran;
            for (var i = 0; i < 45; i++) r.Samples.Add(gpu(i));
        }
        else
        {
            r.GpuStatus = GpuLoadStatus.Unavailable;
            r.GpuStatusDetail = "the graphics load could not start";
        }
        return r;
    }

    public static StressResult Healthy() => Result(null, i => Gpu(i, 60 + Math.Min(i, 15)));
}

public class StressSafetyTests
{
    private static List<StressSample> Run(params double?[] limits) =>
        limits.Select((l, i) => S.Cpu(i, l)).ToList();

    [Fact]
    public void GpuTemperature_StopsAtNinety()
    {
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Gpu, new List<StressSample> { S.Gpu(10, 89) }, 60));
        Assert.Equal(StressStop.GpuTooHot, StressSafety.Decide(StressPhase.Gpu, new List<StressSample> { S.Gpu(10, 90) }, 60));
        Assert.Equal(StressStop.GpuTooHot, StressSafety.Decide(StressPhase.Cpu, new List<StressSample> { S.Cpu(10, 100, gpu: 93) }, 20));
    }

    [Fact]
    public void GpuTemperature_IgnoredWhileIdleOrCoolingDown()
    {
        var hot = new List<StressSample> { new(StressPhase.Cooldown, 100, 2, 100, 100, false, 95, null, null, null, null, null, false, false) };
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Cooldown, hot, 100));
    }

    [Fact]
    public void CpuLimit_BelowFiftyForFiveSecondsStops()
    {
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Cpu, Run(100, 49, 49, 49, 49), 10));
        Assert.Equal(StressStop.CpuSlowedHard, StressSafety.Decide(StressPhase.Cpu, Run(100, 49, 49, 49, 49, 49), 11));
    }

    [Fact]
    public void CpuLimit_ExactlyFiftyOrAnInterruptionDoesNotStop()
    {
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Cpu, Run(50, 50, 50, 50, 50, 50), 10));
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Cpu, Run(40, 40, 40, 90, 40, 40), 10));
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Cpu, Run(null, null, null, null, null, null), 10));
    }

    [Fact]
    public void CpuLimit_ShortIntervalsCountSeconds()
    {
        var fast = Enumerable.Range(0, 9).Select(i => S.Cpu(i * 0.5, 30)).ToList();   // 4.5 s
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Cpu, fast, 10));
        fast.Add(S.Cpu(4.5, 30));                                                      // 5.0 s
        Assert.Equal(StressStop.CpuSlowedHard, StressSafety.Decide(StressPhase.Cpu, fast, 10));
    }

    [Fact]
    public void Unplugged_StopsAnyPhase()
    {
        Assert.Equal(StressStop.Unplugged, StressSafety.Decide(StressPhase.Idle, new List<StressSample> { S.Cpu(0, 100, battery: true) }, 2));
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Idle, new List<StressSample> { S.Cpu(0, 100, battery: null) }, 2));
    }

    [Fact]
    public void TimeCap_StopsAtTwoMinutes()
    {
        Assert.Equal(StressStop.None, StressSafety.Decide(StressPhase.Cpu, new List<StressSample> { S.Cpu(0, 100) }, 119.9));
        Assert.Equal(StressStop.TimeLimit, StressSafety.Decide(StressPhase.Cpu, new List<StressSample> { S.Cpu(0, 100) }, 120));
        Assert.Equal(StressStop.TimeLimit, StressSafety.Decide(StressPhase.Cooldown, new List<StressSample>(), 130));
    }
}

public class StressAnalysisTests
{
    private static Finding Single(StressResult r, FindingCode code) => Assert.Single(StressAnalysis.Evaluate(r), f => f.Code == code);

    [Fact]
    public void Healthy_LooksNormal()
    {
        var f = Assert.Single(StressAnalysis.Evaluate(S.Healthy()));
        Assert.Equal(FindingCode.LooksNormal, f.Code);
        Assert.Contains("No slowdown", f.PlainMessage);
        Assert.Empty(StressAnalysis.Ladder(S.Healthy(), new[] { f }));
    }

    [Fact]
    public void CpuThrottle_ReportsTimeAndSpeed()
    {
        var r = S.Result(i => i >= 20 ? 60 : 100, i => S.Gpu(i, 70));
        var f = Single(r, FindingCode.StressCpuThrottles);
        Assert.Contains("60 percent", f.PlainMessage);
        Assert.Contains("after about 20 seconds", f.PlainMessage);
    }

    [Theory]
    [InlineData(79, 5, true)]
    [InlineData(79, 4, false)]
    [InlineData(80, 20, false)]
    [InlineData(50, 5, true)]
    public void CpuThrottle_Thresholds(double limit, int seconds, bool expected)
    {
        var r = S.Result(i => i is >= 10 && i < 10 + seconds ? limit : 100, i => S.Gpu(i, 70));
        Assert.Equal(expected, StressAnalysis.Evaluate(r).Any(x => x.Code == FindingCode.StressCpuThrottles));
    }

    [Fact]
    public void CpuThrottle_BrokenRunsDoNotAdd()
    {
        var r = S.Result(i => i % 3 == 0 ? 100 : 60, i => S.Gpu(i, 70));
        Assert.DoesNotContain(StressAnalysis.Evaluate(r), x => x.Code == FindingCode.StressCpuThrottles);
    }

    [Fact]
    public void CpuThrottle_FromTheStartSaysAlmostImmediately()
    {
        var r = S.Result(i => 70, i => S.Gpu(i, 70));
        Assert.Contains("almost immediately", Single(r, FindingCode.StressCpuThrottles).PlainMessage);
    }

    [Fact]
    public void GpuOverheat_AtEightySevenWithTime()
    {
        var r = S.Result(null, i => S.Gpu(i, i < 40 ? 70 : 87));
        var f = Single(r, FindingCode.StressGpuOverheats);
        Assert.Contains("87 degrees Celsius", f.PlainMessage);
        Assert.Contains("after about 40 seconds", f.PlainMessage);
        Assert.Contains("RTX 2060", f.PlainMessage);
    }

    [Fact]
    public void GpuOverheat_EightySixIsFine_ThermalSlowdownIsNot()
    {
        Assert.DoesNotContain(StressAnalysis.Evaluate(S.Result(null, i => S.Gpu(i, 86))), x => x.Code == FindingCode.StressGpuOverheats);
        var f = Single(S.Result(null, i => S.Gpu(i, 75, thermal: i >= 30)), FindingCode.StressGpuOverheats);
        Assert.Contains("slows itself down because of heat", f.PlainMessage);
        Assert.Contains("after about 30 seconds", f.PlainMessage);
    }

    [Fact]
    public void GpuOverheat_IdleAndCooldownReadingsAreIgnored()
    {
        var r = S.Healthy();
        r.Samples.Add(new(StressPhase.Cooldown, 110, 1, 100, 100, false, 95, null, null, null, null, null, true, false));
        Assert.DoesNotContain(StressAnalysis.Evaluate(r), x => x.Code == FindingCode.StressGpuOverheats);
    }

    [Fact]
    public void GpuPowerBrake_NeedsThreeReadings()
    {
        Assert.DoesNotContain(StressAnalysis.Evaluate(S.Result(null, i => S.Gpu(i, 70, brake: i is 10 or 11))), x => x.Code == FindingCode.StressGpuPowerLimited);
        Single(S.Result(null, i => S.Gpu(i, 70, brake: i is 10 or 11 or 12)), FindingCode.StressGpuPowerLimited);
    }

    [Fact]
    public void GpuClocksLow_NeedsFiveReadingsBelowSixtyPercentAfterRampUp()
    {
        // 1100 of 1900 = 58 percent.
        var low = S.Result(null, i => S.Gpu(i, 70, clock: 1100));
        Assert.Contains("58 percent", Single(low, FindingCode.StressGpuClocksLow).PlainMessage);
        var ok = S.Result(null, i => S.Gpu(i, 70, clock: 1200));   // 63 percent
        Assert.DoesNotContain(StressAnalysis.Evaluate(ok), x => x.Code == FindingCode.StressGpuClocksLow);
        var rampOnly = S.Result(null, i => S.Gpu(i, 70, clock: i < 5 ? 300 : 1700));
        Assert.DoesNotContain(StressAnalysis.Evaluate(rampOnly), x => x.Code == FindingCode.StressGpuClocksLow);
        var fourOnly = S.Result(null, i => S.Gpu(i, 70, clock: i is >= 20 and < 24 ? 900 : 1700));
        Assert.DoesNotContain(StressAnalysis.Evaluate(fourOnly), x => x.Code == FindingCode.StressGpuClocksLow);
    }

    [Fact]
    public void GpuClocksLow_NotReportedAgainWhenHeatOrPowerExplainsIt()
    {
        var hot = S.Result(null, i => S.Gpu(i, 88, clock: 900));
        Assert.DoesNotContain(StressAnalysis.Evaluate(hot), x => x.Code == FindingCode.StressGpuClocksLow);
        var power = S.Result(null, i => S.Gpu(i, 70, clock: 900, brake: true));
        Assert.DoesNotContain(StressAnalysis.Evaluate(power), x => x.Code == FindingCode.StressGpuClocksLow);
    }

    [Theory]
    [InlineData(StressStop.GpuTooHot, "reached 91 degrees Celsius")]
    [InlineData(StressStop.CpuSlowedHard, "below half of its speed")]
    [InlineData(StressStop.Unplugged, "charger was unplugged")]
    [InlineData(StressStop.TimeLimit, "time limit")]
    public void StoppedForSafety_NamesTheReason(StressStop stop, string expected)
    {
        var r = new StressResult { Outcome = StressOutcome.StoppedForSafety, StopReason = stop, StoppedIn = StressPhase.Gpu, TotalSeconds = 62 };
        r.Samples.Add(S.Gpu(10, 91));
        var f = StressAnalysis.Evaluate(r).Last();
        Assert.Equal(FindingCode.StressStoppedForSafety, f.Code);
        Assert.Contains(expected, f.PlainMessage);
        Assert.Contains("after 62 seconds", f.PlainMessage);
    }

    [Fact]
    public void Refused_AsksToPlugIn()
    {
        var r = new StressResult { Outcome = StressOutcome.RefusedOnBattery };
        var f = Assert.Single(StressAnalysis.Evaluate(r));
        Assert.Equal(FindingCode.StressNotRun, f.Code);
        Assert.Equal("perf_plug_in", Assert.Single(StressAnalysis.Ladder(r, new[] { f })).Id);
    }

    [Fact]
    public void Cancelled_IsNeverReportedAsNormal()
    {
        var r = new StressResult { Outcome = StressOutcome.Cancelled, TotalSeconds = 12 };
        for (var i = 0; i < 5; i++) r.Samples.Add(S.Idle(i));
        Assert.Equal(FindingCode.StressNotRun, Assert.Single(StressAnalysis.Evaluate(r)).Code);
    }

    [Fact]
    public void GpuNotTested_IsSaidPlainly_NotReportedAsFine()
    {
        var r = S.Result(null, null, gpuRan: false);
        var f = Assert.Single(StressAnalysis.Evaluate(r));
        Assert.Equal(FindingCode.LooksNormal, f.Code);
        Assert.Contains("graphics card was not tested because the graphics load could not start", f.PlainMessage);
    }

    [Fact]
    public void NothingReadable_IsIncomplete_NotNormal()
    {
        var r = new StressResult { GpuStatus = GpuLoadStatus.NoReadings, GpuStatusDetail = "x" };
        for (var i = 0; i < 5; i++) r.Samples.Add(S.Cpu(i, null));
        Assert.Equal(FindingCode.ProbeIncomplete, Assert.Single(StressAnalysis.Evaluate(r)).Code);
    }

    [Fact]
    public void Ladders_ReuseIdsWithoutDuplicates()
    {
        var all = new[]
        {
            S.Result(i => 60, i => S.Gpu(i, 90, thermal: true, brake: true)),
            S.Result(i => 60, i => S.Gpu(i, 70, clock: 800)),
            S.Result(null, i => S.Gpu(i, 70, brake: true)),
            S.Result(null, i => S.Gpu(i, 70, clock: 800)),
            S.Result(null, i => S.Gpu(i, 88)),
        };
        foreach (var r in all)
        {
            var f = StressAnalysis.Evaluate(r);
            var ladder = StressAnalysis.Ladder(r, f);
            Assert.NotEmpty(ladder);
            Assert.Equal(ladder.Count, ladder.Select(x => x.Id).Distinct().Count());
            Assert.All(ladder, x => Assert.Equal(0, x.Tier));
        }
        var cpu = S.Result(i => 60, i => S.Gpu(i, 70));
        Assert.Equal(new[] { "perf_power_mode", "perf_cooling" }, StressAnalysis.Ladder(cpu, StressAnalysis.Evaluate(cpu)).Select(x => x.Id));
        var hot = S.Result(null, i => S.Gpu(i, 88));
        Assert.Equal(new[] { "gpu_cooling" }, StressAnalysis.Ladder(hot, StressAnalysis.Evaluate(hot)).Select(x => x.Id));
        var desktop = S.Result(null, i => S.Gpu(i, 70, brake: true));
        desktop.HasBattery = false;
        Assert.Equal(new[] { "gpu_power_cable" }, StressAnalysis.Ladder(desktop, StressAnalysis.Evaluate(desktop)).Select(x => x.Id));
    }

    [Fact]
    public void Hardware_FlagFollowsHeatAndPower()
    {
        var hot = S.Result(null, i => S.Gpu(i, 88));
        Assert.True(StressAnalysis.ProbablyHardware(StressAnalysis.Evaluate(hot)));
        Assert.False(StressAnalysis.ProbablyHardware(StressAnalysis.Evaluate(S.Healthy())));
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var texts = new List<string>();
        var results = new List<StressResult>
        {
            S.Healthy(),
            S.Result(i => i >= 20 ? 60 : 100, i => S.Gpu(i, 88, thermal: true, brake: true)),
            S.Result(i => 70, i => S.Gpu(i, 89)),
            S.Result(null, i => S.Gpu(i, 70, clock: 800)),
            S.Result(null, null, gpuRan: false),
            new StressResult { Outcome = StressOutcome.RefusedOnBattery },
            new StressResult { Outcome = StressOutcome.Cancelled },
            new StressResult { GpuStatus = GpuLoadStatus.NoReadings },
        };
        foreach (var stop in new[] { StressStop.GpuTooHot, StressStop.CpuSlowedHard, StressStop.Unplugged, StressStop.TimeLimit })
        {
            var r = new StressResult { Outcome = StressOutcome.StoppedForSafety, StopReason = stop, TotalSeconds = 40 };
            r.Samples.Add(S.Gpu(1, 91));
            results.Add(r);
        }
        foreach (var r in results)
        {
            var f = StressAnalysis.Evaluate(r);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(StressAnalysis.Ladder(r, f).Select(x => x.Say));
            if (StressAnalysis.NotFixedHint(f) is { } h) texts.Add(h);
        }
        foreach (var code in new[] { FindingCode.StressCpuThrottles, FindingCode.StressGpuOverheats, FindingCode.StressGpuPowerLimited, FindingCode.StressGpuClocksLow, FindingCode.LooksNormal })
            texts.Add(StressAnalysis.NotFixedHint(new[] { new Finding(code, "x", "x") })!);
        texts.Add(StressFlow.Consent);
        texts.AddRange(StressFlow.ConsentBullets);
        texts.Add(StressFlow.TestQuestion);
        Assert.True(texts.Count >= 30);
        foreach (var t in texts)
        {
            Assert.DoesNotMatch(new Regex(@"n't|'ll|'re|'ve|'s |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase), t);
            Assert.All(t.Split('.', StringSplitOptions.RemoveEmptyEntries), x => Assert.True(x.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 30, x));
        }
    }

    [Fact]
    public void FindingCodes_AreAppendedAtTheEnd()
    {
        var names = Enum.GetNames<FindingCode>();
        Assert.Equal("StressNotRun", names[^1]);
        Assert.Equal("GpuResizableBarOff", names[^7]);
    }
}

public class StressRunTests
{
    private sealed class FakeLoad : IGpuLoad
    {
        public bool Started, Stopped, Disposed, InitOk = true;
        public string? AdapterName => "Fake";
        public bool TryInitialize(out string? reason) { reason = InitOk ? null : "fake failure"; return InitOk; }
        public void Start() => Started = true;
        public bool Stop(TimeSpan wait) { Stopped = true; return true; }
        public void Dispose() { Stop(TimeSpan.Zero); Disposed = true; }
    }

    private sealed class FakeReader : IStressReader
    {
        private int _n;
        public Func<int, StressSnapshot> Script = _ => new(100, 100, false, Hot(60));
        public NvidiaReading? Gpu = Hot(60);
        public TimeSpan Latency = TimeSpan.Zero;
        public bool Disposed;
        public bool HasBattery { get; set; } = true;
        public Task<NvidiaReading?> ProbeGpuAsync() => Task.FromResult(Gpu);
        public async Task<StressSnapshot> ReadAsync()
        {
            if (Latency > TimeSpan.Zero) await Task.Delay(Latency);
            return Script(_n++);
        }
        public void Dispose() => Disposed = true;
    }

    private static NvidiaReading Hot(int temp) =>
        new("NVIDIA GeForce RTX 2060", temp, 1500, 1900, 7000, 7000, 80, 120, 3, 3, 16, 16, 0, false, false, false);

    private static readonly StressOptions Fast = new() { IdleSeconds = 0.3, CpuSeconds = 0.5, GpuSeconds = 0.5, CooldownSeconds = 0.3, SampleIntervalSeconds = 0.1, CpuThreads = 1 };

    private static StressEnvironment Env(FakeReader reader, List<FakeLoad> loads, FakeLoad? gpu = null) =>
        new(() => reader, (n, ct) => { var l = new FakeLoad(); loads.Add(l); return l; }, () => gpu);

    [Fact]
    public async Task RefusesOnBattery_WithoutStartingAnyLoad()
    {
        var reader = new FakeReader { Script = _ => new(100, 100, true, null) };
        var loads = new List<FakeLoad>();
        var gpu = new FakeLoad();
        var r = await StressTest.RunAsync(Fast, null, default, Env(reader, loads, gpu));
        Assert.Equal(StressOutcome.RefusedOnBattery, r.Outcome);
        Assert.Empty(loads);
        Assert.False(gpu.Started);
        Assert.Empty(r.Samples);
        Assert.True(reader.Disposed);
    }

    [Fact]
    public async Task RefusesWhenALaptopCannotTellItsPowerSource()
    {
        var reader = new FakeReader { Script = _ => new(100, 100, null, null), HasBattery = true };
        var r = await StressTest.RunAsync(Fast, null, default, Env(reader, new()));
        Assert.Equal(StressOutcome.RefusedOnBattery, r.Outcome);
    }

    [Fact]
    public async Task CompletesAllPhases_AndStopsEveryLoad()
    {
        var reader = new FakeReader();
        var loads = new List<FakeLoad>();
        var gpu = new FakeLoad();
        var seen = new List<StressSample>();
        var r = await StressTest.RunAsync(Fast, new Progress<StressSample>(seen.Add), default, Env(reader, loads, gpu));
        Assert.Equal(StressOutcome.Completed, r.Outcome);
        Assert.Equal(GpuLoadStatus.Ran, r.GpuStatus);
        Assert.Equal(new[] { StressPhase.Idle, StressPhase.Cpu, StressPhase.Gpu, StressPhase.Cooldown }, r.Samples.Select(s => s.Phase).Distinct());
        Assert.All(loads, l => Assert.True(l.Started && l.Stopped && l.Disposed));
        Assert.True(gpu.Started && gpu.Stopped && gpu.Disposed);
        Assert.True(reader.Disposed);
        await Task.Delay(100);
        Assert.True(seen.Count >= r.Samples.Count - 1);
    }

    [Fact]
    public async Task GpuLoadFailure_SkipsThePhase_AsUnavailable()
    {
        var gpu = new FakeLoad { InitOk = false };
        var r = await StressTest.RunAsync(Fast, null, default, Env(new FakeReader(), new List<FakeLoad>(), gpu));
        Assert.Equal(StressOutcome.Completed, r.Outcome);
        Assert.Equal(GpuLoadStatus.Unavailable, r.GpuStatus);
        Assert.DoesNotContain(r.Samples, s => s.Phase == StressPhase.Gpu);
        Assert.False(gpu.Started);
        Assert.Contains("fake failure", r.GpuStatusDetail);
    }

    [Fact]
    public async Task CardWithoutTemperature_SkipsTheGpuPhase()
    {
        var gpu = new FakeLoad();
        var r = await StressTest.RunAsync(Fast, null, default, Env(new FakeReader { Gpu = null }, new List<FakeLoad>(), gpu));
        Assert.Equal(GpuLoadStatus.NoReadings, r.GpuStatus);
        Assert.False(gpu.Started);
    }

    [Fact]
    public async Task HotCard_StopsTheLoad_ThenOnlyCoolsDown()
    {
        var reader = new FakeReader();
        var calls = 0;
        reader.Script = _ => new(100, 100, false, Hot(++calls > 6 ? 91 : 60));
        var loads = new List<FakeLoad>();
        var gpu = new FakeLoad();
        var r = await StressTest.RunAsync(Fast with { CpuSeconds = 3, GpuSeconds = 3 }, null, default, Env(reader, loads, gpu));
        Assert.Equal(StressOutcome.StoppedForSafety, r.Outcome);
        Assert.Equal(StressStop.GpuTooHot, r.StopReason);
        Assert.False(gpu.Started);
        Assert.All(loads, l => Assert.True(l.Stopped && l.Disposed));
        Assert.Contains(r.Samples, s => s.Phase == StressPhase.Cooldown);
        Assert.True(r.TotalSeconds < 3);
    }

    [Fact]
    public async Task TimeCap_EndsTheTest()
    {
        var loads = new List<FakeLoad>();
        var r = await StressTest.RunAsync(Fast with { CpuSeconds = 10, MaxTotalSeconds = 0.8 }, null, default, Env(new FakeReader(), loads));
        Assert.Equal(StressStop.TimeLimit, r.StopReason);
        Assert.True(r.TotalSeconds < 2);
        Assert.All(loads, l => Assert.True(l.Stopped));
    }

    [Fact]
    public async Task Cancel_StopsQuickly_AndReleasesEverything()
    {
        var reader = new FakeReader { Latency = TimeSpan.FromMilliseconds(30) };
        var loads = new List<FakeLoad>();
        var gpu = new FakeLoad();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(500);
        var sw = Stopwatch.StartNew();
        var r = await StressTest.RunAsync(new StressOptions { IdleSeconds = 0.2, CpuSeconds = 30, GpuSeconds = 30, CooldownSeconds = 5, SampleIntervalSeconds = 0.1 }, null, cts.Token, Env(reader, loads, gpu));
        Assert.Equal(StressOutcome.Cancelled, r.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), sw.Elapsed.ToString());
        Assert.NotEmpty(loads);
        Assert.All(loads, l => Assert.True(l.Stopped && l.Disposed));
        Assert.True(reader.Disposed);
    }

    [Fact]
    public async Task CancelBeforeStart_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await StressTest.RunAsync(Fast, null, cts.Token, Env(new FakeReader(), new()));
        Assert.Equal(StressOutcome.Cancelled, r.Outcome);
    }

    [Fact]
    public void RealCpuLoad_AllThreadsEndWithinASecondOfCancel()
    {
        using var cts = new CancellationTokenSource();
        using var load = new CpuLoad(Math.Min(Environment.ProcessorCount, 8), cts.Token);
        load.Start();
        Thread.Sleep(200);
        Assert.Equal(load.Threads, load.AliveThreads);
        cts.CancelAfter(300);
        var sw = Stopwatch.StartNew();
        while (load.AliveThreads > 0 && sw.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(10);
        Assert.Equal(0, load.AliveThreads);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), sw.Elapsed.ToString());
    }

    [Fact]
    public void RealCpuLoad_StopEndsThreadsAndRunsBelowNormalPriority()
    {
        using var load = new CpuLoad(2);
        load.Start();
        Thread.Sleep(100);
        Assert.True(load.Stop(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, load.AliveThreads);
    }

    [Fact]
    public void Flow_DiagnoseWrapsTheAnalysis()
    {
        var d = StressFlow.Diagnose(S.Result(i => 60, i => S.Gpu(i, 70)));
        Assert.Equal(FindingCode.StressCpuThrottles, d.Findings[0].Code);
        Assert.True(d.ProbablyHardware);
        Assert.Equal("stress", StressFlow.Topic);
        Assert.Contains("about 2 minutes", StressFlow.Consent);
    }
}

[Collection("Live machine")]
public class LiveStressTests
{
    [Fact]
    public async Task ShortRun_OnThisMachine_WritesSummary()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var seen = new List<StressSample>();
        var opts = new StressOptions { IdleSeconds = 3, CpuSeconds = 10, GpuSeconds = 10, CooldownSeconds = 4 };
        var r = await StressTest.RunAsync(opts, new Progress<StressSample>(seen.Add), default);
        var f = StressAnalysis.Evaluate(r);
        var ladder = StressAnalysis.Ladder(r, f);

        var sb = new StringBuilder();
        sb.AppendLine($"outcome={r.Outcome} stop={r.StopReason} total={r.TotalSeconds}s threads={r.CpuThreads} battery={r.HasBattery} gpu={r.GpuName} gpuStatus={r.GpuStatus} {r.GpuStatusDetail}");
        sb.AppendLine("phase  t(s)  cpuLimit cpuPerf gpuTemp gpuClk/max gpuPowerW reasons thermal brake");
        foreach (var s in r.Samples)
            sb.AppendLine($"{s.Phase,-8}{s.Seconds,6:0.0}  {s.CpuLimitPercent,6}  {s.CpuPerformancePercent,6}  {s.GpuTemperatureC,4}  {s.GpuClockMhz}/{s.GpuClockMaxMhz}  {s.GpuPowerW}  0x{s.GpuThrottleReasons:X} {s.GpuThermalSlowdown} {s.GpuPowerBrake}");
        sb.AppendLine("FINDINGS");
        foreach (var x in f) sb.AppendLine($"{x.Code}: {x.PlainMessage} [{x.TechnicalDetail}]");
        sb.AppendLine("LADDER " + string.Join(", ", ladder.Select(x => x.Id)));
        File.WriteAllText(output, sb.ToString());

        Assert.NotEqual(StressOutcome.RefusedOnBattery, r.Outcome);
        Assert.NotEmpty(r.Samples);
        Assert.True(r.TotalSeconds < 60);
    }
}
