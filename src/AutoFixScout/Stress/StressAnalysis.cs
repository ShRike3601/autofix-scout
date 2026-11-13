using AutoFixScout.Triage;

namespace AutoFixScout.Stress;

/// <summary>
/// Turns the readings of a stress test into findings and a ladder. Nothing is changed on the computer; the rungs are the
/// ones the performance and graphics checks already use (same ids and sentences), so a person sees one wording everywhere.
/// An unreadable value never counts as "no slowdown". A test without any slowdown reports LooksNormal.
/// </summary>
public static class StressAnalysis
{
    /// <summary>"% Performance Limit" below this percent counts as the processor being slowed down (same as the performance check).</summary>
    public const double CpuThrottledBelowPercent = PerfTriage.ThrottledBelowPercent;
    /// <summary>The slowdown must last at least this long.</summary>
    public const double CpuThrottledForSeconds = 5;
    public const int GpuHotCelsius = GraphicsTriage.HotCelsius;
    /// <summary>The power brake must be reported in at least this many readings.</summary>
    public const int PowerBrakeMinSamples = 3;
    /// <summary>Under full load a graphics clock below this fraction of its maximum counts as far too low.</summary>
    public const double ClockLowFraction = 0.60;
    public const int ClockLowMinSamples = 5;
    /// <summary>Readings in the first seconds of the load are ignored for the clock check (the card is still ramping up).</summary>
    public const double ClockRampSeconds = 5;

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private static string Seconds(double s) => s < 1.5 ? "less than a second" : $"{Math.Round(s)} seconds";

    private static string After(double s) => s < 1.5 ? "almost immediately" : $"after about {Math.Round(s)} seconds";

    private static bool IsLoad(StressSample s) => s.Phase is StressPhase.Cpu or StressPhase.Gpu;

    /// <summary>Longest run of consecutive Cpu-phase readings below the throttle limit that lasts long enough; null when none.</summary>
    internal static (double StartSeconds, double Length, double Lowest)? CpuSlowdown(StressResult r)
    {
        var cpu = r.In(StressPhase.Cpu).ToList();
        (double, double, double)? best = null;
        for (var i = 0; i < cpu.Count; i++)
        {
            if (cpu[i].CpuLimitPercent is not < CpuThrottledBelowPercent) continue;
            var j = i;
            var lowest = cpu[i].CpuLimitPercent!.Value;
            while (j + 1 < cpu.Count && cpu[j + 1].CpuLimitPercent is < CpuThrottledBelowPercent)
            {
                j++;
                lowest = Math.Min(lowest, cpu[j].CpuLimitPercent!.Value);
            }
            var length = cpu[j].Seconds - cpu[i].Seconds + r.SampleIntervalSeconds;
            if (length >= CpuThrottledForSeconds - 1e-6 && (best == null || length > best.Value.Item2))
                best = (cpu[i].PhaseSeconds, length, lowest);
            i = j;
        }
        return best;
    }

