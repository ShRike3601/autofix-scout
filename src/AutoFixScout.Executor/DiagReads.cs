using System.Diagnostics.Eventing.Reader;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AutoFixScout.Executor.Shared;

namespace AutoFixScout.Executor;

/// <summary>
/// diag.read_boot_times, diag.read_crash_reports and diag.read_drive_health: three READ-ONLY actions that need administrator rights only to read
/// protected logs and report folders. They take no argument, change nothing on the computer, and write one JSON file
/// with a fixed name (see DiagResults) that the app validates and deletes. Exit codes: 0 written, 1 failed, 3 nothing to report.
/// The values go through the same parsers the app uses, and a privacy filter drops any text that names a user folder.
/// </summary>
internal static class DiagReads
{
    private const int MaxSlowEvents = 300, MaxReportFolders = 200;
    private const long MaxReportBytes = 512 * 1024;

    // ---- boot times ----

    internal static int BootTimes()
    {
        var now = DateTime.UtcNow;
        var boots = new List<BootRecord>();
        var slow = new List<SlowStartupItem>();
        var window = DiagResults.Days * 24L * 3600 * 1000;
        using (var r = Open($"*[System[EventID=100 and TimeCreated[timediff(@SystemTime) <= {window}]]]"))
            while (boots.Count < DiagResults.MaxBoots && r.ReadEvent() is { } e)
                using (e) { if (StartupTimings.ParseBoot(e.ToXml()) is { } b) boots.Add(b); }
        using (var r = Open($"*[System[(EventID>=101 and EventID<=110) and TimeCreated[timediff(@SystemTime) <= {window}]]]"))
            for (var i = 0; i < MaxSlowEvents && r.ReadEvent() is { } e; i++)
                using (e) { if (StartupTimings.ParseSlowItem(e.ToXml()) is { } s) slow.Add(s); }

        var result = DiagResults.CleanBoots(boots, slow, now);
        if (result.Boots.Count == 0 && result.SlowItems.Count == 0) { RemoveStale(DiagResults.BootAction); return 3; }
        WriteResult(DiagResults.FileFor(DiagResults.BootAction), DiagResults.Serialize(result));
        return 0;
    }

    private static EventLogReader Open(string xpath) =>
        new(new EventLogQuery(StartupTimings.Log, PathType.LogName, xpath) { ReverseDirection = true }); // newest first

    // ---- crash reports ----

    private static string WerRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER");

