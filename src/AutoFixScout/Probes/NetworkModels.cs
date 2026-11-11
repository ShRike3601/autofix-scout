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
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface INetworkProbe { Task<NetworkState> ReadAsync(); }