    public static IReadOnlyList<Finding> Evaluate(StressResult r)
    {
        var f = new List<Finding>();

        if (r.Outcome == StressOutcome.RefusedOnBattery)
        {
            f.Add(F(FindingCode.StressNotRun,
                "The test did not start because the computer is running on battery. Please plug in the charger and run the test again.",
                "refused: on battery or power source unknown"));
            return f;
        }

        if (CpuSlowdown(r) is { } cpu)
            f.Add(F(FindingCode.StressCpuThrottles,
                $"The processor slows down under load. It dropped to about {Math.Round(cpu.Lowest)} percent of its speed {After(cpu.StartSeconds)} of full work.",
                $"cpu limit lowest={cpu.Lowest:0}% from phase second {cpu.StartSeconds:0.#} for {cpu.Length:0.#} s (threshold <{CpuThrottledBelowPercent:0}% for {CpuThrottledForSeconds:0} s)"));

        var load = r.Samples.Where(IsLoad).ToList();
        var name = r.GpuName is { Length: > 0 } n ? n : "graphics card";

        var hotFirst = load.FirstOrDefault(s => s.GpuThermalSlowdown || s.GpuTemperatureC >= GpuHotCelsius);
        var overheats = hotFirst != null;
        if (hotFirst != null)
        {
            var peak = load.Max(s => s.GpuTemperatureC) ?? 0;
            var when = hotFirst.Phase == StressPhase.Gpu ? "of full graphics work" : "of full processor work";
            var thermal = load.Any(s => s.GpuThermalSlowdown);
            var temp = thermal ? hotFirst.GpuTemperatureC ?? peak : peak;
            f.Add(F(FindingCode.StressGpuOverheats,
                thermal
                    ? $"The graphics card \"{name}\" slows itself down because of heat. It reached {temp} degrees Celsius {After(hotFirst.PhaseSeconds)} {when}."
                    : $"The graphics card \"{name}\" gets very hot. It reached {peak} degrees Celsius {After(hotFirst.PhaseSeconds)} {when}, and many cards begin to slow down at this temperature.",
                $"first at phase {hotFirst.Phase} second {hotFirst.PhaseSeconds:0.#}: temp={hotFirst.GpuTemperatureC} thermalSlowdown={hotFirst.GpuThermalSlowdown} peak={peak} C"));
        }

        var brake = load.Count(s => s.GpuPowerBrake);
        var powerLimited = brake >= PowerBrakeMinSamples;
        if (powerLimited)
            f.Add(F(FindingCode.StressGpuPowerLimited,
                $"The graphics card \"{name}\" was slowed down because it did not receive enough power. On a laptop, a charger that is too weak is a common reason.",
                $"power brake in {brake} readings (minimum {PowerBrakeMinSamples})"));

        if (!overheats && !powerLimited)
        {
            var ramped = r.In(StressPhase.Gpu).Where(s => s.PhaseSeconds >= ClockRampSeconds && s.GpuClockMhz > 0 && s.GpuClockMaxMhz > 0).ToList();
            var low = ramped.Where(s => s.GpuClockMhz < s.GpuClockMaxMhz * ClockLowFraction).ToList();
            if (low.Count >= ClockLowMinSamples && low.Count * 2 >= ramped.Count)
            {
                var pct = Math.Round(low.Average(s => (double)s.GpuClockMhz! / s.GpuClockMaxMhz!.Value) * 100);
                f.Add(F(FindingCode.StressGpuClocksLow,
                    $"The graphics card \"{name}\" ran at only about {pct} percent of its top speed under full load, although it reported no heat or power limit.",
                    $"clock below {ClockLowFraction * 100:0}% of max in {low.Count} of {ramped.Count} readings, average {pct}%"));
            }
        }

        if (r.Outcome == StressOutcome.StoppedForSafety)
        {
            var at = Seconds(r.TotalSeconds);
            var lastGpu = r.Samples.LastOrDefault(s => s.GpuTemperatureC != null)?.GpuTemperatureC;
            var text = r.StopReason switch
            {
                StressStop.GpuTooHot => $"The test was stopped after {at} because the graphics card reached {lastGpu} degrees Celsius. This protects the computer.",
                StressStop.CpuSlowedHard => $"The test was stopped after {at} because the processor dropped below half of its speed. This protects the computer.",
                StressStop.Unplugged => $"The test was stopped after {at} because the charger was unplugged. Please plug in the charger and run the test again.",
                _ => $"The test was stopped after {at} because it reached its time limit of 2 minutes.",
            };
            f.Add(F(FindingCode.StressStoppedForSafety, text, $"stop={r.StopReason} in {r.StoppedIn} total={r.TotalSeconds:0.#} s"));
        }

        if (f.Count > 0) return f;

        if (r.Outcome == StressOutcome.Cancelled)
        {
            f.Add(F(FindingCode.StressNotRun, "The test was stopped before it finished, so no result is available. It can be run again at any time.",
                $"cancelled after {r.TotalSeconds:0.#} s"));
            return f;
        }

        var cpuRead = r.CpuReadable;
        var gpuRan = r.GpuStatus == GpuLoadStatus.Ran && r.In(StressPhase.Gpu).Any(s => s.GpuTemperatureC != null);
        if (!cpuRead && !gpuRan)
        {
            f.Add(F(FindingCode.ProbeIncomplete,
                "AutoFix Scout could not read the speed of the processor or the temperature of the graphics card during the test, so no result is available.",
                $"cpuReadable={cpuRead} gpuStatus={r.GpuStatus} {r.GpuStatusDetail}"));
            return f;
        }

        var parts = new List<string> { "No slowdown was found during the test." };
        parts.Add(cpuRead ? "The processor kept its speed under full work." : "The speed of the processor could not be read.");
        parts.Add(gpuRan
            ? $"The graphics card stayed below {GpuHotCelsius} degrees Celsius and reported no heat or power limit."
            : $"The graphics card was not tested because {r.GpuStatusDetail ?? "no load could be applied to it"}.");
        var cpuMin = r.In(StressPhase.Cpu).Min(s => s.CpuLimitPercent);
        var gpuMax = r.Samples.Max(s => s.GpuTemperatureC);
        f.Add(F(FindingCode.LooksNormal, string.Join(" ", parts),
            $"lowest cpu limit={cpuMin?.ToString("0") ?? "n/a"}% peak gpu temp={gpuMax?.ToString() ?? "n/a"} C gpu={r.GpuStatus}"));
        return f;
    }

