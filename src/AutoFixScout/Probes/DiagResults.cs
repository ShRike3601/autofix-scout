using System.Text.Json;

#if AFS_EXECUTOR
// Linked into the elevated helper: a separate namespace, so the app and the helper never clash (the tests reference both).
namespace AutoFixScout.Executor.Shared;
#else
namespace AutoFixScout.Probes;
#endif

/// <summary>One blue screen or live kernel report from Windows Error Reporting (Report.wer): only the fields BlueScreenReports parses.</summary>
public sealed record CrashReport(string EventType, uint? Code, string? Module, DateTime? WhenUtc);

/// <summary>What diag.read_boot_times writes: boots (event 100) and the items Windows named as slowing start-up (events 101 to 110).</summary>
public sealed class DiagBootResult
{
    public int Version { get; set; }
    public string Action { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<BootRecord> Boots { get; set; } = new();
    public List<SlowStartupItem> SlowItems { get; set; } = new();
}

/// <summary>What diag.read_crash_reports writes.</summary>
public sealed class DiagCrashResult
{
    public int Version { get; set; }
    public string Action { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<CrashReport> Reports { get; set; } = new();
}

/// <summary>
/// One physical drive from diag.read_drive_health. No serial number and no path: DeviceId is Windows' small drive number,
/// Name is the friendly model name. Every reading is null when the drive does not report it (or the value was out of range).
/// </summary>
public sealed record DriveHealthRecord(int? DeviceId, string Name, string MediaType, string BusType, string Health, long? SizeBytes,
    int? WearPercent, int? TemperatureC, int? TemperatureMaxC, long? ReadErrorsTotal, long? ReadErrorsUncorrected,
    long? WriteErrorsTotal, long? WriteErrorsUncorrected, long? PowerOnHours, long? StartStopCycles);

/// <summary>What diag.read_drive_health writes.</summary>
public sealed class DiagDriveResult
{
    public int Version { get; set; }
    public string Action { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<DriveHealthRecord> Drives { get; set; } = new();
}

/// <summary>
/// The result files of the two read-only elevated actions (diag.read_boot_times, diag.read_crash_reports, diag.read_drive_health): names,
/// JSON shape, size limits, validation and the privacy filter. Pure; the file is untrusted input for the app, and the
/// elevated helper uses the same filter before it writes. Shared (linked) with AutoFixScout.Executor.
/// </summary>
public static class DiagResults
{
    public const int Version = 1;
    public const string BootAction = "diag.read_boot_times", CrashAction = "diag.read_crash_reports", DriveAction = "diag.read_drive_health";
    public const string BootFile = "diag-boot-times.json", CrashFile = "diag-crash-reports.json", DriveFile = "diag-drive-health.json";
    public const int MaxChars = 256 * 1024, MaxBoots = 10, MaxSlowItems = 100, MaxReports = 200, MaxNameLength = 128;
    public const long MaxDurationMs = 24L * 3600 * 1000;
    public const int Days = 30;
    public const int MaxDrives = 32, MaxTemperatureC = 120, MaxCount = 1_000_000_000, MaxHours = 1_000_000;

    public static bool IsDiagAction(string? actionId) => actionId is BootAction or CrashAction or DriveAction;

    /// <summary>C:\ProgramData\AutoFixScout\logs, where the helper writes and the app reads and deletes.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoFixScout", "logs");

    public static string FileFor(string actionId) =>
        Path.Combine(Folder, actionId == BootAction ? BootFile : actionId == CrashAction ? CrashFile : actionId == DriveAction ? DriveFile : throw new ArgumentException("not a diag action", nameof(actionId)));

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// True for text that names a user folder (anything containing \Users\ or /Users/) or has control characters. Such
    /// text is dropped, never shortened, so a user name cannot reach the app or a report.
    /// </summary>
    public static bool IsPrivate(string? text) =>
        text is null || text.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) || text.Contains("/Users/", StringComparison.OrdinalIgnoreCase)
        || text.Any(char.IsControl);

    /// <summary>The file or friendly name only: null when private, empty or too long; a path is cut to its file name.</summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || IsPrivate(name)) return null;
        var n = name.Trim().Trim('"');
        var cut = Math.Max(n.LastIndexOf((char)92), n.LastIndexOf((char)47));
        if (cut >= 0) n = n[(cut + 1)..];
        n = n.Trim();
        return n.Length is > 0 and <= MaxNameLength ? n : null;
    }

    private static bool Recent(DateTime utc, DateTime now) => utc >= now.AddDays(-Days - 1) && utc <= now.AddDays(1);

    public static DiagBootResult CleanBoots(IEnumerable<BootRecord>? boots, IEnumerable<SlowStartupItem>? slow, DateTime nowUtc)
    {
        var r = new DiagBootResult { Version = Version, Action = BootAction, CreatedUtc = nowUtc };
        r.Boots = (boots ?? Enumerable.Empty<BootRecord>())
            .Where(b => b is not null && Recent(b.TimeUtc, nowUtc) && b.BootMs is >= 0 and <= MaxDurationMs && b.MainPathMs is >= 0 and <= MaxDurationMs && b.PostBootMs is >= 0 and <= MaxDurationMs)
            .OrderByDescending(b => b.TimeUtc).Take(MaxBoots).ToList();
        r.SlowItems = (slow ?? Enumerable.Empty<SlowStartupItem>())
            .Where(s => s is not null && s.TotalMs is >= 0 and <= MaxDurationMs)
            .Select(s => CleanName(s.Name) is { } n ? new SlowStartupItem(n, s.TotalMs, CleanName(s.FileName), s.EventId is >= 0 and <= 110 ? s.EventId : 0) : null).OfType<SlowStartupItem>()
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(x => x.TotalMs)!)
            .OrderByDescending(s => s.TotalMs).Take(MaxSlowItems).ToList();
        return r;
    }

