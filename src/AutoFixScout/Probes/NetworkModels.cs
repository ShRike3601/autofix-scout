namespace AutoFixScout.Probes;

/// <summary>A physical network adapter. ProblemCode 22 means "disabled" (Device Manager code).</summary>
public sealed record NetAdapterInfo(string Name, string InstanceId, bool IsWireless, bool Connected, int? ProblemCode, Drivers.DriverStatus? Driver = null)
{
    public bool Disabled => ProblemCode == 22;
}

/// <summary>probe.network (spec 7.1). Tri.Unknown means "not tested", never "passed".</summary>
public sealed class NetworkState
{
    public IReadOnlyList<NetAdapterInfo> Adapters { get; set; } = new List<NetAdapterInfo>();
    public Tri AirplaneMode { get; set; } = Tri.Unknown;
    public Tri GatewayReachable { get; set; } = Tri.Unknown;
    public Tri DnsResolving { get; set; } = Tri.Unknown;
    public Tri InternetReachable { get; set; } = Tri.Unknown;
    /// <summary>DNS servers typed in by hand on the connected adapter (empty = automatic). VPNs like WARP set 127.x here.</summary>
    public IReadOnlyList<string> StaticDnsServers { get; set; } = new List<string>();
    /// <summary>The adapter StaticDnsServers belongs to.</summary>
    public string? StaticDnsAdapterId { get; set; }
    /// <summary>Did the connected adapter get a usable address from the router? No = only 169.254.x.x (APIPA) or no default gateway.</summary>
    public Tri AddressObtained { get; set; } = Tri.Unknown;
    /// <summary>Wi-Fi signal quality in percent (Native Wifi API); null when not on Wi-Fi or not read. The network name is never kept.</summary>
    public int? WifiSignalPercent { get; set; }
    /// <summary>Yes when the Wi-Fi adapter is known to support the 5 GHz band.</summary>
    public Tri Supports5GHz { get; set; } = Tri.Unknown;
    /// <summary>A Tcpip event 4199 (address already in use) was logged in the last 24 hours.</summary>
    public bool IpConflictRecent { get; set; }
    /// <summary>Yes when Windows treats the current connection as metered (Windows Update and OneDrive may pause).</summary>
    public Tri Metered { get; set; } = Tri.Unknown;
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
    /// <summary>Failures of the extra checks above. Kept apart so they never replace the main result with "could not complete".</summary>
    public IReadOnlyList<string> ExtraProbeErrors { get; set; } = new List<string>();
}

public interface INetworkProbe { Task<NetworkState> ReadAsync(); }
