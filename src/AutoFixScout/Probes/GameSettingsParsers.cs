using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoFixScout.Probes;

/// <summary>Pure parsers for the Windows game settings (registry values as the registry hands them over). Unit tested; anything odd becomes Unknown.</summary>
public static class GameSettingsParsers
{
    /// <summary>A registry number as a whole number: int, uint, long, a 4 or 8 byte binary, or a numeric text. Anything else is null.</summary>
    public static long? Number(object? v)
    {
        switch (v)
        {
            case int i: return i;
            case uint u: return u;
            case long l: return l;
            case ulong ul when ul <= long.MaxValue: return (long)ul;
            case byte[] b when b.Length == 4: return BitConverter.ToInt32(b, 0);
            case byte[] b when b.Length == 8: return BitConverter.ToInt64(b, 0);
            case string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n): return n;
            default: return null;
        }
    }

    /// <summary>1 = Yes, 0 = No, missing or any other value = Unknown.</summary>
    public static Tri OneZero(object? v) => Number(v) switch { 1 => Tri.Yes, 0 => Tri.No, _ => Tri.Unknown };

    /// <summary>Game Mode. A missing value means the Windows 11 default, which is on; on older Windows a missing value stays Unknown.</summary>
    public static Tri GameMode(object? autoGameModeEnabled, bool windows11) =>
        autoGameModeEnabled == null ? (windows11 ? Tri.Yes : Tri.Unknown) : OneZero(autoGameModeEnabled);

    /// <summary>HwSchMode: 2 = on, 1 = off, anything else (missing, 0, 3, text) = Unknown.</summary>
    public static Tri HardwareScheduling(object? hwSchMode) => Number(hwSchMode) switch { 2 => Tri.Yes, 1 => Tri.No, _ => Tri.Unknown };

    /// <summary>Background recording is on only when Game DVR, app capture and background (historical) capture are all on; any unreadable part makes it Unknown unless another part is off.</summary>
    public static Tri BackgroundRecording(Tri gameDvr, Tri appCapture, Tri historical)
    {
        var all = new[] { gameDvr, appCapture, historical };
        if (all.Contains(Tri.No)) return Tri.No;
        return all.All(t => t == Tri.Yes) ? Tri.Yes : Tri.Unknown;
    }

    /// <summary>Splits "Key=Value;Key2=Value2;" into pairs. Keys are case-insensitive; empty or malformed pieces are skipped.</summary>
    public static IReadOnlyDictionary<string, string> Pairs(string? text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = piece.IndexOf('=');
            if (eq <= 0) continue;
            var key = piece[..eq].Trim();
            if (key.Length > 0) d[key] = piece[(eq + 1)..].Trim();
        }
        return d;
    }

    private static Tri Flag(IReadOnlyDictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) ? OneZero(v) : Tri.Unknown;

    /// <summary>"Optimizations for windowed games" (SwapEffectUpgradeEnable) and "Variable refresh rate" (VRROptimizeEnable) from DirectXUserGlobalSettings. Missing = Unknown.</summary>
    public static (Tri WindowedOptimisations, Tri VariableRefresh) GlobalSettings(object? directXUserGlobalSettings)
    {
        if (directXUserGlobalSettings is not string s) return (Tri.Unknown, Tri.Unknown);
        var p = Pairs(s);
        return (Flag(p, "SwapEffectUpgradeEnable"), Flag(p, "VRROptimizeEnable"));
    }

    /// <summary>GpuPreference of one per-app entry: 0 = let Windows decide, 1 = power saving, 2 = high performance; null when missing or odd.</summary>
    public static int? GpuPreference(object? value)
    {
        if (value is not string s || !Pairs(s).TryGetValue("GpuPreference", out var v)) return null;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 2 ? n : null;
    }

    private static readonly string[] GameFolders =
    {
        @"\steamapps\", @"\epic games\", @"\xboxgames\", @"\riot games\", @"\gog galaxy\games\", @"\ea games\", @"\ubisoft\",
    };

    private static readonly string[] LauncherNames =
    {
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "ubisoftconnect.exe", "upc.exe", "galaxyclient.exe",
        "riotclientservices.exe", "eadesktop.exe", "origin.exe",
    };

    private static readonly Regex SafeName = new(@"^[\w .\-()+\[\]']{1,80}\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The file name (never the folder) of a program that is clearly a game: an .exe inside a known game folder of a launcher
    /// (Steam, Epic, Xbox, Riot, GOG, EA, Ubisoft), and not the launcher itself. Null for everything else.
    /// </summary>
    public static string? GameFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('/', '\\');
        var lower = p.ToLowerInvariant();
        var slash = p.LastIndexOf('\\');
        if (slash < 0) return null;
        var name = p[(slash + 1)..];
        if (!SafeName.IsMatch(name) || LauncherNames.Contains(name.ToLowerInvariant())) return null;
        if (lower.Contains(@"\launcher\")) return null;
        return GameFolders.Any(f => lower.Contains(f)) ? name : null;
    }

    /// <summary>
    /// Games that are set to the power saving graphics chip (GpuPreference=1), from UserGpuPreferences (value name = program path,
    /// value = "GpuPreference=1;"). Only file names are returned, sorted, without duplicates, at most 10.
    /// </summary>
    public static IReadOnlyList<string> PowerSavingGames(IEnumerable<KeyValuePair<string, object?>> entries) =>
        entries.Where(e => GpuPreference(e.Value) == 1)
            .Select(e => GameFileName(e.Key))
            .Where(n => n != null).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(10).ToList();
}
