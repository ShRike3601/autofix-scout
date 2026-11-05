namespace AutoFixScout;

/// <summary>
/// Read-only probe models (section 7.1).
/// </summary>
public sealed class ServiceState
{
    public string Name { get; set; } = "";
    public bool Running { get; set; }
}

public sealed class DeviceInfo
{
    public string Name { get; set; } = "";
    public string DeviceID { get; set; } = "";
    public string PNPClass { get; set; } = "";  // MEDIA, AudioEndpoint, Camera, Image, Net
    public int ConfigManagerErrorCode { get; set; }
}

public sealed class DriverInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Provider { get; set; } = "";
    public string INF { get; set; } = "";
    public string HardwareID { get; set; } = "";
}

public sealed class AudioState
{
    public string DefaultOutputDevice { get; set; } = "";
    public bool Muted { get; set; }
    public int Volume { get; set; }  // 0-100
    public string EndpointType { get; set; } = "";  // "Speakers", "Headphones", "HDMI", etc.
    public List<string> ActiveEndpoints { get; set; } = new();
}

public sealed class CameraPrivacyState
{
    public bool CameraAccessAllowed { get; set; }
    public List<string> AppsCurrentlyUsingCamera { get; set; } = new();
}

/// <summary>
/// CameraAppUsage for tracking apps using camera.
/// </summary>
public sealed class CameraAppUsage
{
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
}

/// <summary>
/// Symptom type for session tracking (moved from DatabaseModels).
/// </summary>
public enum Symptom { Audio, Camera, Network, Other }

/// <summary>
/// Outcome type for session tracking (moved from DatabaseModels).
/// </summary>
public enum Outcome { Fixed, NotFixed, ProbableHardware, Abandoned }

/// <summary>
/// Network adapter state (for probe operations).
/// </summary>
public sealed class NetworkState
{
    public string AdapterName { get; set; } = "";
    public bool AirplaneMode { get; set; }
    public bool Enabled { get; set; }
    public bool GatewayReachable { get; set; }
    public bool DnsResolving { get; set; }
}

public sealed class UpdateHistory
{
    public string AdapterName { get; set; } = "";
    public bool AirplaneMode { get; set; }
    public bool Enabled { get; set; }
    public bool GatewayReachable { get; set; }
    public bool DnsResolving { get; set; }
}

public sealed class DriverUpdate
{
    public string DeviceId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
