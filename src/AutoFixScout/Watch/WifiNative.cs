using System.Runtime.InteropServices;

namespace AutoFixScout.Watching;

/// <summary>
/// Reads and reconnects Wi-Fi through the Native Wifi API (wlanapi.dll), which does not depend on the Windows display language.
/// Read-only except <see cref="Reconnect"/>. Key material is never requested: WLAN_PROFILE_GET_PLAINTEXT_KEY is never passed, so
/// the profile XML holds no password. Profile names stay in memory. Every method returns a failure instead of throwing,
/// so the caller can fall back to netsh.
/// </summary>
public static class WifiNative
{
    private const int InterfaceStateConnected = 1;
    private const int OpcodeCurrentConnection = 7;
    private const int ConnectionModeProfile = 0;
    private const int BssTypeAny = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionParameters
    {
        public int Mode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Ssid;
        public IntPtr BssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr handle, ref Guid iface, string profile, IntPtr reserved, out IntPtr xml, IntPtr flags, out uint access);
    [DllImport("wlanapi.dll")] private static extern uint WlanConnect(IntPtr handle, ref Guid iface, ref ConnectionParameters p, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

    private static bool ApiFailure(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or SEHException;

    private static IntPtr Open()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) throw new InvalidOperationException("WlanOpenHandle failed");
        return h;
    }

    private static List<Guid> Interfaces(IntPtr h)
    {
        var result = new List<Guid>();
        if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) throw new InvalidOperationException("WlanEnumInterfaces failed");
        try
        {
            var n = Marshal.ReadInt32(list);
            for (var i = 0; i < n; i++)
            {
                // WLAN_INTERFACE_INFO_LIST: two DWORDs, then entries of 532 bytes (GUID, 256 WCHAR description, state).
                var bytes = new byte[16];
                Marshal.Copy(list + 8 + i * 532, bytes, 0, 16);
                result.Add(new Guid(bytes));
            }
        }
        finally { WlanFreeMemory(list); }
        return result;
    }

    /// <summary>Ok = false when the API could not be used. Connected and Profile come from the first connected interface.</summary>
    public static (bool Ok, bool Connected, string? Profile) ReadState()
    {
        try
        {
            var h = Open();
            try
            {
                foreach (var g0 in Interfaces(h))
                {
                    var g = g0;
                    if (WlanQueryInterface(h, ref g, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) continue;
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state (4), mode (4), profile name (256 WCHAR).
                        if (Marshal.ReadInt32(data) != InterfaceStateConnected) continue;
                        var name = (Marshal.PtrToStringUni(data + 8, 256) ?? "").Split('\0')[0];
                        return (true, true, string.IsNullOrWhiteSpace(name) ? null : name);
                    }
                    finally { WlanFreeMemory(data); }
                }
                return (true, false, null);
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }
        catch (Exception ex) when (ApiFailure(ex)) { return (false, false, null); }
    }

    /// <summary>Profile XML (without key material) of a saved profile; null when it is missing or the API fails.</summary>
    private static string? GetProfileXml(IntPtr h, Guid g, string profile)
    {
        if (WlanGetProfile(h, ref g, profile, IntPtr.Zero, out var xml, IntPtr.Zero, out _) != 0) return null;
        try { return Marshal.PtrToStringUni(xml); }
        finally { WlanFreeMemory(xml); }
    }

    /// <summary>True/false from the profile's connectionMode; null when the API could not answer (use netsh then).</summary>
    public static bool? IsAutoConnect(string profile)
    {
        try
        {
            var h = Open();
            try
            {
                foreach (var g in Interfaces(h))
                    if (GetProfileXml(h, g, profile) is { } xml) return WifiNetsh.IsAutoConnectXml(xml);
                return null;
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }
        catch (Exception ex) when (ApiFailure(ex)) { return null; }
    }

    /// <summary>
    /// For the live check only: connected or not, and whether the current profile is auto-connect. Never the name.
    /// Ok = false when the API failed; AutoConnect is null when there is no current profile or it could not be read.
    /// </summary>
    public static (bool Ok, bool Connected, bool? AutoConnect) Describe()
    {
        var (ok, connected, profile) = ReadState();
        return (ok, connected, profile is null ? null : IsAutoConnect(profile));
    }

    /// <summary>Asks Windows to connect the saved profile on the interface that has it. False when the API could not be used.</summary>
    public static bool Reconnect(string profile)
    {
        try
        {
            var h = Open();
            try
            {
                foreach (var g0 in Interfaces(h))
                {
                    var g = g0;
                    if (GetProfileXml(h, g, profile) is null) continue;
                    var p = new ConnectionParameters { Mode = ConnectionModeProfile, Profile = profile, BssType = BssTypeAny };
                    return WlanConnect(h, ref g, ref p, IntPtr.Zero) == 0;
                }
                return false;
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }
        catch (Exception ex) when (ApiFailure(ex)) { return false; }
    }
}
