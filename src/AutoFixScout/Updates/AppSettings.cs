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

    // ---- Appearance and behaviour (Settings panel) ----

    /// <summary>"dark" (default), "light" or "system" (follows the Windows app mode).</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Frosted glass on Windows 11 in the dark theme. Default on.</summary>
    public bool Glass { get; set; } = true;

    /// <summary>"blue" (default), "teal", "violet" or "orange".</summary>
    public string Accent { get; set; } = "blue";

    /// <summary>Text and layout size: 1.0 (default), 1.1 or 1.2.</summary>
    public double TextScale { get; set; } = 1.0;

    /// <summary>Turns off movement in the app even when Windows animations are on.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Shows the list of steps unfolded during a check (for people who want to follow every step).</summary>
    public bool ActivityOpen { get; set; }

    public static readonly string[] Themes = { "dark", "light", "system" };
    public static readonly string[] Accents = { "blue", "teal", "violet", "orange" };
    public static readonly double[] TextScales = { 1.0, 1.1, 1.2 };

    /// <summary>Puts values from an old or hand-edited file back into range.</summary>
    public AppSettings Normalized()
    {
        if (!Themes.Contains(Theme)) Theme = "dark";
        if (!Accents.Contains(Accent)) Accent = "blue";
        if (!TextScales.Contains(TextScale)) TextScale = 1.0;
        return this;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FileIn(string dir) => Path.Combine(dir, "settings.json");

    public static AppSettings Load(string? dir = null)
    {
        try
        {
            var file = FileIn(dir ?? AppPaths.DataDir);
            return (File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new() : new()).Normalized();
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
