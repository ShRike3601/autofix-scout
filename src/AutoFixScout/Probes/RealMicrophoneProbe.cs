using System.ServiceProcess;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.microphone: Core Audio capture endpoints (like RealAudioProbe does for outputs), the default input's mute and
/// level, the microphone privacy switches, and the sound devices. Read-only, no admin, never throws: every sub-probe
/// failure goes into ProbeErrors so triage fails closed.
/// </summary>
public sealed class RealMicrophoneProbe : IMicrophoneProbe
{
    private const string ConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private static readonly string[] AudioServices = { "AudioSrv", "AudioEndpointBuilder" };

    public MicrophoneState Read()
    {
        var errors = new List<string>();
        var state = new MicrophoneState { ProbeErrors = errors };

        state.Services = AudioServices.Select(name =>
        {
            try
            {
                using var sc = new ServiceController(name);
                return new ServiceInfo(name, sc.Status.ToString());
            }
            catch (Exception) { return new ServiceInfo(name, "NotFound"); }
        }).ToList();

        try { state.Devices = RealAudioProbe.ReadMediaDevices(); }
        catch (Exception ex) { errors.Add($"WMI: {ex.Message}"); }

        try { ReadEndpoints(state); }
        catch (Exception ex) { errors.Add($"Core Audio: {ex.Message}"); }

        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(ConsentKey);
            using var hkcu = Registry.CurrentUser.OpenSubKey(ConsentKey);
            using var desktop = hkcu?.OpenSubKey("NonPackaged");
            state.DeviceAccess = Allowed(hklm);
            state.AppAccess = Allowed(hkcu);
            state.DesktopAppAccess = Allowed(desktop);
            state.DeniedApps = AppConsent.ReadDenied(hkcu);
        }
        catch (Exception ex) { errors.Add($"Consent store: {ex.Message}"); }
        return state;
    }

    private static void ReadEndpoints(MicrophoneState state)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            defaultId = def.ID;
            state.DefaultMuted = def.AudioEndpointVolume.Mute ? Tri.Yes : Tri.No;
            state.DefaultLevelPercent = (int)Math.Round(def.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        }

        // NotPresent is excluded: Windows keeps every input ever seen, which would flood the list.
        var states = DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled;
        state.Endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, states)
            .Select(d => new MicEndpointInfo(d.FriendlyName, d.ID == defaultId, d.State == DeviceState.Active,
                d.State == DeviceState.Disabled, RealAudioProbe.GuessKind(d), RealAudioProbe.OwningDevice(d, state.Devices)))
            .ToList();
    }

    private static Tri Allowed(RegistryKey? key) => (key?.GetValue("Value") as string) switch
    {
        "Allow" => Tri.Yes,
        "Deny" => Tri.No,
        _ => Tri.Unknown,
    };
}
