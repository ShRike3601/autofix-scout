using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using Microsoft.Win32;

namespace AutoFixScout.Repair;

/// <summary>
/// One-click game settings fixes for the signed-in person (HKCU only, no administrator rights). Every method takes the key
/// root, so tests run against a scratch key and never touch the real settings. Each fix is idempotent ("nothing to do" when the
/// value is already as wanted), saves the previous values in an undo token inside the result detail, and <see cref="Undo(RegistryKey, string)"/>
/// puts back exactly those values (a value that was missing before is deleted again).
/// </summary>
public static class GameFixes
{
    public const string GameModeAction = "games.game_mode_on", GraphicsCardAction = "games.use_graphics_card", RecordingAction = "games.background_recording_off";
    public static bool IsGameFix(string actionId) => actionId is GameModeAction or GraphicsCardAction or RecordingAction;

    public const string GameBarKey = @"Software\Microsoft\GameBar";
    public const string GameModeValue = "AutoGameModeEnabled";
    public const string StoreKey = @"System\GameConfigStore";
    public const string StoreValue = "GameDVR_Enabled";
    public const string DvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string CaptureValue = "AppCaptureEnabled", HistoricalValue = "HistoricalCaptureEnabled";
    public const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private static readonly Regex ExeName = new(@"^[A-Za-z0-9 ._()-]{1,80}\.exe$", RegexOptions.Compiled);
    public static bool IsValidExeName(string? name) => name is not null && ExeName.IsMatch(name);

    // ---- previous value encoding: "missing", "d:<number>" (DWORD) or "s:<text>" (string) ----

    private static string? Encode(RegistryKey? key, string name)
    {
        var v = key?.GetValue(name);
        return v switch { null => "missing", int i => "d:" + i, string s => "s:" + s, _ => null };
    }

