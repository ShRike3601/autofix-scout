namespace AutoFixScout.Stress;

/// <summary>The stop rules of the stress test as one pure function, so they can be tested without running any load.</summary>
public static class StressSafety
{
    public const int GpuStopCelsius = 90;
    public const double CpuStopBelowPercent = 50;
    public const double CpuStopForSeconds = 5;

    /// <summary>
    /// Decides after each reading. phaseSamples = the readings of the current phase so far (oldest first).
    /// Only load phases (Cpu, Gpu) can stop for heat or speed; an unplugged charger and the time cap stop any phase.
    /// </summary>
    public static StressStop Decide(StressPhase phase, IReadOnlyList<StressSample> phaseSamples, double totalSeconds, double maxTotalSeconds = 120)
    {
        if (totalSeconds >= maxTotalSeconds) return StressStop.TimeLimit;
        if (phaseSamples.Count == 0) return StressStop.None;
        var last = phaseSamples[^1];
        if (last.OnBattery == true) return StressStop.Unplugged;
        if (phase is not (StressPhase.Cpu or StressPhase.Gpu)) return StressStop.None;
        if (last.GpuTemperatureC >= GpuStopCelsius) return StressStop.GpuTooHot;

        // Trailing run of readings below 50 percent; its length in seconds counts one step beyond the last reading.
        int i = phaseSamples.Count - 1;
        while (i >= 0 && phaseSamples[i].CpuLimitPercent is < CpuStopBelowPercent) i--;
        var run = phaseSamples.Count - 1 - i;
        if (run >= 1)
        {
            var first = phaseSamples[i + 1];
            var step = run >= 2 ? phaseSamples[^1].Seconds - phaseSamples[^2].Seconds : 1.0;
            if (last.Seconds - first.Seconds + step >= CpuStopForSeconds - 1e-6) return StressStop.CpuSlowedHard;
        }
        return StressStop.None;
    }
}
