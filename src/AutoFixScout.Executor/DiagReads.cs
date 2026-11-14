using System.Diagnostics.Eventing.Reader;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AutoFixScout.Executor.Shared;

namespace AutoFixScout.Executor;

/// <summary>
/// diag.read_boot_times and diag.read_crash_reports: two READ-ONLY actions that need administrator rights only to read
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
