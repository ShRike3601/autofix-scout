namespace AutoFixScout.Probes;

/// <summary>The Bluetooth adapter (radio chip) as Device Manager shows it. ProblemCode 22 means "disabled".</summary>
public sealed record BtAdapterInfo(string Name, string InstanceId, int? ProblemCode)
{
    public bool Disabled => ProblemCode == 22;
}

/// <summary>A paired Bluetooth device. Connected: Unknown when Windows did not say.</summary>
public sealed record BtPairedDevice(string Name, string InstanceId, bool IsAudio, Tri Connected);

/// <summary>probe.bluetooth: adapter, radio switch, Bluetooth Support Service, paired devices. Tri.Unknown = not read.</summary>
public sealed class BluetoothState
{
    public IReadOnlyList<BtAdapterInfo> Adapters { get; set; } = new List<BtAdapterInfo>();
    /// <summary>The "Bluetooth" switch in Settings: Yes = on, No = off (or off through "Airplane mode"), Unknown = no radio reported.</summary>
    public Tri RadioOn { get; set; } = Tri.Unknown;
    /// <summary>Bluetooth Support Service (bthserv): Running, Stopped, NotFound, ...</summary>
    public string ServiceStatus { get; set; } = "Unknown";
    public IReadOnlyList<BtPairedDevice> Paired { get; set; } = new List<BtPairedDevice>();
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IBluetoothProbe { BluetoothState Read(); }
