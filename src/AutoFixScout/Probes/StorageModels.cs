namespace AutoFixScout.Probes;

/// <summary>A disk or storage controller (DiskDrive, SCSIAdapter, HDC classes) that reports a Device Manager problem code.</summary>
public sealed record StorageDeviceInfo(string Name, string InstanceId, string PnpClass, int ProblemCode);

/// <summary>
/// probe.storage: system drive space, temporary files, startup apps, uptime, memory and storage devices.
/// Null means "could not be read", never "fine". Only an unreadable system drive is a probe error, because every
/// other reading is optional context.
/// </summary>
public sealed class StorageState
{
    /// <summary>Root of the drive Windows is installed on, e.g. "C:\".</summary>
    public string SystemDrive { get; set; } = @"C:\";
    public long? FreeBytes { get; set; }
    public long? TotalBytes { get; set; }
    /// <summary>Everything in the person's %TEMP% folder.</summary>
    public long? UserTempBytes { get; set; }
    /// <summary>The part of %TEMP% older than 24 hours, which storage.clear_user_temp may delete.</summary>
    public long? ReclaimableTempBytes { get; set; }
    /// <summary>C:\Windows\Temp, read only (usually unreadable without admin, then null).</summary>
    public long? WindowsTempBytes { get; set; }
    public long? RecycleBinBytes { get; set; }
    /// <summary>Enabled entries in the Run keys and Startup folders.</summary>
    public int? StartupAppCount { get; set; }
    public TimeSpan? Uptime { get; set; }
    public int? MemoryLoadPercent { get; set; }
    /// <summary>Disks and storage controllers with a problem code; null = the device list could not be read.</summary>
    public IReadOnlyList<StorageDeviceInfo>? ProblemDevices { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IStorageProbe { StorageState Read(); }
