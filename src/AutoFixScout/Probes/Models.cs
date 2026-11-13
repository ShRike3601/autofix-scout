namespace AutoFixScout.Probes;

public enum Tri { Yes, No, Unknown }

public sealed record ServiceInfo(string Name, string Status);   // Status: Running, Stopped, NotFound, Unknown

public sealed record AudioEndpointInfo(
    string Name,
    bool IsDefault,
    bool IsActive,
    string KindGuess,
    string? DeviceInstanceId = null // the MEDIA device (in AudioState.Devices) this output belongs to; null = unknown
); // KindGuess: Speakers, Headphones, Hdmi, Bluetooth, Other (guessed from the bus, form factor and name)

public sealed record AudioDeviceInfo(
    string Name,
    string InstanceId,
    int? ProblemCode,
    string? DriverVersion,
    string? DriverDate,
    string? DriverProvider,
    AutoFixScout.Drivers.DriverStatus? Driver = null
);

/// <summary>One app in the Volume mixer: process name, optional display name, session volume and mute, and whether it is playing.</summary>
public sealed record AudioSessionInfo(string Process, string? DisplayName, int VolumePercent, bool Muted, bool Active)
{
    public string Label => DisplayName ?? Process;
}

public sealed class AudioState
{
    public IReadOnlyList<ServiceInfo> Services { get; set; } = new List<ServiceInfo>();
    public IReadOnlyList<AudioEndpointInfo> Endpoints { get; set; } = new List<AudioEndpointInfo>();
    public IReadOnlyList<AudioDeviceInfo> Devices { get; set; } = new List<AudioDeviceInfo>();
    public Tri DefaultMuted { get; set; } = Tri.Unknown;
    public int? DefaultVolumePercent { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
    public IReadOnlyList<AudioSessionInfo> Sessions { get; set; } = new List<AudioSessionInfo>();
    /// <summary>Why the sessions could not be read; does not block the check (the device-level findings stand on their own).</summary>
    public string? SessionError { get; set; }
    /// <summary>Set by the UI when the person talks about calls; triage adds the call findings only then.</summary>
    public bool CallFocus { get; set; }
    /// <summary>Communications ducking preference (absent value = Windows default); null = unreadable.</summary>
    public Ducking? Ducking { get; set; }
    /// <summary>Default output for media and for communications (id, name); null = none or unknown.</summary>
    public string? MultimediaDeviceId { get; set; }
    public string? MultimediaDeviceName { get; set; }
    public string? CommsDeviceId { get; set; }
    public string? CommsDeviceName { get; set; }
}

public interface IAudioProbe { AudioState Read(); }