    internal static int CrashReports()
    {
        var now = DateTime.UtcNow;
        var found = new List<CrashReport>();
        var total = 0;
        foreach (var sub in new[] { "ReportArchive", "ReportQueue" })
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(Path.Combine(WerRoot, sub)); }
            catch (Exception) { continue; } // a missing folder is not an error
            foreach (var dir in dirs)
            {
                if (!IsKernelReportFolder(Path.GetFileName(dir))) continue;
                if (total++ >= MaxReportFolders) break;
                try
                {
                    if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // never follow a link
                    var file = new FileInfo(Path.Combine(dir, "Report.wer"));
                    if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaxReportBytes) continue;
                    if (file.LastWriteTimeUtc < now.AddDays(-DiagResults.Days)) continue;
                    // Only the fields BlueScreenReports parses are kept; nothing else of the report, and no dump file, is read.
                    var parsed = BlueScreenReports.ParseReport(File.ReadAllText(file.FullName));
                    if (parsed is null) continue;
                    found.Add(new CrashReport(parsed.EventType, parsed.Code, parsed.Module, parsed.WhenUtc ?? file.LastWriteTimeUtc));
                }
                catch (Exception) { /* one unreadable report must not stop the others */ }
            }
        }
        var result = DiagResults.CleanCrashes(found, now);
        if (result.Reports.Count == 0) { RemoveStale(DiagResults.CrashAction); return 3; }
        WriteResult(DiagResults.FileFor(DiagResults.CrashAction), DiagResults.Serialize(result));
        return 0;
    }

    internal static bool IsKernelReportFolder(string name) =>
        name.StartsWith("Kernel", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LiveKernel", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("BlueScreen", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Critical", StringComparison.OrdinalIgnoreCase);

    // ---- drive health ----

    /// <summary>
    /// diag.read_drive_health: MSFT_PhysicalDisk and its MSFT_StorageReliabilityCounter (root\Microsoft\Windows\Storage). Without
    /// administrator rights the disks list but the counters are denied or empty. The serial number is never selected or written.
    /// Exit 0 when at least one drive reported a counter, 3 when none did.
    /// </summary>
    internal static int DriveHealth()
    {
        var now = DateTime.UtcNow;
        var found = new List<DriveHealthRecord>();
        using var disks = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT * FROM MSFT_PhysicalDisk"); // the whole object is needed to follow the association; only the six listed properties are ever read
        foreach (System.Management.ManagementObject disk in disks.Get())
            using (disk)
            {
                if (found.Count >= DiagResults.MaxDrives) break;
                System.Management.ManagementObject? counter = null;
                try
                {
                    foreach (System.Management.ManagementObject c in disk.GetRelated("MSFT_StorageReliabilityCounter")) { counter = c; break; }
                }
                catch (System.Management.ManagementException) { /* denied or unsupported for this drive: its counters stay Unknown */ }
                using (counter)
                {
                    if (counter is null) continue;
                    found.Add(new DriveHealthRecord(
                        Int(disk["DeviceId"]) is { } id ? (int)id : null,
                        (disk["FriendlyName"] as string ?? "").Trim(), MediaName(disk["MediaType"]), BusName(disk["BusType"]), HealthName(disk["HealthStatus"]),
                        Int(disk["Size"]),
                        Int(counter["Wear"]) is { } w ? (int)w : null, Int(counter["Temperature"]) is { } t ? (int)t : null, Int(counter["TemperatureMax"]) is { } tm ? (int)tm : null,
                        Int(counter["ReadErrorsTotal"]), Int(counter["ReadErrorsUncorrected"]), Int(counter["WriteErrorsTotal"]), Int(counter["WriteErrorsUncorrected"]),
                        Int(counter["PowerOnHours"]), Int(counter["StartStopCycleCount"])));
                }
            }
        var result = DiagResults.CleanDrives(found, now);
        // A drive that reported nothing at all (every counter null) is not worth a file.
        result.Drives.RemoveAll(d => d.WearPercent is null && d.TemperatureC is null && d.TemperatureMaxC is null && d.ReadErrorsUncorrected is null
            && d.WriteErrorsUncorrected is null && d.PowerOnHours is null && d.ReadErrorsTotal is null && d.WriteErrorsTotal is null && d.StartStopCycles is null);
        if (result.Drives.Count == 0) { RemoveStale(DiagResults.DriveAction); return 3; }
        WriteResult(DiagResults.FileFor(DiagResults.DriveAction), DiagResults.Serialize(result));
        return 0;
    }

    private static long? Int(object? v)
    {
        try { return v is null ? null : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception) { return null; }
    }

    private static string MediaName(object? v) => Int(v) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" };
    private static string HealthName(object? v) => Int(v) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
    private static string BusName(object? v) => Int(v) switch { 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 17 => "NVMe", _ => "Other" };

    // ---- the result file ----

    private static void RemoveStale(string actionId)
    {
        try { var p = DiagResults.FileFor(actionId); if (File.Exists(p)) File.Delete(p); } catch (Exception) { }
    }

    /// <summary>
    /// Writes the fixed result file as a new file (an old file or link of that name is removed first, never followed), with
    /// an ACL that lets Administrators and SYSTEM do everything and every user read and delete it, and nobody else write.
    /// </summary>
    internal static void WriteResult(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) File.Delete(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute | FileSystemRights.Delete, AccessControlType.Allow));
        security.SetOwner(admins);
        using var fs = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadPermissions, FileShare.None, 4096, FileOptions.None, security);
        fs.Write(new UTF8Encoding(false).GetBytes(json));
    }
}
