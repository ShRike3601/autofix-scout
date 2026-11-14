using System.Management;
using System.Runtime.InteropServices;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.storage: read-only, no admin, never throws. Each reading fails on its own (null) so one unreadable part does
/// not hide the rest; only the system drive itself is required.
/// </summary>
public sealed class RealStorageProbe : IStorageProbe
{
    /// <summary>Temporary files older than this may be cleared (storage.clear_user_temp uses the same age).</summary>
    public static readonly TimeSpan TempAge = TimeSpan.FromHours(24);

    private static readonly string[] StorageClasses = { "DiskDrive", "SCSIAdapter", "HDC" };

    public StorageState Read()
    {
        var errors = new List<string>();
        var s = new StorageState { ProbeErrors = errors, WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
        try
        {
            (s.SystemDrive, s.FreeBytes, s.TotalBytes) = ReadSystemDrive();
        }
        catch (Exception ex) { AppLog.Warn("Storage probe", ex); errors.Add($"system drive: {ex.Message}"); }

        // The readings are independent (each sets its own fields), so they run side by side: the 2 second process
        // sample, the folder walks and the WMI/event log reads used to add up one after another.
        var tasks = new List<Task>
        {
            Task.Run(() =>
            {
                try
                {
                    Activity.Report(ActivityKind.Files, "Measuring the temporary folder %LOCALAPPDATA%\\Temp (sizes and ages only, 24 hours is the cut-off)");
                    var (total, old) = TempSize(Path.GetTempPath(), DateTime.UtcNow - TempAge);
                    s.UserTempBytes = total;
                    s.ReclaimableTempBytes = old;
                }
                catch (Exception) { /* unknown */ }
            }),
            Task.Run(() => s.WindowsTempBytes = WindowsTempSize()),
            Task.Run(() =>
            {
                Activity.Report(ActivityKind.Files, "Asking Windows for the Recycle Bin size (SHQueryRecycleBin)");
                s.RecycleBinBytes = RecycleBinSize();
            }),
            Task.Run(() => ReadBigUsers(s)),
            Task.Run(() =>
            {
                try
                {
                    var entries = Repair.StartupApps.ReadAll();
                    s.StartupEntries = entries;
                    s.StartupAppCount = entries.Count(e => e.Enabled);
                }
                catch (Exception) { /* unknown */ }
            }),
            Task.Run(() => { try { s.TopProcesses = ProcessSampler.Sample(TimeSpan.FromSeconds(2), TotalMemory()); } catch (Exception) { /* unknown */ } }),
            Task.Run(() =>
            {
                Activity.Report(ActivityKind.Analyze, "Reading start-up timings from the last 30 days");
                s.BootTimes = StartupTimings.ReadBoots();
                s.SlowStartupItems = StartupTimings.ReadSlowItems();
                s.FastStartup = StartupTimings.ReadFastStartup();
            }),
            Task.Run(() => { try { s.ProblemDevices = ReadProblemDevices(); } catch (Exception) { /* unknown */ } }),
        };
        Task.WaitAll(tasks.ToArray());
        ElevatedDiagnostics.ApplyTo(s); // start-up data the elevated helper read earlier in this session
        s.PhysicalDriveCount = RealHealthProbe.CountPhysicalDrives();
        Activity.Report(ActivityKind.Analyze, $"Read the storage and start-up details{(errors.Count > 0 ? $"; {Activity.Count(errors.Count, "part")} could not be read" : "")}");
        s.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        s.MemoryLoadPercent = MemoryLoad();
        return s;
    }

    public static readonly TimeSpan OldDownload = TimeSpan.FromDays(90);

    /// <summary>Big space users, each on its own so one failure only leaves that part Unknown. Read-only, no admin.</summary>
    private static void ReadBigUsers(StorageState s)
    {
        var root = s.SystemDrive;
        var b = SpaceWalker.DefaultBudget;
        try
        {
            Activity.Report(ActivityKind.Files, "Checking for the Windows.old folder and measuring it (sizes only)");
            var w = Path.Combine(root, "Windows.old");
            s.WindowsOld = Directory.Exists(w) ? SpaceWalker.Folder(w, b) : new SpaceItem(0, false);
        }
        catch (Exception) { /* unknown */ }
        Activity.Report(ActivityKind.Files, "Reading the size of hiberfil.sys (file information only)");
        try { s.HiberFile = SpaceWalker.FileSize(Path.Combine(root, "hiberfil.sys")); } catch (Exception) { /* unknown */ }
        try
        {
            if (SpaceWalker.DownloadsPath() is { } dl && Directory.Exists(dl))
            {
                Activity.Report(ActivityKind.Files, "Measuring %USERPROFILE%\\Downloads (file sizes and ages only, no names)");
                var r = SpaceWalker.Measure(dl, b, DateTime.UtcNow - OldDownload);
                if (r.TopReadable) { s.Downloads = new SpaceItem(r.Bytes, !r.Complete); s.DownloadsOldCount = r.OldFiles; }
            }
        }
        catch (Exception) { /* unknown */ }
        try
        {
            Activity.Report(ActivityKind.Files, "Measuring the Delivery Optimization cache (sizes only)");
            var parts = new[]
            {
                Path.Combine(s.WindowsDirectory, @"SoftwareDistribution\DeliveryOptimization"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\DeliveryOptimization\Cache"),
            }.Select(p => SpaceWalker.Folder(p, b)).Where(x => x is not null).ToList();
            if (parts.Count > 0) s.DeliveryOptimization = new SpaceItem(parts.Sum(x => x!.Bytes), parts.Any(x => x!.AtLeast));
        }
        catch (Exception) { /* unknown */ }
        try
        {
            Activity.Report(ActivityKind.Files, "Measuring the largest folders in %USERPROFILE% (sizes only, folder names are not recorded here)");
            s.BigProfileFolders = SpaceWalker.BigProfileFolders(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12));
        }
        catch (Exception) { /* unknown */ }
    }

    /// <summary>The drive Windows runs from. Shared with the Windows Update check.</summary>
    public static (string Root, long Free, long Total) ReadSystemDrive()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var d = new DriveInfo(root);
        return (root, d.AvailableFreeSpace, d.TotalSize);
    }

