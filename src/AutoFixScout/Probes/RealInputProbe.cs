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
            s.TouchpadEnabled = ReadTri(TouchpadKey + @"\Status", "Enabled");
            s.LeaveOnWithMouse = ReadTri(TouchpadKey, "LeaveOnWithMouse");
        }
        catch (Exception ex) { errors.Add($"Touchpad: {ex.Message}"); }
        try
        {
            s.StickyKeysFlags = Accessibility.Get(Accessibility.Kind.Sticky);
            s.FilterKeysFlags = Accessibility.Get(Accessibility.Kind.Filter);
            s.ToggleKeysFlags = Accessibility.Get(Accessibility.Kind.Toggle);
            s.MouseKeysFlags = Accessibility.Get(Accessibility.Kind.Mouse);
        }
        catch (Exception ex) { errors.Add($"Accessibility: {ex.Message}"); }
        return s;
    }

    private static Tri ReadTri(string path, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path);
        return k?.GetValue(name) is int v ? (v != 0 ? Tri.Yes : Tri.No) : Tri.Unknown;
    }

    private static List<InputDevice> ReadDevices()
    {
        var list = new List<InputDevice>();
        using var q = new ManagementObjectSearcher(
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
