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
        { "AudioSrv", "AudioEndpointBuilder", "WlanSvc", "wuauserv", "BITS", "CryptSvc", "Spooler", "bthserv" };

    /// <summary>Windows Update services a person may have switched off; service.set_manual puts them back to Manual (Windows' default).</summary>
    public static readonly HashSet<string> ManualStartServices = new(StringComparer.OrdinalIgnoreCase) { "wuauserv", "BITS", "CryptSvc" };

    /// <summary>Same as Triage.Rescan.Areas (a test keeps them equal).</summary>
    public static readonly string[] RescanAreas = { "audio", "camera", "network", "bluetooth" };

    // Sound, camera, network and Bluetooth adapters only (spec 3). Display and storage drivers are never touched: a bad one can stop Windows starting.
    private static readonly string[] ToggleableClasses = { "MEDIA", "Camera", "Image", "Net", "Bluetooth" };

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
                // The area is only recorded; the scan itself takes no input.
                (_, "device.rescan") when RescanAreas.Contains(args[1]) => ScanForHardwareChanges(),
                (_, "service.set_manual") when ManualStartServices.Contains(args[1]) => SetManualAndStart(args[1]),
                // Printer names are checked against the installed printers, never put into a query or command line.
                (_, "printer.clear_queue") when IsValidPrinterName(args[1]) && FindPrinter(args[1]) is { } p => ClearQueue(p),
                (_, "printer.set_online") when IsValidPrinterName(args[1]) && FindPrinter(args[1]) is { } p => SetOnline(p),
                (_, "repair.dism") when args[1] == Repairs.System => Repairs.Dism(),
                (_, "repair.sfc") when args[1] == Repairs.System => Repairs.Sfc(),
                (_, "time.resync") when args[1] == Repairs.System => Repairs.TimeResync(),
                (_, "network.reset_winhttp_proxy") when args[1] == Repairs.System => Repairs.ResetWinHttpProxy(),
                (_, "network.clean_hosts") when args[1] == Repairs.System => Repairs.CleanHosts(),
                (_, "network.reset_winsock") when args[1] == Repairs.System => Repairs.ResetWinsock(),
                (_, "system.repair_path") when args[1] == Repairs.System => Repairs.RepairPath(),
                (_, "update.reset_components") when args[1] == Repairs.System => Repairs.ResetUpdateComponents(),
                (_, "search.rebuild_index") when args[1] == Repairs.System => Repairs.RebuildSearchIndex(),
                (_, "service.restore_default") when AutoFixScout.Repair.ServiceDefaults.Find(args[1]) is not null => Repairs.RestoreServiceDefault(args[1]),
                (_, "runtime.install_vcredist") when Repairs.RuntimeArchitectures.Contains(args[1]) => Repairs.InstallVcRedist(args[1]),
                (_, "disk.scan") when Repairs.IsSystemDrive(args[1]) => Repairs.DiskScan(args[1]),
                (_, "storage.cleanup_components") when args[1] == Repairs.System => Repairs.CleanupComponents(),
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

    /// <summary>
    /// Shape check before the installed-printer lookup: printable, no quotes, commas (Windows forbids them in printer
    /// names) or shell characters, at most 256 characters.
    /// </summary>
    public static bool IsValidPrinterName(string name) =>
        name.Length is > 0 and <= 256 && name.Trim().Length > 0
        && !name.Any(c => char.IsControl(c) || c is '"' or '\'' or ',' or ';' or '|' or '<' or '>' or '*' or '?');

    /// <summary>The installed printer with exactly this name (case-insensitive), or null. Enumerates; no name in WQL.</summary>
    private static ManagementObject? FindPrinter(string name)
    {
        using var q = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
        return q.Get().Cast<ManagementObject>().FirstOrDefault(p => string.Equals(p["Name"] as string, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>printer.clear_queue: cancels every document waiting for this one printer (Win32_Printer.CancelAllJobs).</summary>
    private static int ClearQueue(ManagementObject printer)
    {
        using (printer)
            return Convert.ToUInt32(printer.InvokeMethod("CancelAllJobs", null)) == 0 ? 0 : 1;
    }

    /// <summary>printer.set_online: clears "Use printer offline" and resumes a paused printer. Nothing else is changed.</summary>
    private static int SetOnline(ManagementObject printer)
    {
        using (printer)
        {
            if (printer["WorkOffline"] is true)
            {
                printer["WorkOffline"] = false;
                printer.Put();
            }
            var paused = printer["ExtendedPrinterStatus"] is ushort ext && ext == 8
                || (printer["PrinterState"] is { } st && (Convert.ToInt64(st) & 1) != 0);
            if (paused && Convert.ToUInt32(printer.InvokeMethod("Resume", null)) != 0) return 1;
            return 0;
        }
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

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern int CM_Reenumerate_DevNode(uint devInst, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern uint CMP_WaitNoPendingInstallEvents(uint timeoutMs);

    /// <summary>
    /// Device Manager's "Scan for hardware changes": re-enumerate from the root so devices that were uninstalled (driver
    /// kept) come back, then wait for Windows to finish installing them. Works on every Windows 10/11 build, unlike
    /// pnputil /scan-devices (2004+). Adds devices only; never removes or changes one.
    /// </summary>
    private static int ScanForHardwareChanges()
    {
        if (CM_Locate_DevNodeW(out var root, null, 0) != 0) return 1;
        if (CM_Reenumerate_DevNode(root, 0x1 /* CM_REENUMERATE_SYNCHRONOUS */) != 0) return 1;
        CMP_WaitNoPendingInstallEvents(60_000); // WAIT_TIMEOUT just means "still busy"; the real test decides
        Thread.Sleep(2000); // audio endpoints / camera stacks appear a moment after the device starts
        return 0;
    }

    /// <summary>Start type back to Manual (Windows' default for these), then start it. Name is allowlisted by the caller.</summary>
    private static int SetManualAndStart(string service)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "config", service, "start=", "demand" }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) { p.WaitForExit(); if (p.ExitCode != 0) return 1; }
        return RestartService(service);
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
