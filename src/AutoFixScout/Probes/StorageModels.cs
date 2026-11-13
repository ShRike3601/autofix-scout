namespace AutoFixScout.Probes;

/// <summary>A disk or storage controller (DiskDrive, SCSIAdapter, HDC classes) that reports a Device Manager problem code.</summary>
public sealed record StorageDeviceInfo(string Name, string InstanceId, string PnpClass, int ProblemCode);

public enum StartupScope { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

/// <summary>One app that starts at sign-in: a Run value (Name = value name) or a Startup folder file (Name = file name).</summary>
public sealed record StartupEntry(string Name, string Command, StartupScope Scope, bool Enabled);

/// <summary>
/// A running program (all its processes together) over a ~2 second sample. CpuPercent is of the whole computer
/// (all cores); MemoryPercent is its working set against installed memory. Path null = could not be read.
/// </summary>
public sealed record ProcessUsage(string Name, string? Path, double CpuPercent, long WorkingSetBytes, double MemoryPercent);

/// <summary>
/// probe.storage: system drive space, temporary files, startup apps, uptime, memory and storage devices.
/// Null means "could not be read", never "fine". Only an unreadable system drive is a probe error, because every
/// other reading is optional context.
/// </summary>
public sealed class StorageState
{
    /// <summary>Root of the drive Windows is installed on, e.g. "C:\".</summary>
    public string SystemDrive { get; set; } = @"C:\";
    /// <summary>The Windows folder; startup entries and programs under it are Windows' own and never offered.</summary>
    public string WindowsDirectory { get; set; } = @"C:\Windows";
    public long? FreeBytes { get; set; }
    public long? TotalBytes { get; set; }
    /// <summary>Everything in the person's %TEMP% folder.</summary>
    public long? UserTempBytes { get; set; }
    /// <summary>The part of %TEMP% older than 24 hours, which storage.clear_user_temp may delete.</summary>
    public long? ReclaimableTempBytes { get; set; }
    /// <summary>C:\Windows\Temp, read only (usually unreadable without admin, then null).</summary>
    public long? WindowsTempBytes { get; set; }
    public long? RecycleBinBytes { get; set; }
    /// <summary>Big space users, each read on its own; null = Unknown (denied or not readable). AtLeast = a lower bound.</summary>
    public SpaceItem? WindowsOld { get; set; }
    public SpaceItem? HiberFile { get; set; }
    public SpaceItem? Downloads { get; set; }
    /// <summary>Files in Downloads not changed for 90 days or more.</summary>
    public int? DownloadsOldCount { get; set; }
    public SpaceItem? DeliveryOptimization { get; set; }
    /// <summary>The three largest folders directly in the profile (sizes and folder names only).</summary>
    public IReadOnlyList<ProfileFolder>? BigProfileFolders { get; set; }
    /// <summary>Enabled entries in the Run keys and Startup folders.</summary>
    public int? StartupAppCount { get; set; }
    /// <summary>Every Run and Startup folder entry with its "Startup apps" state; null = could not be read.</summary>
    public IReadOnlyList<StartupEntry>? StartupEntries { get; set; }
    /// <summary>Busiest programs that are not part of Windows, by processor and by memory; null = could not be read.</summary>
    public IReadOnlyList<ProcessUsage>? TopProcesses { get; set; }
    /// <summary>Boots of the last 30 days (Diagnostics-Performance event 100), newest first; null = the log could not be read (Unknown), empty = no boot recorded.</summary>
    public IReadOnlyList<BootRecord>? BootTimes { get; set; }
    /// <summary>Apps, drivers and services Windows named as slowing start-up, slowest first; null = Unknown.</summary>
    public IReadOnlyList<SlowStartupItem>? SlowStartupItems { get; set; }
    /// <summary>Fast Startup switch; null = Unknown.</summary>
    public bool? FastStartup { get; set; }
    public TimeSpan? Uptime { get; set; }
    public int? MemoryLoadPercent { get; set; }
    /// <summary>Disks and storage controllers with a problem code; null = the device list could not be read.</summary>
    public IReadOnlyList<StorageDeviceInfo>? ProblemDevices { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IStorageProbe { StorageState Read(); }
