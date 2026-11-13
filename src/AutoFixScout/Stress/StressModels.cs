namespace AutoFixScout.Stress;

public enum StressPhase { Idle, Cpu, Gpu, Cooldown }

/// <summary>Why the test ended before its planned length. None = it was not cut short.</summary>
public enum StressStop { None, GpuTooHot, CpuSlowedHard, Unplugged, TimeLimit }

public enum StressOutcome { Completed, Cancelled, StoppedForSafety, RefusedOnBattery }

/// <summary>Whether the graphics card load ran. NoReadings = the card cannot report its temperature, so loading it would show nothing.</summary>
public enum GpuLoadStatus { NotAttempted, Ran, Unavailable, NoReadings }

/// <summary>One reading, taken about once a second. Null = could not be read (never "fine").</summary>
public sealed record StressSample(
    StressPhase Phase, double Seconds, double PhaseSeconds,
    double? CpuLimitPercent, double? CpuPerformancePercent, bool? OnBattery,
    int? GpuTemperatureC, int? GpuClockMhz, int? GpuClockMaxMhz, double? GpuPowerW, double? GpuPowerLimitW,
    ulong? GpuThrottleReasons, bool GpuThermalSlowdown, bool GpuPowerBrake);

public sealed record StressOptions
{
    public double IdleSeconds { get; init; } = 5;
    public double CpuSeconds { get; init; } = 45;
    public double GpuSeconds { get; init; } = 45;
    public double CooldownSeconds { get; init; } = 10;
    /// <summary>Hard cap for the whole test (2 minutes).</summary>
    public double MaxTotalSeconds { get; init; } = 120;
    public double SampleIntervalSeconds { get; init; } = 1;
    /// <summary>0 = one thread per logical core.</summary>
    public int CpuThreads { get; init; }
}

public sealed class StressResult
{
    public List<StressSample> Samples { get; init; } = new();
    public StressOutcome Outcome { get; set; } = StressOutcome.Completed;
    public StressStop StopReason { get; set; } = StressStop.None;
    public StressPhase? StoppedIn { get; set; }
    public double TotalSeconds { get; set; }
    public double SampleIntervalSeconds { get; set; } = 1;
    public int CpuThreads { get; set; }
    public bool HasBattery { get; set; }
    public GpuLoadStatus GpuStatus { get; set; } = GpuLoadStatus.NotAttempted;
    /// <summary>Why the graphics card load did not run (plain, for the person).</summary>
    public string? GpuStatusDetail { get; set; }
    /// <summary>Name of the card that was read and loaded.</summary>
    public string? GpuName { get; set; }

    public IEnumerable<StressSample> In(StressPhase p) => Samples.Where(s => s.Phase == p);
    public bool CpuReadable => Samples.Any(s => s.Phase is StressPhase.Cpu && s.CpuLimitPercent != null);
    public bool GpuReadable => Samples.Any(s => s.GpuTemperatureC != null);
}
