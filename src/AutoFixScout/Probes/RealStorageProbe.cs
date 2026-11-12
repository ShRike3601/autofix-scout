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
        catch (Exception ex) { errors.Add($"system drive: {ex.Message}"); }

        try
        {
            var (total, old) = TempSize(Path.GetTempPath(), DateTime.UtcNow - TempAge);
            s.UserTempBytes = total;
            s.ReclaimableTempBytes = old;
        }
        catch (Exception) { /* unknown */ }
        s.WindowsTempBytes = WindowsTempSize();
        s.RecycleBinBytes = RecycleBinSize();
        try
        {
            var entries = Repair.StartupApps.ReadAll();
            s.StartupEntries = entries;
            s.StartupAppCount = entries.Count(e => e.Enabled);
        }
        catch (Exception) { /* unknown */ }
        try { s.TopProcesses = ProcessSampler.Sample(TimeSpan.FromSeconds(2), TotalMemory()); } catch (Exception) { /* unknown */ }
        s.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        s.MemoryLoadPercent = MemoryLoad();
        try { s.ProblemDevices = ReadProblemDevices(); } catch (Exception) { /* unknown */ }
        return s;
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
        using var q = new ManagementObjectSearcher(
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
