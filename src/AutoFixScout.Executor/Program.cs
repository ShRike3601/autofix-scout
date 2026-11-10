using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace AutoFixScout.Executor;

/// <summary>
/// Elevated executor (spec 5). Accepts only an allowlisted action id plus one argument validated for that action.
/// Never a command string, script, path or URL. Exit codes: 0 ok, 1 action failed, 2 rejected input.
/// </summary>
public static class Program
{
    internal static readonly HashSet<string> RestartableServices = new(StringComparer.OrdinalIgnoreCase)
        { "AudioSrv", "AudioEndpointBuilder", "WlanSvc" };

    private static readonly string[] ToggleableClasses = { "MEDIA", "Camera", "Image", "Net" };

    public static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        try
        {
            return args[0] switch
            {
                "service.restart" when RestartableServices.Contains(args[1]) => RestartService(args[1]),
                "device.disable_enable" when IsValidInstanceId(args[1]) && IsToggleableDevice(args[1]) => ToggleDevice(args[1]),
                _ => 2,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // PnP instance ids: letters, digits and & \ _ . # { } -. No quotes or spaces, so they are safe in WQL and as a pnputil argument.
    internal static bool IsValidInstanceId(string id) =>
        id.Length is > 0 and < 400 && Regex.IsMatch(id, @"^[A-Za-z0-9\\&_.#{}\-]+$");

    private static bool IsToggleableDevice(string id)
    {
        using var q = new ManagementObjectSearcher($"SELECT PNPClass FROM Win32_PnPEntity WHERE DeviceID = '{id.Replace(@"\", @"\\")}'");
        return q.Get().Cast<ManagementBaseObject>().Any(d => ToggleableClasses.Contains(d["PNPClass"] as string, StringComparer.OrdinalIgnoreCase));
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

    private static int Pnputil(string verb, string id)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe")) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(id);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
