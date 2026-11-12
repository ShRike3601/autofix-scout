using AutoFixScout.Repair;

namespace AutoFixScout.Probes;

/// <summary>One program that closed unexpectedly (Windows "Application Error", event 1000), grouped by program and module.</summary>
public sealed record CrashInfo(string App, string? Module, string? ExceptionCode, int Count, DateTime LastUtc, string? AppPath = null)
{
    public CrashCause Cause => CrashCauses.Classify(Module, ExceptionCode, App);
}

/// <summary>A Microsoft Store app whose package Windows reports as damaged (Status other than Ok).</summary>
public sealed record StoreAppInfo(string FullName, string Name, string Status);

/// <summary>
/// probe.health: a broad, read-only check of the software problems that sit behind many everyday symptoms.
/// Null or Unknown means "could not be read"; the triage never treats that as healthy.
/// </summary>
public sealed class HealthState
{
    /// <summary>Program crashes in the last 14 days, most frequent first.</summary>
    public IReadOnlyList<CrashInfo> Crashes { get; set; } = new List<CrashInfo>();
    /// <summary>Programs that stopped responding (event 1002), by name, in the last 14 days.</summary>
    public IReadOnlyDictionary<string, int> Hangs { get; set; } = new Dictionary<string, int>();
    public int BlueScreens { get; set; }
    public int UnexpectedShutdowns { get; set; }
    /// <summary>Disk and file-system errors (disk 7/51/153, Ntfs 55/98) in the last 14 days.</summary>
    public int DiskErrors { get; set; }
    /// <summary>Uncorrected hardware errors reported by the processor or memory (WHEA, error level only).</summary>
    public int HardwareErrors { get; set; }
    /// <summary>Programs that could not start because a Microsoft runtime was missing or damaged (SideBySide errors).</summary>
    public IReadOnlyList<string> RuntimeErrors { get; set; } = new List<string>();
    /// <summary>Services that failed to start or stopped unexpectedly at least three times (display names).</summary>
    public IReadOnlyList<string> FailingServices { get; set; } = new List<string>();
    public int UpdateFailures { get; set; }

    /// <summary>Computer clock minus internet time, in seconds. Null = could not be measured (for example offline).</summary>
    public double? ClockOffsetSeconds { get; set; }
    /// <summary>"Set time automatically" is off (W32Time Parameters Type = NoSync).</summary>
    public bool AutoTimeOff { get; set; }

    public bool UserProxyEnabled { get; set; }
    public string? UserProxyServer { get; set; }
    public string? AutoConfigUrl { get; set; }
    /// <summary>Whether the user's proxy (or setup script) answers. Unknown when none is set.</summary>
    public Tri UserProxyReachable { get; set; } = Tri.Unknown;
    /// <summary>The system (WinHTTP) proxy; null = direct (no proxy).</summary>
    public string? WinHttpProxy { get; set; }
    public Tri WinHttpProxyReachable { get; set; } = Tri.Unknown;

    /// <summary>Suspicious hosts-file entries (see <see cref="HostsFile.Suspicious"/>).</summary>
    public IReadOnlyList<HostsHijack> HostsHijacks { get; set; } = new List<HostsHijack>();
    /// <summary>Network add-ons (Winsock providers) that are not part of Windows.</summary>
    public IReadOnlyList<string> ThirdPartyWinsock { get; set; } = new List<string>();
    /// <summary>Windows folders missing from the system PATH.</summary>
    public IReadOnlyList<string> MissingPathEntries { get; set; } = new List<string>();
    public bool TempFolderMissing { get; set; }

    /// <summary>Every service in <see cref="ServiceDefaults.All"/>; Status: Running/Stopped/NotFound/Unknown.</summary>
    public IReadOnlyList<UpdateServiceInfo> Services { get; set; } = new List<UpdateServiceInfo>();
    public IReadOnlyList<StoreAppInfo> BrokenStoreApps { get; set; } = new List<StoreAppInfo>();
    public Tri RebootPending { get; set; } = Tri.Unknown;
    /// <summary>Physical drives (HDD/SSD) with Windows' health verdict.</summary>
    public IReadOnlyList<PhysicalDriveInfo> Drives { get; set; } = new List<PhysicalDriveInfo>();
    /// <summary>Present devices with a Device Manager warning (code 45 "not connected" and phantom devices left out).</summary>
    public IReadOnlyList<DeviceProblem> DeviceProblems { get; set; } = new List<DeviceProblem>();
    /// <summary>Windows signed the person in with a temporary profile (folder name or Application log event 1511/1515 in 2 days).</summary>
    public bool TempProfile { get; set; }
    public string? TempProfileDetail { get; set; }
    /// <summary>The program the person said is the one with the problem (from "A program keeps closing"); null = not asked.</summary>
    public string? ChosenApp { get; set; }

    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

/// <summary>A hosts-file line that sends a well-known site elsewhere (or blocks a security site).</summary>
public sealed record HostsHijack(string Host, string Ip);

/// <summary>A physical drive as Windows Storage reports it. Health: Healthy, Warning, Unhealthy, Unknown.</summary>
public sealed record PhysicalDriveInfo(string Name, string MediaType, string Health, IReadOnlyList<int> OperationalStatus, bool PredictFailure)
{
    /// <summary>Unhealthy, "predictive failure" (5), "error" (6), "non-recoverable error" (7) or Windows' own failure prediction.</summary>
    public bool Failing => Health == "Unhealthy" || PredictFailure || OperationalStatus.Any(s => s is 5 or 6 or 7);
    /// <summary>"Warning" health or "degraded" (3) or "stressed" (4).</summary>
    public bool Warning => !Failing && (Health == "Warning" || OperationalStatus.Any(s => s is 3 or 4));
}

public interface IHealthProbe { HealthState Read(); }