    private static void Restore(RegistryKey root, string path, string name, string encoded)
    {
        if (encoded == "missing")
        {
            using var k = root.OpenSubKey(path, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        using var key = root.CreateSubKey(path, writable: true);
        if (encoded.StartsWith("d:", StringComparison.Ordinal)) key.SetValue(name, int.Parse(encoded.AsSpan(2)), RegistryValueKind.DWord);
        else key.SetValue(name, encoded[2..], RegistryValueKind.String);
    }

    private static bool ValidEncoded(string e) =>
        e == "missing" || (e.StartsWith("d:", StringComparison.Ordinal) && int.TryParse(e.AsSpan(2), out _)) || e.StartsWith("s:", StringComparison.Ordinal);

    private static string Token(string kind, params string[] fields) =>
        "undo=" + kind + string.Concat(fields.Select(f => "|" + Uri.EscapeDataString(f)));

    private static readonly Regex TokenRx = new(@"undo=(gamemode|capture|gpu)((?:\|[A-Za-z0-9%._~!*'()-]*)+)", RegexOptions.Compiled);

    /// <summary>The undo payload ("kind|field|field", fields escaped) found in a fix result, or null when the fix changed nothing.</summary>
    public static string? PayloadFromDetail(string? detail)
    {
        if (detail is null || TokenRx.Match(detail) is not { Success: true } m) return null;
        var payload = m.Groups[1].Value + m.Groups[2].Value;
        return IsValidPayload(payload) ? payload : null;
    }

    /// <summary>Shape check of a payload: known kind, the right number of fields, every previous value well formed.</summary>
    public static bool IsValidPayload(string? payload)
    {
        if (payload is null) return false;
        var parts = payload.Split('|');
        try
        {
            var f = parts.Skip(1).Select(Uri.UnescapeDataString).ToArray();
            return parts[0] switch
            {
                "gamemode" => f.Length == 1 && ValidEncoded(f[0]),
                "capture" => f.Length == 3 && f.All(ValidEncoded),
                "gpu" => f.Length == 2 && f[0].Length is > 0 and <= 1000 && f[1].Length is > 0 and <= 1000 && !f[0].Contains('\0'),
                _ => false,
            };
        }
        catch (UriFormatException) { return false; }
    }

    // ---- 1. Game Mode ----

    public static (bool Ok, string Detail) GameModeOn(RegistryKey root)
    {
        using var key = root.OpenSubKey(GameBarKey);
        var before = Encode(key, GameModeValue);
        if (before is null) return (false, "games.game_mode_on: the Game Mode value has an unexpected type, nothing changed");
        if (key?.GetValue(GameModeValue) is int and 1) return (true, "Game Mode is already on, nothing to do");
        using var w = root.CreateSubKey(GameBarKey, writable: true);
        w.SetValue(GameModeValue, 1, RegistryValueKind.DWord);
        return (true, $"Game Mode turned on; {Token("gamemode", before)}");
    }

    // ---- 3. Background recording ----

    public static (bool Ok, string Detail) BackgroundRecordingOff(RegistryKey root)
    {
        using var store = root.OpenSubKey(StoreKey);
        using var dvr = root.OpenSubKey(DvrKey);
        var items = new (string Path, string Name, RegistryKey? Key)[] { (StoreKey, StoreValue, store), (DvrKey, CaptureValue, dvr), (DvrKey, HistoricalValue, dvr) };
        var before = items.Select(i => Encode(i.Key, i.Name)).ToArray();
        if (before.Any(b => b is null)) return (false, "games.background_recording_off: a recording value has an unexpected type, nothing changed");
        var todo = items.Where(i => i.Key?.GetValue(i.Name) is not (int and 0)).ToList();
        if (todo.Count == 0) return (true, "Background recording is already off, nothing to do");
        foreach (var (path, name, _) in todo)
        {
            using var w = root.CreateSubKey(path, writable: true);
            w.SetValue(name, 0, RegistryValueKind.DWord);
        }
        return (true, $"background recording turned off; {Token("capture", before[0]!, before[1]!, before[2]!)}");
    }

    // ---- 2. Graphics card for one game ----

    /// <summary>The string with GpuPreference set to the given number; every other part stays as it was. Null when it has no GpuPreference part.</summary>
    public static string? WithGpuPreference(string value, int preference)
    {
        var pieces = value.Split(';');
        var done = false;
        for (var i = 0; i < pieces.Length; i++)
        {
            var eq = pieces[i].IndexOf('=');
            if (done || eq <= 0 || !pieces[i][..eq].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)) continue;
            pieces[i] = pieces[i][..(eq + 1)] + preference;
            done = true;
        }
        return done ? string.Join(';', pieces) : null;
    }

    public static (bool Ok, string Detail) UseGraphicsCard(RegistryKey root, string? exeName)
    {
        if (!IsValidExeName(exeName)) return (false, "games.use_graphics_card: invalid program name, nothing changed");
        using var key = root.OpenSubKey(GpuKey);
        var matches = (key?.GetValueNames() ?? Array.Empty<string>())
            .Where(n => GameSettingsParsers.GameFileName(n) is { } g && g.Equals(exeName, StringComparison.OrdinalIgnoreCase)
                        && n.Trim().Replace('/', '\\').EndsWith("\\" + exeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return (false, "games.use_graphics_card: no such game in the graphics preferences, nothing changed");
        if (matches.Count > 1) return (false, "games.use_graphics_card: more than one entry has this file name, nothing changed");
        var name = matches[0];
        if (key!.GetValueKind(name) != RegistryValueKind.String || key.GetValue(name) is not string old)
            return (false, "games.use_graphics_card: the entry has an unexpected type, nothing changed");
        var pref = GameSettingsParsers.GpuPreference(old);
        if (pref == 2) return (true, "the game already uses the graphics card, nothing to do");
        if (pref != 1) return (false, "games.use_graphics_card: the game is not set to the power saving chip, nothing changed");
        var updated = WithGpuPreference(old, 2)!;
        using var w = root.OpenSubKey(GpuKey, writable: true)!;
        w.SetValue(name, updated, RegistryValueKind.String);
        return (true, $"the game was set to use the graphics card; {Token("gpu", name, old)}");
    }

    // ---- undo ----

    /// <summary>Puts back the values saved in a payload. A value that was missing is deleted; an unreadable payload changes nothing.</summary>
    public static (bool Ok, string Detail) Undo(RegistryKey root, string payload)
    {
        if (!IsValidPayload(payload)) return (false, "games undo: invalid record, nothing changed");
        var parts = payload.Split('|');
        var f = parts.Skip(1).Select(Uri.UnescapeDataString).ToArray();
        switch (parts[0])
        {
            case "gamemode":
                Restore(root, GameBarKey, GameModeValue, f[0]);
                return (true, "Game Mode restored to its previous value");
            case "capture":
                Restore(root, StoreKey, StoreValue, f[0]);
                Restore(root, DvrKey, CaptureValue, f[1]);
                Restore(root, DvrKey, HistoricalValue, f[2]);
                return (true, "background recording restored to its previous values");
            default:
                // Only an entry that is still there, inside a known game folder, is written back.
                if (GameSettingsParsers.GameFileName(f[0]) is null) return (false, "games undo: not a game entry, nothing changed");
                using (var key = root.OpenSubKey(GpuKey, writable: true))
                {
                    var existing = key?.GetValueNames().FirstOrDefault(n => n.Equals(f[0], StringComparison.OrdinalIgnoreCase));
                    if (key is null || existing is null) return (false, "games undo: the entry no longer exists, nothing changed");
                    key.SetValue(existing, f[1], RegistryValueKind.String);
                }
                return (true, "the graphics card choice of the game was restored");
        }
    }

    // ---- real HKCU entry points (FixActions) ----
    public static (bool Ok, string Detail) GameModeOn() => GameModeOn(Registry.CurrentUser);
    public static (bool Ok, string Detail) BackgroundRecordingOff() => BackgroundRecordingOff(Registry.CurrentUser);
    public static (bool Ok, string Detail) UseGraphicsCard(string? exeName) => UseGraphicsCard(Registry.CurrentUser, exeName);
    public static (bool Ok, string Detail) Undo(string payload) => Undo(Registry.CurrentUser, payload);
}
