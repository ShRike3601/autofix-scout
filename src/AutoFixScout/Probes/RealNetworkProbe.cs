using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.network: adapters (WMI), airplane mode, gateway ping, DNS lookup, and the same connectivity test Windows uses (NCSI).
/// Read-only, no admin, and every network test has a short timeout so it works with the network down.
/// </summary>
public sealed class RealNetworkProbe : INetworkProbe
{
    private const string TestHost = "www.msftconnecttest.com";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<NetworkState> ReadAsync()
    {
        var errors = new List<string>();
        var state = new NetworkState { ProbeErrors = errors };

        try { state.Adapters = ReadAdapters(); }
        catch (Exception ex) { errors.Add($"WMI: {ex.Message}"); return state; }

        Activity.Report(ActivityKind.Network, "Checking the network adapters, Airplane mode and the connection tests");
        state.AirplaneMode = ReadAirplaneMode();
        if (!state.Adapters.Any(a => a.Connected)) return state; // nothing to test through

        try
        {
            var connected = state.Adapters.OrderByDescending(a => a.IsWireless).First(a => a.Connected);
            state.StaticDnsServers = ReadStaticDns(connected.InstanceId);
            state.StaticDnsAdapterId = connected.InstanceId;
        }
        catch (Exception ex) { errors.Add($"DNS settings: {ex.Message}"); }

        ReadExtras(state);

        state.GatewayReachable = await PingGatewayAsync();
        Activity.Report(ActivityKind.Network, $"Testing DNS: resolving {TestHost}");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            state.DnsResolving = (await Dns.GetHostAddressesAsync(TestHost, cts.Token)).Length > 0 ? Tri.Yes : Tri.No;
        }
        catch (Exception) { state.DnsResolving = Tri.No; }

