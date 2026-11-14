namespace AutoFixScout.Probes;

/// <summary>One installed memory module as Windows reports it. Speeds in MT/s (null = not reported).</summary>
public sealed record RamModule(long CapacityBytes, int? RatedSpeed, int? ConfiguredSpeed, int? FormFactor, int? MemoryType)
{
    /// <summary>A removable module (DIMM = 8, SODIMM = 12). Soldered memory is not a module the person can change.</summary>
    public bool Removable => FormFactor is 8 or 12;
}

/// <summary>
/// probe.perf: why a computer feels slow or games stutter. Read-only, no admin. Null = could not be read, never "fine".
/// </summary>
public sealed class PerfState
{
    /// <summary>"% Performance Limit" averaged over a few seconds (100 = not limited); null = counter not available.</summary>
    public double? PerformanceLimitPercent { get; set; }
    /// <summary>"% Processor Performance" over the same time (above 100 = turbo); null = counter not available.</summary>
    public double? ProcessorPerformancePercent { get; set; }
    /// <summary>Kernel-Processor-Power event 37 (speed limited by firmware) in the last 7 days.</summary>
    public int FirmwareLimitEvents { get; set; }

    public string? CpuName { get; set; }
    /// <summary>Microcode revision from "Update Revision" in the registry (upper 32 bits of the stored value).</summary>
    public uint? MicrocodeRevision { get; set; }
    public DateTime? BiosDateUtc { get; set; }

    /// <summary>Active power plan GUID (powercfg /getactivescheme).</summary>
    public string? ActiveSchemeGuid { get; set; }
    /// <summary>Windows 11 power mode (overlay) GUID; null when not available.</summary>
    public string? OverlayGuid { get; set; }
    public bool HasBattery { get; set; }
    /// <summary>Running on battery now; null = unknown.</summary>
    public bool? OnBattery { get; set; }
    public bool? BatterySaverOn { get; set; }

    public IReadOnlyList<RamModule> Ram { get; set; } = new List<RamModule>();
    public bool RamRead { get; set; }

    /// <summary>Blue screens, unexpected shutdowns and uncorrected hardware errors in the last 14 days (shared with the health check).</summary>
    public int BlueScreens { get; set; }
    public int UnexpectedShutdowns { get; set; }
    public int HardwareErrors { get; set; }
    public bool InstabilityRead { get; set; }
    /// <summary>Physical drives Windows Storage lists (0 = none or unreadable); the drive health read is offered only when drives exist.</summary>
    public int PhysicalDriveCount { get; set; }
    /// <summary>diag.read_drive_health already ran in this session.</summary>
    public bool DriveHealthRead { get; set; }

    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();

    public long TotalRamBytes => Ram.Sum(m => m.CapacityBytes);
}

public interface IPerfProbe { PerfState Read(); }
