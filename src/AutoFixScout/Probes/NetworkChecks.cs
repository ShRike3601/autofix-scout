using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AutoFixScout.Probes;

/// <summary>
/// The extra read-only checks of the internet check: address from the router, Wi-Fi signal, IP address conflict and
/// metered connection. The parsing helpers are pure so they can be tested without a network.
/// </summary>
public static class NetworkChecks
{
    /// <summary>Weak Wi-Fi signal: below this quality (percent, as Windows shows it) the finding is raised.</summary>
    public const int WeakSignalPercent = 40;

    // ---- pure helpers ----

    /// <summary>169.254.0.0/16: the address Windows gives itself when no router answered (APIPA).</summary>
    public static bool IsApipa(string ipv4) =>
        IPAddress.TryParse(ipv4, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
        && ip.GetAddressBytes() is [169, 254, _, _];

    /// <summary>
    /// No usable address from the router: every IPv4 address is APIPA, or the adapter has no default gateway at all.
    /// </summary>
    public static Tri AddressObtained(IReadOnlyCollection<string> ipv4Addresses, bool hasDefaultGateway)
    {
        if (ipv4Addresses.Count > 0 && ipv4Addresses.All(IsApipa)) return Tri.No;
        if (!hasDefaultGateway) return Tri.No;
        return Tri.Yes;
    }

    /// <summary>dot11_phy_type values: a (4), vht/ac (8), he/ax (10) and eht/be (11) adapters work on the 5 GHz band. Others are not known to.</summary>
    public static Tri Supports5GHz(int phyType) => phyType is 4 or 8 or 10 or 11 ? Tri.Yes : Tri.Unknown;

    /// <summary>NLM_CONNECTION_COST: Fixed (2) and Variable (4) are what Windows treats as metered. Anything else is not.</summary>
    public static Tri IsMetered(uint cost) => (cost & 0x6) != 0 ? Tri.Yes : Tri.No;

    /// <summary>DefaultMediaCost registry value: 2 = Fixed, 3 = Variable are metered, 1 = Unrestricted.</summary>
    public static Tri IsMeteredRegistryValue(object? value) => value switch { 2 or 3 => Tri.Yes, 1 => Tri.No, _ => Tri.Unknown };

    /// <summary>Tcpip event 4199: the address of this computer is in use by another device. Last 24 hours.</summary>
    public const string ConflictQuery =
        "*[System[Provider[@Name='Tcpip'] and (EventID=4199) and TimeCreated[timediff(@SystemTime) <= 86400000]]]";

    // ---- reads ----

    /// <summary>Counts the WLAN-AutoConfig events 8003 and 8001 of the last 3 days. Only ids, reason codes and reason text are read.</summary>
    public static WifiDropStats ReadWifiDrops()
    {
        var events = new List<(int, int, string?)>();
        try
        {
            using var reader = EventLogs.Open("Microsoft-Windows-WLAN-AutoConfig/Operational", WifiDrops.Query, TimeSpan.FromDays(WifiDrops.Days));
            for (var ev = reader.ReadEvent(); ev != null; ev = reader.ReadEvent())
                using (ev) events.Add(WifiDrops.ParseEvent(ev.ToXml()));
        }
        catch (EventLogNotFoundException) { }
        var transitions = 0;
        try
        {
            var q = $"*[System[((Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=42) or (Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=13)) and TimeCreated[timediff(@SystemTime) <= {(long)TimeSpan.FromDays(WifiDrops.Days).TotalMilliseconds}]]]";
            using var r = EventLogs.Open("System", q, TimeSpan.FromDays(WifiDrops.Days));
            for (var ev = r.ReadEvent(); ev != null; ev = r.ReadEvent()) using (ev) transitions++;
        }
        catch (EventLogException) { }
        // Event 12013: an enterprise (802.1X) sign-in failed, e.g. because Windows has no saved sign-in to reconnect with.
        var signIn = 0;
        try
        {
            var q = $"*[System[EventID=12013 and TimeCreated[timediff(@SystemTime) <= {(long)TimeSpan.FromDays(WifiDrops.Days).TotalMilliseconds}]]]";
            using var r = EventLogs.Open("Microsoft-Windows-WLAN-AutoConfig/Operational", q, TimeSpan.FromDays(WifiDrops.Days));
            for (var ev = r.ReadEvent(); ev != null; ev = r.ReadEvent()) using (ev) signIn++;
        }
        catch (EventLogException) { }
        return WifiDrops.Count(events) with { SleepsAndShutdowns = transitions, SignInFailures = signIn };
    }

    /// <summary>Whether Windows may turn the adapter with this connection GUID off to save power (class key PnPCapabilities). Unknown when no key matches.</summary>
    public static Tri ReadWifiPowerSaving(string adapterGuid)
    {
        Activity.ReadingRegistry("HKLM", @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972...}", "adapter power saving setting");
        using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return Tri.Unknown;
        foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(name);
            if (k?.GetValue("NetCfgInstanceId") is string id && string.Equals(id, adapterGuid, StringComparison.OrdinalIgnoreCase))
                return WifiDrops.MayTurnOffToSavePower(k.GetValue("PnPCapabilities"));
        }
        return Tri.Unknown;
    }


