using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AutoFixScout.Probes;

/// <summary>Read-only, no admin, never throws: failures go into ProbeErrors.</summary>
public sealed class RealInputProbe : IInputProbe
{
    public const string TouchpadKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";

    public InputState Read()
    {
        var errors = new List<string>();
        var s = new InputState { ProbeErrors = errors };
        try { s.Devices = ReadDevices(); }
        catch (Exception ex) { errors.Add($"WMI: {ex.Message}"); }
        try
        {
            Activity.ReadingRegistry("HKCU", TouchpadKey, "(touchpad settings)");
            s.TouchpadEnabled = ReadTri(TouchpadKey + @"\Status", "Enabled");
            s.LeaveOnWithMouse = ReadTri(TouchpadKey, "LeaveOnWithMouse");
        }
        catch (Exception ex) { errors.Add($"Touchpad: {ex.Message}"); }
        try
        {
            Activity.Report(ActivityKind.Command, "Reading the accessibility key settings through SystemParametersInfo (Sticky, Filter, Toggle and Mouse Keys)");
            s.StickyKeysFlags = Accessibility.Get(Accessibility.Kind.Sticky);
            s.FilterKeysFlags = Accessibility.Get(Accessibility.Kind.Filter);
            s.ToggleKeysFlags = Accessibility.Get(Accessibility.Kind.Toggle);
            s.MouseKeysFlags = Accessibility.Get(Accessibility.Kind.Mouse);
        }
        catch (Exception ex) { errors.Add($"Accessibility: {ex.Message}"); }
        Activity.ReadingRegistry("HKCU", "Keyboard Layout", "(installed layouts and the switch shortcut)");
        try { ReadLayouts(s); }

        catch (Exception ex) { errors.Add($"Layouts: {ex.Message}"); }
        return s;
    }

    /// <summary>Read-only: HKCU Keyboard Layout Preload, Substitutes and Toggle, and the display language.</summary>
    private static void ReadLayouts(InputState s)
    {
        var layouts = new List<KeyboardLayoutInfo>();
        using (var pre = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload"))
        using (var sub = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Substitutes"))
        {
            if (pre is not null)
                foreach (var n in pre.GetValueNames().OrderBy(x => int.TryParse(x, out var i) ? i : int.MaxValue))
                {
                    var klid = pre.GetValue(n) as string;
                    if (klid is null) continue;
                    var real = sub?.GetValue(klid) as string ?? klid;
                    if (!layouts.Any(l => l.Klid.Equals(real, StringComparison.OrdinalIgnoreCase)))
                        layouts.Add(new KeyboardLayoutInfo(real, KeyboardLayouts.FriendlyName(real)));
                }
        }
        if (layouts.Count == 0)
        {
            var n0 = GetKeyboardLayoutList(0, null);
            var arr = new IntPtr[Math.Max(n0, 0)];
            var got = n0 > 0 ? GetKeyboardLayoutList(n0, arr) : 0;
            foreach (var h in arr.Take(got))
            {
                var klid = ((long)h & 0xFFFF).ToString("x8");
                if (!layouts.Any(l => l.Klid == klid)) layouts.Add(new KeyboardLayoutInfo(klid, KeyboardLayouts.FriendlyName(klid)));
            }
        }
        s.Layouts = layouts;
        using (var prof = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile"))
        {
            var klid = (prof?.GetValue("InputMethodOverride") as string)?.Split(':').LastOrDefault(); // for example "0409:00000409"
            s.DefaultLayout = KeyboardLayouts.TryParseKlid(klid, out _)
                ? new KeyboardLayoutInfo(klid!, KeyboardLayouts.FriendlyName(klid!))
                : layouts.FirstOrDefault();
        }
        using (var d = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            s.DisplayLanguage = (d?.GetValue("PreferredUILanguages") as string[])?.FirstOrDefault()
                ?? System.Globalization.CultureInfo.CurrentUICulture.Name;
        using (var t = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Toggle"))
        {
            var legacy = t?.GetValue("Hotkey") as string ?? "1"; // Windows default: Alt+Shift
            s.LanguageHotkey = KeyboardLayouts.DecodeHotkey(t?.GetValue("Language Hotkey") as string ?? legacy);
            s.LayoutHotkey = KeyboardLayouts.DecodeHotkey(t?.GetValue("Layout Hotkey") as string ?? legacy);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int size, IntPtr[]? list);

    private static Tri ReadTri(string path, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path);
        return k?.GetValue(name) is int v ? (v != 0 ? Tri.Yes : Tri.No) : Tri.Unknown;
    }

    private static List<InputDevice> ReadDevices()
    {
        var list = new List<InputDevice>();
        using var q = Wmi.Searcher(
            "SELECT Name, DeviceID, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'Keyboard' OR PNPClass = 'Mouse' OR PNPClass = 'HIDClass'");
        foreach (ManagementObject d in q.Get())
            using (d)
                list.Add(new InputDevice(d["Name"] as string ?? "(unnamed)", d["DeviceID"] as string ?? "", d["PNPClass"] as string ?? "",
                    d["ConfigManagerErrorCode"] is uint code ? (int)code : null));
        return list;
    }
}

/// <summary>
/// Sticky, Filter, Toggle and Mouse Keys through SystemParametersInfo, which is the live truth (a shortcut key can switch
/// them on before the registry is written) and applies a change at once. Non-admin.
/// </summary>
public static class Accessibility
{
    public enum Kind { Sticky, Filter, Toggle, Mouse }

    private const uint UpdateAndBroadcast = 0x1 | 0x2; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE

    // Number of 4-byte members of each structure (cbSize, dwFlags, ...); the members after dwFlags are written back unchanged.
    private static (uint Get, uint Set, int Ints) Spec(Kind k) => k switch
    {
        Kind.Sticky => (0x3A, 0x3B, 2),   // STICKYKEYS
        Kind.Filter => (0x32, 0x33, 6),   // FILTERKEYS
        Kind.Toggle => (0x34, 0x35, 2),   // TOGGLEKEYS
        Kind.Mouse => (0x36, 0x37, 7),    // MOUSEKEYS
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] data, uint winIni);

    private static int[] Load(Kind k)
    {
        var (get, _, n) = Spec(k);
        var data = new int[n];
        data[0] = n * 4;
        if (!SystemParametersInfo(get, (uint)(n * 4), data, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return data;
    }

    public static int Get(Kind k) => Load(k)[1];

    /// <summary>Sets only the Flags member; every other member keeps the value Windows reported.</summary>
    public static void Set(Kind k, int flags)
    {
        var (_, set, n) = Spec(k);
        var data = Load(k);
        data[1] = flags;
        if (!SystemParametersInfo(set, (uint)(n * 4), data, UpdateAndBroadcast)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