    private static void AddOnce(List<Rung> rungs, Rung rung)
    {
        if (rungs.All(x => x.Id != rung.Id)) rungs.Add(rung);
    }

    /// <summary>Least risky first. Every rung is a Settings page or a step for the person; nothing is changed.</summary>
    public static IReadOnlyList<Rung> Ladder(StressResult r, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(f => f.Code).ToHashSet();
        var rungs = new List<Rung>();
        if (codes.Contains(FindingCode.StressNotRun) || codes.Contains(FindingCode.ProbeIncomplete))
        {
            if (r.Outcome == StressOutcome.RefusedOnBattery) rungs.Add(PerfTriage.PlugInRung());
            return rungs;
        }
        if (r.StopReason == StressStop.Unplugged) AddOnce(rungs, PerfTriage.PlugInRung());
        if (codes.Contains(FindingCode.StressCpuThrottles) || codes.Contains(FindingCode.StressGpuClocksLow) || (codes.Contains(FindingCode.StressGpuPowerLimited) && r.HasBattery))
            AddOnce(rungs, PerfTriage.PowerModeRung());
        if (codes.Contains(FindingCode.StressCpuThrottles))
            AddOnce(rungs, PerfTriage.CoolingRung());
        else if (codes.Contains(FindingCode.StressGpuOverheats))
            AddOnce(rungs, GraphicsTriage.CoolingRung());
        if (codes.Contains(FindingCode.StressGpuPowerLimited) && !r.HasBattery)
            AddOnce(rungs, GraphicsTriage.PowerCableRung());
        if (codes.Contains(FindingCode.StressGpuClocksLow))
            AddOnce(rungs, GraphicsTriage.CleanDriverRung());
        return rungs;
    }

    /// <summary>Heat or a power limit that shows up under load is a matter for the hardware.</summary>
    public static bool ProbablyHardware(IReadOnlyList<Finding> findings) =>
        findings.Any(f => f.Code is FindingCode.StressCpuThrottles or FindingCode.StressGpuOverheats or FindingCode.StressGpuPowerLimited);

    public static string? NotFixedHint(IReadOnlyList<Finding> after)
    {
        if (after.Count == 0) return null;
        return after[0].Code switch
        {
            FindingCode.StressCpuThrottles => "The processor still slows down under load. Dust in the cooler or old thermal paste is the most likely cause. A helper can clean the computer and check its cooling.",
            FindingCode.StressGpuOverheats => "The graphics card still gets too hot under load. Dust, a failing fan or old thermal paste are the most likely causes. A helper can check the cooling with you.",
            FindingCode.StressGpuPowerLimited => "The graphics card is still held back by its power supply. A helper can check the charger, the power supply and the cables.",
            FindingCode.StressGpuClocksLow => "The graphics card still runs far below its top speed under load. A helper can look at its driver and settings with you.",
            FindingCode.LooksNormal => "No slowdown was found under load. If the computer still feels slow, please run the performance check while the problem is happening.",
            _ => null,
        };
    }
}
