namespace AutoFixScout.Probes;

public enum Tri { Yes, No, Unknown }

public sealed record ServiceInfo(string Name, string Status);   // Status: Running, Stopped, NotFound, Unknown

public sealed record AudioEndpointInfo(string Name, bool IsDefault, bool IsActive, string KindGuess); // KindGuess: Speakers, Headphones, Hdmi, Bluetooth, Other (guessed from the name text only)

public sealed record AudioDeviceInfo(string Name, string InstanceId, int? ProblemCode, string? DriverVersion, string? DriverDate, string? DriverProvider);

public sealed record AudioState(
    IReadOnlyList<ServiceInfo> Services,
    IReadOnlyList<AudioEndpointInfo> Endpoints,
    IReadOnlyList<AudioDeviceInfo> Devices,
    Tri DefaultMuted,
    int? DefaultVolumePercent,
    IReadOnlyList<string> ProbeErrors);

public interface IAudioProbe { AudioState Read(); }
