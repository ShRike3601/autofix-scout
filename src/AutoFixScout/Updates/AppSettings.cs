using System.Text.Json;

namespace AutoFixScout.Updates;

/// <summary>Where AutoFix Scout keeps its small per-user files.</summary>
public static class AppPaths
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout");
}

/// <summary>
/// Per-user settings in %LOCALAPPDATA%\AutoFixScout\settings.json. A missing or damaged file means the defaults.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Default on. When off, AutoFix Scout never contacts api.github.com.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Default off. Only for people who test builds: lets a development build be offered a release.</summary>
    public bool OfferUpdatesToDevBuilds { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FileIn(string dir) => Path.Combine(dir, "settings.json");

    public static AppSettings Load(string? dir = null)
    {
        try
        {
            var file = FileIn(dir ?? AppPaths.DataDir);
            return File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>False when the file could not be written (the setting then only lasts until the app closes).</summary>
    public bool Save(string? dir = null)
    {
        try
        {
            dir ??= AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FileIn(dir), JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch (Exception) { return false; }
    }
}
