namespace AutoFixScout.Repair;

/// <summary>
/// Windows 10/11 default start types for services that many everyday problems depend on. Shared with the elevated
/// executor (linked file), which only restores a service named here, and only to the value named here.
/// </summary>
internal static class ServiceDefaults
{
    public enum Start { Automatic, Manual }

    public sealed record Default(string Name, string DisplayName, Start Start, bool ShouldRun, string Area, bool Delayed = false);

    public static readonly IReadOnlyList<Default> All = new Default[]
    {
        new("Dhcp", "DHCP Client", Start.Automatic, true, "internet"),
        new("Dnscache", "DNS Client", Start.Automatic, true, "internet"),
        new("nsi", "Network Store Interface Service", Start.Automatic, true, "internet"),
        new("NlaSvc", "Network Location Awareness", Start.Manual, false, "internet"),
        new("Wcmsvc", "Windows Connection Manager", Start.Automatic, true, "internet"),
        new("BFE", "Base Filtering Engine", Start.Automatic, true, "internet"),
        new("mpssvc", "Windows Defender Firewall", Start.Automatic, true, "security"),
        new("LanmanWorkstation", "Workstation", Start.Automatic, true, "network files"),
        new("EventLog", "Windows Event Log", Start.Automatic, true, "system"),
        new("Winmgmt", "Windows Management Instrumentation", Start.Automatic, true, "system"),
        new("CryptSvc", "Cryptographic Services", Start.Automatic, true, "updates"),
        new("ProfSvc", "User Profile Service", Start.Automatic, true, "system"),
        new("SENS", "System Event Notification Service", Start.Automatic, true, "system"),
        new("Themes", "Themes", Start.Automatic, true, "appearance"),
        new("FontCache", "Windows Font Cache Service", Start.Automatic, true, "appearance"),
        new("ShellHWDetection", "Shell Hardware Detection", Start.Automatic, true, "devices"),
        new("AudioSrv", "Windows Audio", Start.Automatic, true, "sound"),
        new("AudioEndpointBuilder", "Windows Audio Endpoint Builder", Start.Automatic, true, "sound"),
        new("WSearch", "Windows Search", Start.Automatic, true, "search", Delayed: true),
        new("Spooler", "Print Spooler", Start.Automatic, true, "printing"),
        new("W32Time", "Windows Time", Start.Manual, false, "clock"),
        new("wuauserv", "Windows Update", Start.Manual, false, "updates"),
        new("BITS", "Background Intelligent Transfer Service", Start.Manual, false, "updates"),
        new("TrustedInstaller", "Windows Modules Installer", Start.Manual, false, "updates"),
        new("AppXSvc", "AppX Deployment Service (AppXSVC)", Start.Manual, false, "apps"),
        new("ClipSVC", "Client License Service (ClipSVC)", Start.Manual, false, "apps"),
        new("InstallService", "Microsoft Store Install Service", Start.Manual, false, "apps"),
        new("WlanSvc", "WLAN AutoConfig", Start.Automatic, true, "internet"),
        new("bthserv", "Bluetooth Support Service", Start.Manual, false, "bluetooth"),
    };

    public static Default? Find(string name) =>
        All.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A fault is a service switched off (Disabled), or an automatic service that should be running but is stopped.
    /// Services that do not exist on this computer (for example no Wi-Fi) are never faults.
    /// </summary>
    public static bool IsFault(string name, string status, string startType)
    {
        if (Find(name) is not { } d || status is "NotFound" or "Unknown") return false;
        if (startType.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return true;
        return d.ShouldRun && d.Start == Start.Automatic && status == "Stopped";
    }
}
