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
}
