using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace AutoFixScout.Executor;

/// <summary>
/// Elevated executor (spec 5). Accepts only an allowlisted action id plus arguments validated for that action.
/// Never a command string, script, path or URL. Exit codes: 0 ok, 1 action failed, 2 rejected input,
/// and for drivers 3 nothing to do (no older driver / nothing offered), 4 changed but undone, 6 restore failed (see SavePoints).
/// </summary>
public static class Program
{
    internal static readonly HashSet<string> RestartableServices = new(StringComparer.OrdinalIgnoreCase)
        { "AudioSrv", "AudioEndpointBuilder", "WlanSvc" };

    // Sound, camera and network only (spec 3). Display and storage drivers are never touched: a bad one can stop Windows starting.
    private static readonly string[] ToggleableClasses = { "MEDIA", "Camera", "Image", "Net" };

    public static int Main(string[] args)
    {
        try
        {
            return (args.Length, args.ElementAtOrDefault(0)) switch
            {
                (3, "driver.rollback") when IsValidInstanceId(args[1]) && IsValidSavePointId(args[2]) && IsToggleableDevice(args[1])
                    => SavePoints.Rollback(args[1], args[2]),
                (3, "driver.install_from_windows_update") when IsValidInstanceId(args[1]) && IsValidSavePointId(args[2]) && IsToggleableDevice(args[1])
                    => SavePoints.InstallFromWindowsUpdate(args[1], args[2]),
                (2, "driver.restore") when IsValidSavePointId(args[1]) => SavePoints.Restore(args[1]),
                (not 2, _) => 2,
                (_, "service.restart") when RestartableServices.Contains(args[1]) => RestartService(args[1]),
                (_, "device.disable_enable") when IsValidInstanceId(args[1]) && IsToggleableDevice(args[1]) => ToggleDevice(args[1]),
                (_, "device.enable") when IsValidInstanceId(args[1]) && IsToggleableDevice(args[1]) => Pnputil("/enable-device", args[1]) == 0 ? 0 : 1,
                (_, "network.reset_dns") when IsValidInstanceId(args[1]) && IsNetAdapter(args[1]) => ResetDns(args[1]),
                _ => 2,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>Save point ids are app-generated GUIDs ("N" format), never paths.</summary>
    public static bool IsValidSavePointId(string id) => Regex.IsMatch(id, "^[0-9a-f]{32}$");

    // PnP instance ids: letters, digits and & \ _ . # { } -. No quotes or spaces, so they are safe in WQL and as a pnputil argument.
    public static bool IsValidInstanceId(string id) =>
        id.Length is > 0 and < 400 && Regex.IsMatch(id, @"^[A-Za-z0-9\\&_.#{}\-]+$");

    private static bool IsToggleableDevice(string id)
    {
        using var q = new ManagementObjectSearcher($"SELECT PNPClass FROM Win32_PnPEntity WHERE DeviceID = '{id.Replace(@"\", @"\\")}'");
        return q.Get().Cast<ManagementBaseObject>().Any(d => ToggleableClasses.Contains(d["PNPClass"] as string, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsNetAdapter(string id)
    {
        using var q = new ManagementObjectSearcher($"SELECT PNPClass FROM Win32_PnPEntity WHERE DeviceID = '{id.Replace(@"\", @"\\")}'");
        return q.Get().Cast<ManagementBaseObject>().Any(d => string.Equals(d["PNPClass"] as string, "Net", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Hand-typed DNS servers back to automatic (DHCP) on one adapter. The previous servers are in the app's probe state
    /// and helper report, so a helper can put them back. No netsh, no command line: WMI on the adapter's own configuration.
    /// </summary>
    private static int ResetDns(string instanceId)
    {
        using var a = new ManagementObjectSearcher($"SELECT GUID FROM Win32_NetworkAdapter WHERE PNPDeviceID = '{instanceId.Replace(@"\", @"\\")}'");
        var guid = a.Get().Cast<ManagementBaseObject>().Select(x => x["GUID"] as string).FirstOrDefault(g => g != null);
        if (guid is null) return 1;
        using var c = new ManagementObjectSearcher($"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{guid}'");
        foreach (ManagementObject cfg in c.Get())
        {
            var inParams = cfg.GetMethodParameters("SetDNSServerSearchOrder");
            inParams["DNSServerSearchOrder"] = null; // null = obtain DNS servers automatically
            var result = Convert.ToUInt32(cfg.InvokeMethod("SetDNSServerSearchOrder", inParams, null)["ReturnValue"]);
            return result is 0 or 1 ? 0 : 1; // 1 = done, restart suggested
        }
        return 1;
    }

    private static int RestartService(string name)
    {
        using var sc = new ServiceController(name);
        // Stop() also stops dependents (AudioEndpointBuilder -> AudioSrv), so remember which were running and bring them back.
        var restartAfter = sc.DependentServices.Where(d => d.Status == ServiceControllerStatus.Running).Select(d => d.ServiceName).ToList();
        // A stopped AudioSrv is itself a no-sound cause, so the audio restart always brings it up.
        if (name.Equals("AudioEndpointBuilder", StringComparison.OrdinalIgnoreCase)) restartAfter.Add("AudioSrv");
        var timeout = TimeSpan.FromSeconds(30);
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
        foreach (var dep in restartAfter.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var d = new ServiceController(dep);
            if (d.Status != ServiceControllerStatus.Running) { d.Start(); d.WaitForStatus(ServiceControllerStatus.Running, timeout); }
        }
        return 0;
    }

    private static int ToggleDevice(string id)
    {
        var disabled = Pnputil("/disable-device", id);
        // Always try to re-enable, even if disable reported failure: leaving a device off is the harm we must avoid.
        var enabled = Pnputil("/enable-device", id);
        return disabled == 0 && enabled == 0 ? 0 : 1;
    }

    internal static int Pnputil(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
