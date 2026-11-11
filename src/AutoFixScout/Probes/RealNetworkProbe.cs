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

        state.AirplaneMode = ReadAirplaneMode();
        if (!state.Adapters.Any(a => a.Connected)) return state; // nothing to test through

        state.GatewayReachable = await PingGatewayAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            state.DnsResolving = (await Dns.GetHostAddressesAsync(TestHost, cts.Token)).Length > 0 ? Tri.Yes : Tri.No;
        }
        catch (Exception) { state.DnsResolving = Tri.No; }

        if (state.DnsResolving == Tri.Yes)
        {
            try { state.InternetReachable = (await Http.GetStringAsync($"http://{TestHost}/connecttest.txt")).StartsWith("Microsoft Connect Test") ? Tri.Yes : Tri.No; }
            catch (Exception) { state.InternetReachable = Tri.No; }
        }
        else state.InternetReachable = Tri.No;
        return state;
    }

    private static List<NetAdapterInfo> ReadAdapters()
    {
        var list = new List<NetAdapterInfo>();
        using var q = new ManagementObjectSearcher(
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
        using var ping = new Ping();
        foreach (var gw in gateways)
            for (var attempt = 0; attempt < 2; attempt++)
                try { if ((await ping.SendPingAsync(gw, 1000)).Status == IPStatus.Success) return Tri.Yes; } catch (PingException) { }
        // Some routers ignore ping; triage only treats this as the cause when DNS fails too.
        return Tri.No;
    }
}
