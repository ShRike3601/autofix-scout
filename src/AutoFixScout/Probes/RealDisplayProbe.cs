using System.Management;
using System.Runtime.InteropServices;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.display: display adapters (Win32_PnPEntity, Display class), screens Windows detects (root\wmi
/// WmiMonitorBasicDisplayParams), screens showing the desktop (EnumDisplayDevices) and built-in screen brightness
/// (WmiMonitorBrightness). Read-only, no admin, never throws.
/// </summary>
public sealed class RealDisplayProbe : IDisplayProbe
{
    public DisplayState Read()
    {
        var errors = new List<string>();
        var state = new DisplayState { ProbeErrors = errors };

        try { state.Adapters = ReadAdapters(); }
        catch (Exception ex) { errors.Add($"Display adapters: {ex.Message}"); }

        try
        {
            using var q = new ManagementObjectSearcher(@"root\wmi", "SELECT Active FROM WmiMonitorBasicDisplayParams");
            state.ConnectedScreens = q.Get().Cast<ManagementBaseObject>().Count(m => m["Active"] is true);
        }
        catch (Exception ex) { errors.Add($"WmiMonitorBasicDisplayParams: {ex.Message}"); }

        try { state.ShowingScreens = CountShowingScreens(); }
        catch (Exception ex) { errors.Add($"EnumDisplayDevices: {ex.Message}"); }

        try { state.Screens = ReadScreens(); }
        catch (Exception ex) { errors.Add($"EnumDisplaySettings: {ex.Message}"); }

        // Desktops have no software brightness ("Not supported"): that is normal, not a probe error.
        try
        {
            using var q = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness, Active FROM WmiMonitorBrightness");
            state.BrightnessPercent = q.Get().Cast<ManagementBaseObject>()
                .Where(m => m["Active"] is true).Select(m => (int?)Convert.ToInt32(m["CurrentBrightness"])).FirstOrDefault();
        }
        catch (Exception) { state.BrightnessPercent = null; }
        return state;
    }

    private static List<DisplayAdapterInfo> ReadAdapters()
    {
        using var q = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Display'");
        return q.Get().Cast<ManagementBaseObject>()
            .Select(d => new DisplayAdapterInfo(
                d["Name"] as string ?? "",
                d["DeviceID"] as string ?? "",
                d["ConfigManagerErrorCode"] is uint code ? (int)code : null))
            .ToList();
    }

    private const int AttachedToDesktop = 0x1; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP (output) / DISPLAY_DEVICE_ACTIVE (monitor)
    private const int MirroringDriver = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICE info, uint flags);

    /// <summary>
    /// Screens showing the desktop: active monitors on each output that is part of the desktop. In "Duplicate" one
    /// output carries two active monitors, so both count.
    /// </summary>
    private static int CountShowingScreens()
    {
        var count = 0;
        for (uint i = 0; i < 64; i++)
        {
            var output = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref output, 0)) break;
            if ((output.StateFlags & AttachedToDesktop) == 0 || (output.StateFlags & MirroringDriver) != 0) continue;
            var monitors = 0;
            for (uint j = 0; j < 16; j++)
            {
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevicesW(output.DeviceName, j, ref mon, 0)) break;
                if ((mon.StateFlags & AttachedToDesktop) != 0) monitors++;
            }
            count += Math.Max(monitors, 1); // an attached output shows the desktop on at least one screen
        }
        return count;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    private const int CurrentSettings = -1;

    /// <summary>Read-only: current mode and every supported mode of each active, non-mirroring output.</summary>
    internal static List<ScreenInfo> ReadScreens()
    {
        var screens = new List<ScreenInfo>();
        for (uint i = 0; i < 64; i++)
        {
            var output = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref output, 0)) break;
            if ((output.StateFlags & AttachedToDesktop) == 0 || (output.StateFlags & MirroringDriver) != 0) continue;
            var cur = NewMode();
            if (!EnumDisplaySettingsW(output.DeviceName, CurrentSettings, ref cur)) continue;
            var modes = new HashSet<DisplayMode>();
            for (var m = 0; m < 5000; m++)
            {
                var dm = NewMode();
                if (!EnumDisplaySettingsW(output.DeviceName, m, ref dm)) break;
                if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0) continue;
                modes.Add(new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency));
            }
            screens.Add(new ScreenInfo(output.DeviceName,
                new DisplayMode((int)cur.dmPelsWidth, (int)cur.dmPelsHeight, (int)cur.dmDisplayFrequency),
                modes.ToList(), ReadScale(cur.dmPositionX, cur.dmPositionY, output.DeviceName)));
        }
        return screens;
    }

    private static DEVMODE NewMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    /// <summary>Scale in percent for the monitor at the output's top-left corner: GetDpiForMonitor, else the registry.</summary>
    private static int? ReadScale(int x, int y, string device)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0 /* effective */, out var dx, out _) == 0 && dx > 0)
                return (int)Math.Round(dx * 100.0 / 96);
        }
        catch (Exception) { /* fall through to the registry */ }
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics");
            if (k?.GetValue("AppliedDPI") is int dpi && dpi > 0) return (int)Math.Round(dpi * 100.0 / 96);
            using var d = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (d?.GetValue("LogPixels") is int lp && lp > 0) return (int)Math.Round(lp * 100.0 / 96);
        }
        catch (Exception) { }
        return null;
    }
}