    /// <summary>Driver date/version and the Wi-Fi tuning still pending, from the adapter's class key (read-only).</summary>
    public static (DateTime? Date, string? Version, IReadOnlyList<string> Pending) ReadWifiDriver(string adapterGuid)
    {
        Activity.ReadingRegistry("HKLM", @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972...}", "adapter driver and tuning settings");
        using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return (null, null, Array.Empty<string>());
        foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(name);
            if (k?.GetValue("NetCfgInstanceId") is not string id || !string.Equals(id, adapterGuid, StringComparison.OrdinalIgnoreCase)) continue;
            var (current, supported) = ReadTuningValues(k);
            var pending = Repair.WifiTuning.Plan(current, supported)
                .Select(p => Repair.WifiTuning.Targets.First(t => t.Keyword == p.Keyword).Meaning).ToList();
            return (Repair.WifiTuning.ParseDriverDate(k.GetValue("DriverDate") as string), k.GetValue("DriverVersion") as string, pending);
        }
        return (null, null, Array.Empty<string>());
    }

    internal static (Dictionary<string, string> Current, Dictionary<string, IReadOnlyCollection<string>> Supported) ReadTuningValues(Microsoft.Win32.RegistryKey k)
    {
        var current = new Dictionary<string, string>();
        var supported = new Dictionary<string, IReadOnlyCollection<string>>();
        foreach (var (kw, _, _) in Repair.WifiTuning.Targets)
        {
            if (k.GetValue(kw) is { } v) current[kw] = v.ToString()!;
            using var e = k.OpenSubKey($@"Ndi\Params\{kw}\Enum");
            if (e != null) supported[kw] = e.GetValueNames();
        }
        return (current, supported);
    }

    /// <summary>Yes when the current Wi-Fi connection uses enterprise (802.1X) sign-in. "WPA2-Enterprise" is not translated by Windows.</summary>
    public static Tri ReadWifiEnterprise()
    {
        Commands.Running("netsh.exe", "wlan show interfaces", "sign-in type only");
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "wlan", "show", "interfaces" }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return IsEnterprise(text);
    }

    public static Tri IsEnterprise(string netshOutput) =>
        netshOutput.Contains("-Enterprise", StringComparison.OrdinalIgnoreCase) || netshOutput.Contains("802.1X", StringComparison.OrdinalIgnoreCase) ? Tri.Yes
        : netshOutput.Contains("-Personal", StringComparison.OrdinalIgnoreCase) || netshOutput.Contains("Open", StringComparison.Ordinal) ? Tri.No : Tri.Unknown;
    /// <summary>Address state of the adapter with this connection GUID (Win32_NetworkAdapter.GUID).</summary>
    public static Tri ReadAddressObtained(string adapterGuid)
    {
        Activity.Report(ActivityKind.Network, "Reading the address and gateway state of the network adapter");
        var ni = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Id, adapterGuid, StringComparison.OrdinalIgnoreCase));
        if (ni is null || ni.OperationalStatus != OperationalStatus.Up) return Tri.Unknown;
        var props = ni.GetIPProperties();
        var v4 = props.UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList();
        var gateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
        return AddressObtained(v4, gateway);
    }

    /// <summary>True when the System log holds a Tcpip 4199 event from the last 24 hours. Only counts; the event text (addresses) is never read.</summary>
    public static bool ReadRecentIpConflict()
    {
        try
        {
            using var reader = EventLogs.Open("System", ConflictQuery, TimeSpan.FromDays(1));
            using var ev = reader.ReadEvent();
            return ev != null;
        }
        catch (EventLogNotFoundException) { return false; }
    }

    /// <summary>
    /// The machine's current connection cost through INetworkCostManager (Network List Manager COM, no Windows SDK projection).
    /// Falls back to the DefaultMediaCost registry value when the COM call is not available.
    /// </summary>
    public static Tri ReadMetered(bool wireless)
    {
        Activity.Report(ActivityKind.Network, "Asking the Network List Manager whether the connection is metered");
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type != null && Activator.CreateInstance(type) is INetworkCostManager mgr)
            {
                try
                {
                    mgr.GetCost(out var cost, IntPtr.Zero);
                    return IsMetered(cost);
                }
                finally { Marshal.ReleaseComObject(mgr); }
            }
        }
        catch (Exception) { /* fall through to the registry */ }
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\DefaultMediaCost");
        return IsMeteredRegistryValue(key?.GetValue(wireless ? "WiFi" : "Ethernet"));
    }

    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destination);
    }

    // ---- Native Wifi API (wlanapi.dll), read-only ----

    /// <summary>Signal quality (0-100) and 5 GHz support of the first connected Wi-Fi interface; null when there is none. The SSID is never read.</summary>
    public static (int SignalPercent, Tri Supports5GHz)? ReadWifi()
    {
        Activity.Report(ActivityKind.Network, "Reading Wi-Fi signal strength and band through the Native Wifi API (network name not read)");
        const int ErrorServiceNotActive = 1062;
        var rc = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (rc == ErrorServiceNotActive) return null;
        if (rc != 0) throw new InvalidOperationException($"WlanOpenHandle error {rc}");
        try
        {
            rc = WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (rc != 0) throw new InvalidOperationException($"WlanEnumInterfaces error {rc}");
            try
            {
                var count = Marshal.ReadInt32(list);
                for (var i = 0; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO_LIST: 8-byte header; each WLAN_INTERFACE_INFO is 532 bytes (GUID, 256 WCHAR, state).
                    var item = IntPtr.Add(list, 8 + i * 532);
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    if (Marshal.ReadInt32(item, 528) != 1) continue; // wlan_interface_state_connected
                    rc = WlanQueryInterface(handle, ref guid, 7 /* current_connection */, IntPtr.Zero, out _, out var data, out _);
                    if (rc != 0) throw new InvalidOperationException($"WlanQueryInterface error {rc}");
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state, mode, profile name (512 bytes), then WLAN_ASSOCIATION_ATTRIBUTES:
                        // SSID (36), bss type (4), BSSID (6, padded to 8), phy type, phy index, signal quality.
                        var phy = Marshal.ReadInt32(data, 568);
                        var quality = Marshal.ReadInt32(data, 576);
                        return (Math.Clamp(quality, 0, 100), Supports5GHz(phy));
                    }
                    finally { WlanFreeMemory(data); }
                }
                return null;
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opcode, IntPtr reserved, out int size, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}
