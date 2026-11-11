namespace AutoFixScout.Probes;

public enum Tri { Yes, No, Unknown }

public sealed record ServiceInfo(string Name, string Status);   // Status: Running, Stopped, NotFound, Unknown

public sealed record AudioEndpointInfo(
    string Name,
    bool IsDefault,
    bool IsActive,
    string KindGuess
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

public sealed class AudioState
{
    public IReadOnlyList<ServiceInfo> Services { get; set; } = new List<ServiceInfo>();
    public IReadOnlyList<AudioEndpointInfo> Endpoints { get; set; } = new List<AudioEndpointInfo>();
    public IReadOnlyList<AudioDeviceInfo> Devices { get; set; } = new List<AudioDeviceInfo>();
    public Tri DefaultMuted { get; set; } = Tri.Unknown;
    public int? DefaultVolumePercent { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IAudioProbe { AudioState Read(); }
