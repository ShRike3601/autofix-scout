namespace AutoFixScout.Probes;

/// <summary>A keyboard, mouse or HID device from Windows (PnP classes Keyboard, Mouse, HIDClass).</summary>
public sealed record InputDevice(string Name, string InstanceId, string PnpClass, int? ProblemCode)
{
    public bool Disabled => ProblemCode == 22;
    public bool Errored => ProblemCode is { } c && c != 0 && c != 22;
    public bool IsKeyboard => PnpClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase);
    public bool IsMouse => PnpClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase);
    public bool IsTouchpad => Name.Contains("touch pad", StringComparison.OrdinalIgnoreCase) || Name.Contains("touchpad", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// probe.input: input devices, the precision touchpad switch and the four accessibility key features.
/// A flags value of null means it could not be read; a missing registry value is read as 0 (off), as Windows does.
/// </summary>
public sealed class InputState
{
    public IReadOnlyList<InputDevice> Devices { get; set; } = new List<InputDevice>();
    /// <summary>HKCU PrecisionTouchPad\Status Enabled: Yes = on, No = off, Unknown = no such setting.</summary>
    public Tri TouchpadEnabled { get; set; } = Tri.Unknown;
    /// <summary>HKCU PrecisionTouchPad LeaveOnWithMouse.</summary>
    public Tri LeaveOnWithMouse { get; set; } = Tri.Unknown;
    public int? StickyKeysFlags { get; set; }
    public int? FilterKeysFlags { get; set; }
    public int? ToggleKeysFlags { get; set; }
    public int? MouseKeysFlags { get; set; }
    /// <summary>Installed layouts (Preload resolved through Substitutes); empty when none could be read.</summary>
    public IReadOnlyList<KeyboardLayoutInfo> Layouts { get; set; } = new List<KeyboardLayoutInfo>();
    /// <summary>The first Preload entry or InputMethodOverride: the layout used by default.</summary>
    public KeyboardLayoutInfo? DefaultLayout { get; set; }
    /// <summary>First preferred Windows display language tag, for example "en-GB".</summary>
    public string? DisplayLanguage { get; set; }
    public SwitchHotkey LanguageHotkey { get; set; } = SwitchHotkey.Unknown;
    public SwitchHotkey LayoutHotkey { get; set; } = SwitchHotkey.Unknown;
    /// <summary>The person reported wrong characters, so the layout findings come first.</summary>
    public bool TypingFocus { get; set; }
    public bool SwitchHotkeyOn => KeyboardLayouts.IsKeyChord(LanguageHotkey) || KeyboardLayouts.IsKeyChord(LayoutHotkey);
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();

    /// <summary>Bit 1 of every accessibility Flags value (SKF_STICKYKEYSON, FKF_FILTERKEYSON, TKF_TOGGLEKEYSON, MKF_MOUSEKEYSON).</summary>
    public const int OnBit = 1;
    public static bool IsOn(int? flags) => flags is { } f && (f & OnBit) != 0;
    public static int ClearOn(int flags) => flags & ~OnBit;
    public bool HasTouchpad => Devices.Any(d => d.IsTouchpad);
    public bool HasMouse => Devices.Any(d => d.IsMouse && !d.IsTouchpad);
}

public interface IInputProbe { InputState Read(); }

/// <summary>An installed keyboard layout. Klid is the 8 hex digit identifier (for example "00000409").</summary>
public sealed record KeyboardLayoutInfo(string Klid, string Name)
{
    /// <summary>The low 16 bits of the KLID: the Windows language identifier.</summary>
    public int LangId => KeyboardLayouts.LangId(Klid);
}

/// <summary>Switch hotkey codes of HKCU\Keyboard Layout\Toggle.</summary>
public enum SwitchHotkey { AltShift, CtrlShift, None, Grave, Unknown }

/// <summary>Pure helpers: KLID parsing, friendly names and hotkey decoding.</summary>
public static class KeyboardLayouts
{
    public static bool TryParseKlid(string? klid, out int value)
    {
        value = 0;
        if (klid is null) return false;
        var t = klid.Trim();
        return t.Length == 8 && int.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public static int LangId(string klid) => TryParseKlid(klid, out var v) ? v & 0xFFFF : 0;

    private static readonly Dictionary<int, string> Known = new()
    {
        [0x0409] = "US", [0x0809] = "United Kingdom", [0x4009] = "India", [0x0c09] = "Australian", [0x1009] = "Canadian English",
        [0x040c] = "French", [0x0407] = "German", [0x040a] = "Spanish", [0x0410] = "Italian", [0x0419] = "Russian",
    };

    /// <summary>Friendly name such as "US" or "United Kingdom"; falls back to the culture name, then to the raw KLID.</summary>
    public static string FriendlyName(string klid)
    {
        if (!TryParseKlid(klid, out _)) return klid;
        var id = LangId(klid);
        if (Known.TryGetValue(id, out var k)) return k;
        try { return System.Globalization.CultureInfo.GetCultureInfo(id).DisplayName; }
        catch (Exception) { return klid; }
    }

    public static SwitchHotkey DecodeHotkey(string? value) => value?.Trim() switch
    {
        "1" => SwitchHotkey.AltShift,
        "2" => SwitchHotkey.CtrlShift,
        "3" => SwitchHotkey.None,
        "4" => SwitchHotkey.Grave,
        _ => SwitchHotkey.Unknown,
    };

    public static bool IsKeyChord(SwitchHotkey h) => h is SwitchHotkey.AltShift or SwitchHotkey.CtrlShift;

    /// <summary>Two letter language of a language tag ("en-GB" gives "en") or of a language identifier.</summary>
    public static string LanguageOfTag(string tag) => tag.Split('-', '_')[0].ToLowerInvariant();

    public static string? LanguageOfLangId(int id)
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo(id).TwoLetterISOLanguageName.ToLowerInvariant(); }
        catch (Exception) { return null; }
    }
}
