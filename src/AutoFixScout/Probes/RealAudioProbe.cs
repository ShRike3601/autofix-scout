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

        // Devices first, so each output can be tied to the device it belongs to.
        try { state.Devices = ReadMediaDevices(); }
        catch (Exception ex) { errors.Add($"WMI: {ex.Message}"); }

        try { ReadEndpoints(state); }
        catch (Exception ex) { errors.Add($"Core Audio: {ex.Message}"); }

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
            state.MultimediaDeviceId = def.ID;
            state.MultimediaDeviceName = def.FriendlyName;
            state.DefaultMuted = def.AudioEndpointVolume.Mute ? Tri.Yes : Tri.No;
            state.DefaultVolumePercent = (int)Math.Round(def.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            try { state.Sessions = AppSound.ReadSessions(def); }
            catch (Exception ex) { state.SessionError = ex.Message; }
        }

        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Communications))
        {
            using var comms = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
            state.CommsDeviceId = comms.ID;
            state.CommsDeviceName = comms.FriendlyName;
        }
        try { state.Ducking = CallSound.Read(); } catch (Exception) { state.Ducking = null; }

        // NotPresent is excluded: Windows keeps every endpoint ever seen, which would flood the list.
        var states = DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled;
        state.Endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, states)
            .Select(d => new AudioEndpointInfo(d.FriendlyName, d.ID == defaultId, d.State == DeviceState.Active, GuessKind(d),
                OwningDevice(d, state.Devices)))
            .ToList();
    }

    /// <summary>
    /// The sound device an output belongs to, so a driver rollback targets the device the person is listening on
    /// (test step 5 offered the HDMI audio driver for a laptop-speaker problem). The output's own device node is a
    /// child of that device; the nearest ancestor that is one of the MEDIA devices wins. Null = couldn't tell.
    /// </summary>
    internal static string? OwningDevice(MMDevice endpoint, IReadOnlyList<AudioDeviceInfo> devices)
    {
        try
        {
            // The endpoint's property store often lacks PKEY_Device_InstanceId (AFS-TESTBENCH: missing for every output),
            // but its PnP node is always SWD\MMDEVAPI\<endpoint id>.
            var id = endpoint.Properties.Contains(EndpointInstanceId) && endpoint.Properties[EndpointInstanceId]?.Value is string s
                ? s : EndpointNodeId(endpoint.ID);
            return MatchDevice(Ancestors(id), devices.Select(d => d.InstanceId));
        }
        catch (Exception) { return null; }
    }

    internal static string EndpointNodeId(string endpointId) => $@"SWD\MMDEVAPI\{endpointId}";

    internal static string? MatchDevice(IEnumerable<string> ancestors, IEnumerable<string> devices)
    {
        var known = devices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ancestors.FirstOrDefault(known.Contains) is { } match ? known.First(k => k.Equals(match, StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>PKEY_Device_InstanceId of the endpoint ("SWD\MMDEVAPI\{0.0.0.00000000}.{guid}").</summary>
    private static readonly PropertyKey EndpointInstanceId = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    private static IEnumerable<string> Ancestors(string instanceId)
    {
        // CM_LOCATE_DEVNODE_PHANTOM: unplugged outputs (headphone jack, BT speaker off) have non-present nodes.
        if (CM_Locate_DevNodeW(out var node, instanceId, 1) != 0) yield break;
        var buffer = new char[400];
        for (var i = 0; i < 4 && CM_Get_Parent(out node, node, 0) == 0; i++)
        {
            if (CM_Get_Device_IDW(node, buffer, buffer.Length, 0) != 0) yield break;
            yield return new string(buffer, 0, Array.IndexOf(buffer, '\0') is var n and >= 0 ? n : buffer.Length);
        }
    }

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);

    internal static string GuessKind(MMDevice d)
    {
        // FormFactor 9 = DigitalAudioDisplayDevice (HDMI/DisplayPort), 3/5 = headphones/headset.
        int? formFactor = null;
        try { formFactor = Convert.ToInt32(d.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor]?.Value); } catch { }
        if (formFactor == 9) return "Hdmi";
        string? bus = null;
        try { bus = d.Properties[EnumeratorName]?.Value as string; } catch { }
        return GuessKind(d.FriendlyName, formFactor, bus);
    }

    /// <summary>PKEY_Device_EnumeratorName: the bus the endpoint's device is on ("BTHENUM", "HDAUDIO", "USB", ...).</summary>
    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    internal static string GuessKind(string name, int? formFactor = null, string? bus = null)
    {
        // Bus first: Bluetooth speakers are often named just "Speakers (JBL Go 3)" (found in test step 2d).
        if (bus?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true) return "Bluetooth";
        var n = name.ToLowerInvariant();
        if (n.Contains("bluetooth") || n.Contains("hands-free") || n.Contains("airpods")) return "Bluetooth";
        if (n.Contains("hdmi") || n.Contains("displayport")) return "Hdmi";
        if (formFactor is 3 or 5 || n.Contains("headphone") || n.Contains("headset")) return "Headphones";
        if (formFactor == 1 || n.Contains("speaker")) return "Speakers";
        return "Other";
    }

    internal static List<AudioDeviceInfo> ReadMediaDevices()
    {
        // The two WMI queries do not depend on each other (Win32_PnPSignedDriver is the slow one), so they run side by side.
        var driversTask = Task.Run(() =>
        {
            var map = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
            using var q = new ManagementObjectSearcher(
                "SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'MEDIA'");
            foreach (var d in q.Get())
                if (d["DeviceID"] is string id) map[id] = d;
            return map;
        });

        var found = new List<ManagementBaseObject>();
        using (var devices = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'MEDIA'"))
            foreach (var dev in devices.Get()) found.Add(dev);
        var drivers = driversTask.GetAwaiter().GetResult();

        var result = new List<AudioDeviceInfo>();
        foreach (var dev in found)
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