    /// <summary>Size of a folder tree without following junctions or links out of it; inaccessible parts are skipped.</summary>
    public static (long Total, long OlderThanCutoff) TempSize(string dir, DateTime cutoffUtc)
    {
        long total = 0, old = 0;
        var opts = new System.IO.EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
        {
            try
            {
                total += f.Length;
                if (f.LastWriteTimeUtc < cutoffUtc) old += f.Length;
            }
            catch (Exception) { /* file vanished meanwhile */ }
        }
        return (total, old);
    }

    /// <summary>C:\Windows\Temp size if this account may list it; null otherwise (a partial sum would look like "small").</summary>
    private static long? WindowsTempSize()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            Activity.Report(ActivityKind.Files, "Measuring C:\\Windows\\Temp if this account may list it (sizes only)");

            _ = Directory.EnumerateFileSystemEntries(dir).FirstOrDefault(); // throws when access is denied
            return TempSize(dir, DateTime.MinValue).Total;
        }
        catch (Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    internal static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    /// <summary>Recycle Bin size over all drives; null if Windows does not answer.</summary>
    public static long? RecycleBinSize()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : null;
        }
        catch (Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static int? MemoryLoad()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref m) ? (int)m.dwMemoryLoad : null;
        }
        catch (Exception) { return null; }
    }

    private static long? TotalMemory()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : null;
        }
        catch (Exception) { return null; }
    }

    private static List<StorageDeviceInfo> ReadProblemDevices()
    {
        var list = new List<StorageDeviceInfo>();
        using var q = Wmi.Searcher(
            "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        foreach (var d in q.Get())
        {
            var cls = d["PNPClass"] as string ?? "";
            var code = Convert.ToInt32(d["ConfigManagerErrorCode"] ?? 0);
            // 45 = not connected right now (an unplugged USB disk), not a fault.
            if (!StorageClasses.Contains(cls, StringComparer.OrdinalIgnoreCase) || code is 0 or 45) continue;
            list.Add(new StorageDeviceInfo(d["Name"] as string ?? "(unnamed)", d["DeviceID"] as string ?? "", cls, code));
        }
        return list;
    }
}
