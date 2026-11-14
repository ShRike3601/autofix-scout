using AutoFixScout.Probes;

namespace AutoFixScout;

/// <summary>
/// App side of the two read-only elevated actions (diag.read_boot_times, diag.read_crash_reports). After the helper
/// finishes, the result file is read, validated (it is untrusted input), deleted, kept for this session and merged into
/// the probe states, so the next check sees the data. Nothing here changes the computer.
/// </summary>
public static class ElevatedDiagnostics
{
    private static readonly object Gate = new();
    private static DiagBootResult? _boots;
    private static DiagCrashResult? _crashes;

    /// <summary>Forgets what was read (tests and a new session).</summary>
    public static void Reset() { lock (Gate) { _boots = null; _crashes = null; } }

    /// <summary>The validated data of the last run in this session, or null when it has not run.</summary>
    public static DiagBootResult? Boots { get { lock (Gate) return _boots; } }
    public static DiagCrashResult? Crashes { get { lock (Gate) return _crashes; } }

    /// <summary>
    /// Reads the result file of a finished helper run and deletes it. Exit code 0 = data was written, 3 = nothing to report
    /// (a known empty result); anything else = nothing is used. Returns whether the data could be used, and the sentence for the helper report.
    /// </summary>
    public static (bool Ok, string Detail) Finish(string actionId, int exitCode, string? folder = null)
    {
        var path = Path.Combine(folder ?? DiagResults.Folder, actionId == DiagResults.BootAction ? DiagResults.BootFile : DiagResults.CrashFile);
        var text = exitCode == 0 ? ReadAndDelete(path) : DeleteOnly(path);
        var boots = actionId == DiagResults.BootAction;
        if (exitCode == 3)
        {
            lock (Gate) { if (boots) _boots = DiagResults.CleanBoots(null, null, DateTime.UtcNow); else _crashes = DiagResults.CleanCrashes(null, DateTime.UtcNow); }
            Activity.Report(ActivityKind.Analyze, boots ? "Windows has no start-up report to read" : "Windows has no crash reports to read");
            return (true, "nothing to report; nothing was changed");
        }
        if (exitCode != 0)
            return (false, $"the helper could not read the data (exit code {exitCode}); nothing was changed");
        if (boots)
        {
            var r = DiagResults.ParseBoots(text);
            if (r is null) { Activity.Report(ActivityKind.Analyze, "The start-up report from the helper could not be used"); return (false, "the result file was missing or not valid; nothing was changed"); }
            lock (Gate) _boots = r;
            Activity.Report(ActivityKind.Analyze, $"Read {Activity.Count(r.Boots.Count, "boot")}");
            return (true, $"read {r.Boots.Count} boots and {r.SlowItems.Count} slow start-up items; nothing was changed");
        }
        var c = DiagResults.ParseCrashes(text);
        if (c is null) { Activity.Report(ActivityKind.Analyze, "The crash reports from the helper could not be used"); return (false, "the result file was missing or not valid; nothing was changed"); }
        lock (Gate) _crashes = c;
        Activity.Report(ActivityKind.Analyze, $"Read {Activity.Count(c.Reports.Count, "crash report")}");
        return (true, $"read {c.Reports.Count} crash reports; nothing was changed");
    }

    /// <summary>The file text, then the file is deleted whatever happens. Null when the file is missing, a link, or too big.</summary>
    internal static string? ReadAndDelete(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > DiagResults.MaxChars * 4L) return null;
            return File.ReadAllText(path);
        }
        catch (Exception) { return null; }
        finally { DeleteOnly(path); }
    }

    private static string? DeleteOnly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* a file that cannot be removed is not used again: the next run replaces it */ }
        return null;
    }

    // ---- merging ----

    /// <summary>Fills BootTimes and SlowStartupItems only where the app could not read them itself (null = Unknown).</summary>
    public static void Merge(StorageState s, DiagBootResult r)
    {
        s.BootTimes ??= r.Boots.ToList();
        s.SlowStartupItems ??= r.SlowItems.ToList();
    }

    /// <summary>Fills the blue screen module names (only from BlueScreen reports) and marks that the reports were read.</summary>
    public static void Merge(HealthState s, DiagCrashResult r)
    {
        s.CrashReportsRead = true;
        if (s.BlueScreenModules.Count > 0) return;
        s.BlueScreenModules = BlueScreenReports.CountModules(
            r.Reports.Where(x => x.EventType.Equals("BlueScreen", StringComparison.OrdinalIgnoreCase)).Select(x => x.Module));
    }

    /// <summary>Called by the probes at the end of a read: adds what the helper read earlier in this session.</summary>
    public static void ApplyTo(StorageState s) { if (Boots is { } r) Merge(s, r); }
    public static void ApplyTo(HealthState s) { if (Crashes is { } r) Merge(s, r); }
}
