using System.Text.Json;

namespace AutoFixScout.Watching;

/// <summary>What the main app reads from the watcher's file.</summary>
public sealed record WifiWatchSummary(int Drops, int ReconnectedByWatcher, int Days);

/// <summary>
/// %LOCALAPPDATA%\AutoFixScout\wifi-watch.json: the last 30 days of outages as counts (time, reason category, whether the
/// watcher reconnected, seconds offline). It has no field that could hold a network name, and nothing leaves the computer.
/// </summary>
public sealed class WifiWatchStore
{
    public const int KeepDays = 30;
    private sealed class FileShape { public List<WifiWatchRecord> Records { get; set; } = new(); }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Path { get; }

    public WifiWatchStore(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "wifi-watch.json");

    /// <summary>Records of the last 30 days; empty when the file is missing or unreadable.</summary>
    public IReadOnlyList<WifiWatchRecord> Load(DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(Path)) return Array.Empty<WifiWatchRecord>();
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(Path), Options);
            var cutoff = nowUtc.AddDays(-KeepDays);
            return shape?.Records.Where(r => r.TimeUtc >= cutoff).ToList() ?? new List<WifiWatchRecord>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return Array.Empty<WifiWatchRecord>(); }
    }

    public void Add(WifiWatchRecord record, DateTime nowUtc)
    {
        var all = Load(nowUtc).ToList();
        all.Add(record);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape { Records = all }, Options));
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>Null when there is no file.</summary>
    public WifiWatchSummary? Summarize(DateTime nowUtc, int days = KeepDays)
    {
        if (!File.Exists(Path)) return null;
        var cutoff = nowUtc.AddDays(-days);
        var recent = Load(nowUtc).Where(r => r.TimeUtc >= cutoff).ToList();
        return new WifiWatchSummary(recent.Count, recent.Count(r => r.ReconnectedByWatcher), days);
    }
}