        Activity.Report(ActivityKind.Network, state.DnsResolving == Tri.Yes ? "DNS lookup succeeded" : "DNS lookup failed");
        if (state.DnsResolving == Tri.Yes)
        {
            Activity.Report(ActivityKind.Network, $"HTTP GET http://{TestHost}/connecttest.txt");
            try { state.InternetReachable = (await Http.GetStringAsync($"http://{TestHost}/connecttest.txt")).StartsWith("Microsoft Connect Test") ? Tri.Yes : Tri.No; }
            catch (Exception) { state.InternetReachable = Tri.No; }
        }
        else state.InternetReachable = Tri.No;
        Activity.Report(ActivityKind.Analyze, state.InternetReachable == Tri.Yes ? "The connectivity test answered" : "The connectivity test did not answer");
        return state;
    }

    /// <summary>Address, Wi-Fi signal, IP conflict and metered cost. Each read is separate; a failure goes to ExtraProbeErrors only.</summary>
    private static void ReadExtras(NetworkState state)
    {
        var errors = new List<string>();
        state.ExtraProbeErrors = errors;
        var connected = state.Adapters.OrderByDescending(a => a.IsWireless).First(a => a.Connected);
        try
        {
            var guid = ReadAdapterGuid(connected.InstanceId);
            if (guid != null) state.AddressObtained = NetworkChecks.ReadAddressObtained(guid);
        }
        catch (Exception ex) { errors.Add($"Address: {ex.Message}"); }
        if (connected.IsWireless)
        {
            try
            {
                if (NetworkChecks.ReadWifi() is { } wifi) { state.WifiSignalPercent = wifi.SignalPercent; state.Supports5GHz = wifi.Supports5GHz; }
            }
            catch (Exception ex) { errors.Add($"Wi-Fi signal: {ex.Message}"); }
        }
        var wifiAdapter = state.Adapters.Where(a => a.IsWireless).OrderByDescending(a => a.Connected).FirstOrDefault();
        if (wifiAdapter != null)
        {
            try { state.WifiDrops = NetworkChecks.ReadWifiDrops(); }
            catch (Exception ex) { errors.Add($"Wi-Fi events: {ex.Message}"); }
            try
            {
                var wguid = ReadAdapterGuid(wifiAdapter.InstanceId);
                if (wguid != null) state.WifiPowerSavingMayTurnOff = NetworkChecks.ReadWifiPowerSaving(wguid);
                if (wguid != null)
                {
                    var (date, version, pending) = NetworkChecks.ReadWifiDriver(wguid);
                    state.WifiDriverDate = date; state.WifiDriverVersion = version; state.WifiTuningPending = pending;
                    state.WifiInstanceId = wifiAdapter.InstanceId;
                }
            }
            catch (Exception ex) { errors.Add($"Wi-Fi power setting: {ex.Message}"); }
            try { if (wifiAdapter.Connected) state.WifiEnterprise = NetworkChecks.ReadWifiEnterprise(); }
            catch (Exception ex) { errors.Add($"Wi-Fi security type: {ex.Message}"); }
        }
        try
        {
            state.WifiWatcher = Watching.WifiWatcherSetup.ReadState();
            state.WifiWatch = new Watching.WifiWatchStore().Summarize(DateTime.UtcNow);
        }
        catch (Exception ex) { errors.Add($"Wi-Fi watcher: {ex.Message}"); }
        try { state.IpConflictRecent = NetworkChecks.ReadRecentIpConflict(); }
        catch (Exception ex) { errors.Add($"Event log: {ex.Message}"); }
        try { state.Metered = NetworkChecks.ReadMetered(connected.IsWireless); }
        catch (Exception ex) { errors.Add($"Connection cost: {ex.Message}"); }
    }

    private static string? ReadAdapterGuid(string instanceId)
    {
        using var q = Wmi.Searcher(
            $"SELECT GUID FROM Win32_NetworkAdapter WHERE PNPDeviceID = '{instanceId.Replace(@"\", @"\\").Replace("'", "")}'");
        return q.Get().Cast<ManagementBaseObject>().Select(a => a["GUID"] as string).FirstOrDefault(g => g != null);
    }

    private static List<NetAdapterInfo> ReadAdapters()
    {
        var list = new List<NetAdapterInfo>();
        using var q = Wmi.Searcher(
            "SELECT Name, NetConnectionID, PNPDeviceID, NetConnectionStatus, ConfigManagerErrorCode FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
        foreach (var a in q.Get())
        {
            var id = a["PNPDeviceID"] as string ?? "";
            // Software and Bluetooth PAN adapters aren't what people mean by "my internet".
            if (id.Length == 0 || id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTH", StringComparison.OrdinalIgnoreCase)) continue;
            var name = a["Name"] as string ?? "";
            var label = $"{name} {a["NetConnectionID"]}".ToLowerInvariant();
            list.Add(new NetAdapterInfo(
                name, id,
                IsWireless: label.Contains("wi-fi") || label.Contains("wireless") || label.Contains("wlan") || label.Contains("802.11"),
                Connected: a["NetConnectionStatus"] is ushort status && status == 2,
                ProblemCode: a["ConfigManagerErrorCode"] is uint code ? (int)code : null,
                Driver: Drivers.DriverStatus.For(id)));
        }
        return list;
    }

    // shortcut: reads the RadioManagement registry value (not a documented API), switch to Windows.Devices.Radios if it proves unreliable.
    private static Tri ReadAirplaneMode()
    {
        try
        {
            Activity.ReadingRegistry("HKLM", @"SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState");
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState");
            return key?.GetValue("") switch { 1 => Tri.Yes, 0 => Tri.No, _ => Tri.Unknown };
        }
        catch (Exception) { return Tri.Unknown; }
    }

    private static async Task<Tri> PingGatewayAsync()
    {
        var gateways = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .Where(ip => !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
            .Distinct().ToList();
        if (gateways.Count == 0) return Tri.No;
        Activity.Report(ActivityKind.Network, $"Pinging the default gateway ({Activity.Count(gateways.Count, "address")}, up to 2 tries each)");
        using var ping = new Ping();
        foreach (var gw in gateways)
            for (var attempt = 0; attempt < 2; attempt++)
                try { if ((await ping.SendPingAsync(gw, 1000)).Status == IPStatus.Success) return Tri.Yes; } catch (PingException) { }
        // Some networks block ping to the router (the test laptop's campus Wi-Fi does). The ping still makes Windows
        // look the router up on the local network, so a "Reachable" neighbour entry means it answered.
        try { if (gateways.Any(NeighbourReachable)) return Tri.Yes; } catch (Exception) { }
        return Tri.No;
    }

    /// <summary>MSFT_NetNeighbor State: 5 Reachable, 6 Permanent. Stale/Probe/Incomplete don't prove the router is there now.</summary>
    private static bool NeighbourReachable(IPAddress gateway)
    {
        using var q = Wmi.Searcher(@"root\StandardCimv2",
            $"SELECT State FROM MSFT_NetNeighbor WHERE IPAddress = '{gateway}'");
        return q.Get().Cast<ManagementBaseObject>().Any(n => Convert.ToInt32(n["State"]) is 5 or 6);
    }

    /// <summary>Hand-typed DNS servers live in the interface's NameServer value; DHCP ones in DhcpNameServer.</summary>
    private static List<string> ReadStaticDns(string instanceId)
    {
        using var q = Wmi.Searcher(
            $"SELECT GUID FROM Win32_NetworkAdapter WHERE PNPDeviceID = '{instanceId.Replace(@"\", @"\\").Replace("'", "")}'");
        var guid = q.Get().Cast<ManagementBaseObject>().Select(a => a["GUID"] as string).FirstOrDefault(g => g != null);
        if (guid is null) return new List<string>();
        Activity.ReadingRegistry("HKLM", @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", "DNS server settings of the adapter");
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}");

        return ((key?.GetValue("NameServer") as string) ?? "")
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
