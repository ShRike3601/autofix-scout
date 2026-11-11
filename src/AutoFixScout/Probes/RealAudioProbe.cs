using System.Management;
using System.ServiceProcess;
using NAudio.CoreAudioApi;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.audio_state + probe.service_state + probe.devices/drivers for the MEDIA class (spec 7.1). Read-only, no admin.
/// Any sub-probe failure goes into ProbeErrors so triage fails closed.
/// </summary>
public sealed class RealAudioProbe : IAudioProbe
{
    private static readonly string[] AudioServices = { "AudioSrv", "AudioEndpointBuilder" };

    public AudioState Read()
    {
        var errors = new List<string>();
        var state = new AudioState { ProbeErrors = errors };

        state.Services = AudioServices.Select(name =>
        {
            try
            {
                using var sc = new ServiceController(name);
                return new ServiceInfo(name, sc.Status.ToString());
            }
            catch (InvalidOperationException) { return new ServiceInfo(name, "NotFound"); }
        }).ToList();

        try { ReadEndpoints(state); }
        catch (Exception ex) { errors.Add($"Core Audio: {ex.Message}"); }

        try { state.Devices = ReadMediaDevices(); }
        catch (Exception ex) { errors.Add($"WMI: {ex.Message}"); }

        return state;
    }

    private static void ReadEndpoints(AudioState state)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
            state.DefaultMuted = def.AudioEndpointVolume.Mute ? Tri.Yes : Tri.No;
            state.DefaultVolumePercent = (int)Math.Round(def.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        }

        // NotPresent is excluded: Windows keeps every endpoint ever seen, which would flood the list.
        var states = DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled;
        state.Endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, states)
            .Select(d => new AudioEndpointInfo(d.FriendlyName, d.ID == defaultId, d.State == DeviceState.Active, GuessKind(d)))
            .ToList();
    }

    internal static string GuessKind(MMDevice d)
    {
        // FormFactor 9 = DigitalAudioDisplayDevice (HDMI/DisplayPort), 3/5 = headphones/headset.
        int? formFactor = null;
        try { formFactor = Convert.ToInt32(d.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor]?.Value); } catch { }
        if (formFactor == 9) return "Hdmi";
        return GuessKind(d.FriendlyName, formFactor);
    }

    internal static string GuessKind(string name, int? formFactor = null)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("bluetooth") || n.Contains("hands-free") || n.Contains("airpods")) return "Bluetooth";
        if (n.Contains("hdmi") || n.Contains("displayport")) return "Hdmi";
        if (formFactor is 3 or 5 || n.Contains("headphone") || n.Contains("headset")) return "Headphones";
        if (formFactor == 1 || n.Contains("speaker")) return "Speakers";
        return "Other";
    }

    private static List<AudioDeviceInfo> ReadMediaDevices()
    {
        var drivers = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
        using (var q = new ManagementObjectSearcher(
            "SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'MEDIA'"))
            foreach (var d in q.Get())
                if (d["DeviceID"] is string id) drivers[id] = d;

        var result = new List<AudioDeviceInfo>();
        using var devices = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'MEDIA'");
        foreach (var dev in devices.Get())
        {
            var id = dev["DeviceID"] as string ?? "";
            drivers.TryGetValue(id, out var drv);
            result.Add(new AudioDeviceInfo(
                dev["Name"] as string ?? "(unnamed)",
                id,
                dev["ConfigManagerErrorCode"] is uint code ? (int)code : null,
                drv?["DriverVersion"] as string,
                drv?["DriverDate"] is string date ? ManagementDateTimeConverter.ToDateTime(date).ToString("yyyy-MM-dd") : null,
                drv?["DriverProviderName"] as string,
                Drivers.DriverStatus.For(id)));
        }
        return result;
    }
}
