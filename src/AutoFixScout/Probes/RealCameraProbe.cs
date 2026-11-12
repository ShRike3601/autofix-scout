using System.Management;
using Microsoft.Win32;

namespace AutoFixScout.Probes;

/// <summary>A camera device. ProblemCode 22 means "disabled" (Device Manager code).</summary>
public sealed record CameraDeviceInfo(string Name, string InstanceId, string PnpClass, int? ProblemCode, Drivers.DriverStatus? Driver = null)
{
    public bool Disabled => ProblemCode == 22;
}

/// <summary>probe.devices (Camera/Image class) + probe.camera_privacy (spec 7.1). Tri.Unknown = couldn't read, never "allowed".</summary>
public sealed class CameraState
{
    public IReadOnlyList<CameraDeviceInfo> Devices { get; set; } = new List<CameraDeviceInfo>();
    /// <summary>Settings > Privacy > Camera > "Camera access" (whole device, HKLM).</summary>
    public Tri DeviceAccess { get; set; } = Tri.Unknown;
    /// <summary>"Let apps access your camera" (this user, HKCU).</summary>
    public Tri AppAccess { get; set; } = Tri.Unknown;
    /// <summary>"Let desktop apps access your camera" (Zoom, browsers, Teams classic).</summary>
    public Tri DesktopAppAccess { get; set; } = Tri.Unknown;
    /// <summary>Apps that started using the camera and haven't stopped (spec 11: a heuristic).</summary>
    public IReadOnlyList<string> AppsUsingCamera { get; set; } = new List<string>();
    /// <summary>Friendly names of apps with their own choice set to "Deny" (Settings > Privacy > Camera, per app).</summary>
    public IReadOnlyList<string> DeniedApps { get; set; } = new List<string>();
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface ICameraProbe { CameraState Read(); }

public sealed class RealCameraProbe : ICameraProbe
{
    private const string ConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    public CameraState Read()
    {
        var errors = new List<string>();
        var state = new CameraState { ProbeErrors = errors };
        try { state.Devices = ReadDevices(); }
        catch (Exception ex) { errors.Add($"WMI: {ex.Message}"); }
        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(ConsentKey);
            using var hkcu = Registry.CurrentUser.OpenSubKey(ConsentKey);
            using var desktop = hkcu?.OpenSubKey("NonPackaged");
            state.DeviceAccess = Allowed(hklm);
            state.AppAccess = Allowed(hkcu);
            state.DesktopAppAccess = Allowed(desktop);
            state.DeniedApps = AppConsent.ReadDenied(hkcu);
            state.AppsUsingCamera = InUse(hkcu, packaged: true).Concat(InUse(desktop, packaged: false)).Distinct().ToList();
        }
        catch (Exception ex) { errors.Add($"Consent store: {ex.Message}"); }
        return state;
    }

    private static List<CameraDeviceInfo> ReadDevices()
    {
        // Spec 11: match cameras by PNPClass, not by name text.
        using var q = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
        return q.Get().Cast<ManagementBaseObject>().Select(d => new CameraDeviceInfo(
            d["Name"] as string ?? "(unnamed)",
            d["DeviceID"] as string ?? "",
            d["PNPClass"] as string ?? "",
            d["ConfigManagerErrorCode"] is uint code ? (int)code : null,
            Drivers.DriverStatus.For(d["DeviceID"] as string ?? ""))).ToList();
    }

    private static Tri Allowed(RegistryKey? key) => (key?.GetValue("Value") as string) switch
    {
        "Allow" => Tri.Yes,
        "Deny" => Tri.No,
        _ => Tri.Unknown,
    };

    private static IEnumerable<string> InUse(RegistryKey? parent, bool packaged)
    {
        if (parent is null) yield break;
        foreach (var name in parent.GetSubKeyNames())
        {
            if (name == "NonPackaged") continue;
            using var app = parent.OpenSubKey(name);
            if (app?.GetValue("LastUsedTimeStart") is long start && start > 0 && app.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                yield return FriendlyAppName(name, packaged);
        }
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera"; "C:#Program Files#Zoom#bin#Zoom.exe" -> "Zoom".</summary>
    internal static string FriendlyAppName(string keyName, bool packaged)
    {
        if (packaged)
        {
            var family = keyName.Split('_')[0];
            return family[(family.LastIndexOf('.') + 1)..];
        }
        var file = keyName.Split('#').Last();
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }
}
