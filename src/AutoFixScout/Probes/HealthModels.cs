using AutoFixScout.Triage;
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
    /// <summary>Stop codes of the blue screens in the last 14 days, most frequent first (empty when none could be read).</summary>
    public IReadOnlyList<StopCodeCount> StopCodes { get; set; } = new List<StopCodeCount>();
    /// <summary>Modules (driver files) that Windows named in blue screen reports in the last 14 days, most frequent first. Often empty: the reports can need administrator rights.</summary>
    public IReadOnlyList<ModuleCount> BlueScreenModules { get; set; } = new List<ModuleCount>();
    /// <summary>When the blue screens happened (local time), from the System log.</summary>
    public IReadOnlyList<DateTime> BlueScreenTimes { get; set; } = new List<DateTime>();
    /// <summary>Kernel-Power 41 events that carry a stop code (the code is not 0), in the last 14 days.</summary>
    public int KernelPowerBugchecks { get; set; }
    /// <summary>Files in the Windows Minidump folder; null when the folder could not be listed.</summary>
    public int? MinidumpCount { get; set; }
    /// <summary>The page file (virtual memory) settings and size; null = could not be read.</summary>
    public PageFileState? PageFile { get; set; }
    /// <summary>Resource-Exhaustion-Detector event 2004 ("low virtual memory") in the last 14 days.</summary>
    public int LowMemoryEvents { get; set; }
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

    /// <summary>Programs, Windows updates and drivers installed in the last 7 days, newest first.</summary>
    public IReadOnlyList<RecentChange> RecentChanges { get; set; } = new List<RecentChange>();
    /// <summary>Dates (UTC) of System Restore points. Unknown = not readable without administrator rights.</summary>
    public Tri RestorePointsKnown { get; set; } = Tri.Unknown;
    public IReadOnlyList<DateTime> RestorePoints { get; set; } = new List<DateTime>();
    /// <summary>Graphics driver resets in the last 14 days (Display event 4101, LiveKernelEvent 141 or 117).</summary>
    public int GraphicsResets { get; set; }

    /// <summary>USB disconnects, hub errors and selective suspend (last 3 days); null = could not be read.</summary>
    public UsbState? Usb { get; set; }

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

public enum RecentChangeKind { Program, Update, Driver }

/// <summary>Something installed in the last week. WhenUtc is the install date (programs record the day only).</summary>
public sealed record RecentChange(RecentChangeKind Kind, string Name, DateTime WhenUtc);

/// <summary>Page file as Windows reports it. Sizes in MB. Automatic = null when it could not be read.</summary>
public sealed record PageFileState(bool? Automatic, long TotalRamMb, IReadOnlyList<PageFileSetting> Settings, IReadOnlyList<PageFileUse> Usage)
{
    /// <summary>Total size of the page files that exist now.</summary>
    public long AllocatedMb => Usage.Sum(u => u.AllocatedMb);
    public bool Exists => Usage.Count > 0;
}

/// <summary>Win32_PageFileSetting: the size the person set by hand (0 = system chooses).</summary>
public sealed record PageFileSetting(string Name, long InitialMb, long MaximumMb);

/// <summary>Win32_PageFileUsage: a page file that exists, its size and how much is in use.</summary>
public sealed record PageFileUse(string Name, long AllocatedMb, long CurrentUsageMb);
