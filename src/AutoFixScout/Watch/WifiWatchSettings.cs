using System.Text.Json;

namespace AutoFixScout.Watching;

/// <summary>
/// %LOCALAPPDATA%\AutoFixScout\wifi-watch-settings.json: per-user switches of the Wi-Fi watcher. Today only "Notify"
/// (show a toast after a reconnect; on by default). A missing or unreadable file means the default.
/// </summary>
public sealed class WifiWatchSettings
{
    private sealed class FileShape { public bool Notify { get; set; } = true; }
    public string Path { get; }

    public WifiWatchSettings(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "wifi-watch-settings.json");

    public bool Notify
    {
        get
        {
            try { return !File.Exists(Path) || (JsonSerializer.Deserialize<FileShape>(File.ReadAllText(Path))?.Notify ?? true); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return true; }
        }
    }

    public void SetNotify(bool on)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape { Notify = on }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>Allows at most one toast per <see cref="Interval"/>.</summary>
public sealed class ToastGate
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private DateTime? _last;

    public bool TryAcquire(DateTime nowUtc)
    {
        if (_last is { } l && nowUtc - l < Interval) return false;
        _last = nowUtc;
        return true;
    }
}