    public static DiagCrashResult CleanCrashes(IEnumerable<CrashReport>? reports, DateTime nowUtc)
    {
        var r = new DiagCrashResult { Version = Version, Action = CrashAction, CreatedUtc = nowUtc };
        r.Reports = (reports ?? Enumerable.Empty<CrashReport>())
            .Where(c => c is not null && c.EventType is not null && (c.EventType.Equals("BlueScreen", StringComparison.OrdinalIgnoreCase) || c.EventType.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase))
                        && (c.WhenUtc is null || Recent(c.WhenUtc.Value, nowUtc)))
            .Select(c => new CrashReport(c.EventType.Equals("BlueScreen", StringComparison.OrdinalIgnoreCase) ? "BlueScreen" : "LiveKernelEvent",
                c.Code is <= 0x0FFFFFFF ? c.Code : null, CleanName(c.Module), c.WhenUtc))
            .Take(MaxReports).ToList();
        return r;
    }

    private static int? Pct(int? v) => v is >= 0 and <= 100 ? v : null;
    private static int? Temp(int? v) => v is >= 0 and <= MaxTemperatureC ? v : null;
    private static long? Count(long? v) => v is >= 0 and <= MaxCount ? v : null;
    private static long? Hours(long? v) => v is >= 0 and <= MaxHours ? v : null;

    /// <summary>
    /// Drops drives without a usable name. A reading out of range becomes null (Unknown): wear 0 to 100, temperatures 0 to 120 C.
    /// Names are cut to the friendly model name; a name that holds a user folder is dropped with its drive.
    /// </summary>
    public static DiagDriveResult CleanDrives(IEnumerable<DriveHealthRecord>? drives, DateTime nowUtc)
    {
        var r = new DiagDriveResult { Version = Version, Action = DriveAction, CreatedUtc = nowUtc };
        r.Drives = (drives ?? Enumerable.Empty<DriveHealthRecord>())
            .Where(d => d is not null && CleanName(d.Name) is not null)
            .Select(d => new DriveHealthRecord(
                d.DeviceId is >= 0 and <= 4096 ? d.DeviceId : null, CleanName(d.Name)!, CleanWord(d.MediaType), CleanWord(d.BusType), CleanWord(d.Health),
                d.SizeBytes is > 0 and <= 1L << 60 ? d.SizeBytes : null,
                Pct(d.WearPercent), Temp(d.TemperatureC), Temp(d.TemperatureMaxC), Count(d.ReadErrorsTotal), Count(d.ReadErrorsUncorrected),
                Count(d.WriteErrorsTotal), Count(d.WriteErrorsUncorrected), Hours(d.PowerOnHours), Count(d.StartStopCycles)))
            .Take(MaxDrives).ToList();
        return r;
    }

    private static string CleanWord(string? w) => w is not null && w.Length <= 24 && w.All(c => char.IsLetterOrDigit(c) || c == ' ') ? w : "Unknown";

    public static string Serialize(DiagDriveResult r) => JsonSerializer.Serialize(r, Options);

    /// <summary>Validates the text of a drive health result file; same rules as <see cref="ParseBoots"/>.</summary>
    public static DiagDriveResult? ParseDrives(string? json, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxChars) return null;
        try
        {
            var r = JsonSerializer.Deserialize<DiagDriveResult>(json, Options);
            if (r is null || r.Version != Version || r.Action != DriveAction) return null;
            return CleanDrives(r.Drives, nowUtc ?? DateTime.UtcNow);
        }
        catch (Exception) { return null; }
    }

    public static string Serialize(DiagBootResult r) => JsonSerializer.Serialize(r, Options);
    public static string Serialize(DiagCrashResult r) => JsonSerializer.Serialize(r, Options);

    /// <summary>Validates the text of a boot result file. Null when it is missing, too big, malformed or for another action. Entries that fail the checks are dropped.</summary>
    public static DiagBootResult? ParseBoots(string? json, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxChars) return null;
        try
        {
            var r = JsonSerializer.Deserialize<DiagBootResult>(json, Options);
            if (r is null || r.Version != Version || r.Action != BootAction) return null;
            return CleanBoots(r.Boots, r.SlowItems, nowUtc ?? DateTime.UtcNow);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Validates the text of a crash report result file; same rules as <see cref="ParseBoots"/>.</summary>
    public static DiagCrashResult? ParseCrashes(string? json, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxChars) return null;
        try
        {
            var r = JsonSerializer.Deserialize<DiagCrashResult>(json, Options);
            if (r is null || r.Version != Version || r.Action != CrashAction) return null;
            return CleanCrashes(r.Reports, nowUtc ?? DateTime.UtcNow);
        }
        catch (Exception) { return null; }
    }
}
