namespace AutoFixScout.Probes;

/// <summary>
/// A sound input (capture endpoint). IsActive = present and enabled; Disabled = turned off in Sound settings;
/// otherwise unplugged. KindGuess as for outputs (Bluetooth, Headphones = headset, Other, ...).
/// </summary>
public sealed record MicEndpointInfo(
    string Name,
    bool IsDefault,
    bool IsActive,
    bool Disabled,
    string KindGuess,
    string? DeviceInstanceId = null // the MEDIA device (in MicrophoneState.Devices) this input belongs to; null = unknown
);

/// <summary>probe.microphone: capture endpoints, default input level, privacy switches, sound devices. Tri.Unknown = not read.</summary>
public sealed class MicrophoneState
{
    public IReadOnlyList<ServiceInfo> Services { get; set; } = new List<ServiceInfo>();
    public IReadOnlyList<MicEndpointInfo> Endpoints { get; set; } = new List<MicEndpointInfo>();
    /// <summary>Sound devices (PnP class MEDIA): microphones sit on the same chips as speakers.</summary>
    public IReadOnlyList<AudioDeviceInfo> Devices { get; set; } = new List<AudioDeviceInfo>();
    public Tri DefaultMuted { get; set; } = Tri.Unknown;
    public int? DefaultLevelPercent { get; set; }
    /// <summary>Settings > Privacy > Microphone > "Microphone access" (whole device, HKLM).</summary>
    public Tri DeviceAccess { get; set; } = Tri.Unknown;
    /// <summary>"Let apps access your microphone" (this user, HKCU).</summary>
    public Tri AppAccess { get; set; } = Tri.Unknown;
    /// <summary>"Let desktop apps access your microphone" (Zoom, browsers, Teams classic).</summary>
    public Tri DesktopAppAccess { get; set; } = Tri.Unknown;
    /// <summary>Friendly names of apps with their own choice set to "Deny" (Settings > Privacy > Microphone, per app).</summary>
    public IReadOnlyList<string> DeniedApps { get; set; } = new List<string>();
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IMicrophoneProbe { MicrophoneState Read(); }
