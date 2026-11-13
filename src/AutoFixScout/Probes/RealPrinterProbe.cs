using System.Management;
using System.ServiceProcess;

namespace AutoFixScout.Probes;

/// <summary>
/// probe.printer: Print Spooler service, installed printers and their status (Win32_Printer), and the print queues
/// (Win32_PrintJob). Read-only, no admin, never throws.
/// </summary>
public sealed class RealPrinterProbe : IPrinterProbe
{
    public PrinterState Read()
    {
        var errors = new List<string>();
        var state = new PrinterState { ProbeErrors = errors };
        ReadScanners(state);
        try
        {
            Activity.Report(ActivityKind.Command, "Reading the state of the Print Spooler service (Spooler)");
            using var sc = new ServiceController("Spooler");

            state.SpoolerRunning = sc.Status == ServiceControllerStatus.Running ? Tri.Yes : Tri.No;
        }
        catch (Exception ex) { errors.Add($"Spooler service: {ex.Message}"); return state; }
        // With the spooler stopped Windows cannot list printers, so there is nothing more to read.
        if (state.SpoolerRunning != Tri.Yes) return state;

        try { state.Printers = ReadPrinters(); }
        catch (Exception ex) { errors.Add($"Win32_Printer: {ex.Message}"); return state; }
        try { state.Jobs = ReadJobs(); }
        catch (Exception ex) { errors.Add($"Win32_PrintJob: {ex.Message}"); }
        return state;
    }

    /// <summary>Scanner part: WIA and Shell Hardware Detection services, and scanner devices. Errors stay in ScannerErrors.</summary>
    private static void ReadScanners(PrinterState state)
    {
        var errors = new List<string>();
        state.ScannerErrors = errors;
        try { state.WiaService = ReadService("stisvc"); state.ShellHardwareService = ReadService("ShellHWDetection"); }
        catch (Exception ex) { errors.Add($"services: {ex.Message}"); }
        if (state.WiaService?.Status == "Unknown") errors.Add("stisvc: could not be read");
        if (state.ShellHardwareService?.Status == "Unknown") errors.Add("ShellHWDetection: could not be read");
        try { state.Scanners = ReadScannerDevices(); }
        catch (Exception ex) { errors.Add($"Win32_PnPEntity: {ex.Message}"); }
    }

    private static UpdateServiceInfo ReadService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return new UpdateServiceInfo(name, sc.Status == ServiceControllerStatus.Running ? "Running" : sc.Status == ServiceControllerStatus.Stopped ? "Stopped" : sc.Status.ToString(), sc.StartType.ToString());
        }
        catch (InvalidOperationException) { return new UpdateServiceInfo(name, "NotFound", "NotFound"); }
        catch (Exception) { return new UpdateServiceInfo(name, "Unknown", "Unknown"); }
    }

    /// <summary>Still-image devices that are not cameras: webcams use the usbvideo driver or have "camera" or "webcam" in the name.</summary>
    private static List<ScannerDevice> ReadScannerDevices()
    {
        var list = new List<ScannerDevice>();
        using var q = Wmi.Searcher(
            "SELECT Name, DeviceID, PNPClass, Service, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Image' OR PNPClass = 'WIA' OR PNPClass = 'ScannerDevice'");
        foreach (var d in q.Get())
        {
            var name = d["Name"] as string ?? "";
            var id = d["DeviceID"] as string ?? "";
            if (name.Length == 0 || PrinterRules.IsCamera(name, d["Service"] as string)) continue;
            list.Add(new ScannerDevice(name, id, d["ConfigManagerErrorCode"] is null ? 0 : Convert.ToInt32(d["ConfigManagerErrorCode"])));
        }
        return list;
    }

    private static List<PrinterInfo> ReadPrinters()
    {
        var list = new List<PrinterInfo>();
        using var q = Wmi.Searcher(
            "SELECT Name, Default, WorkOffline, PrinterStatus, ExtendedPrinterStatus, DetectedErrorState, PrinterState, PortName FROM Win32_Printer");
        foreach (var p in q.Get())
        {
            var name = p["Name"] as string ?? "";
            if (name.Length == 0) continue;
            list.Add(new PrinterInfo(
                name,
                IsDefault: p["Default"] is true,
                IsVirtual: PrinterRules.IsVirtual(name, p["PortName"] as string),
                WorkOffline: p["WorkOffline"] is true,
                PrinterStatus: ToInt(p["PrinterStatus"]),
                ExtendedPrinterStatus: ToInt(p["ExtendedPrinterStatus"]),
                DetectedErrorState: ToInt(p["DetectedErrorState"]),
                PrinterState: p["PrinterState"] is null ? null : Convert.ToInt64(p["PrinterState"])));
        }
        return list;
    }

    private static List<PrintJobInfo> ReadJobs()
    {
        var list = new List<PrintJobInfo>();
        using var q = Wmi.Searcher("SELECT Name, StatusMask, TimeSubmitted FROM Win32_PrintJob");
        foreach (var j in q.Get())
        {
            // Name is "<printer name>, <job id>"; printer names cannot contain commas.
            var name = j["Name"] as string ?? "";
            var comma = name.LastIndexOf(", ", StringComparison.Ordinal);
            if (comma <= 0) continue;
            DateTime? submitted = null;
            if (j["TimeSubmitted"] is string t && t.Length > 0)
                try { submitted = ManagementDateTimeConverter.ToDateTime(t).ToUniversalTime(); } catch (Exception) { }
            list.Add(new PrintJobInfo(name[..comma], ToInt(j["StatusMask"]), submitted));
        }
        return list;
    }

    private static int? ToInt(object? v) => v is null ? null : Convert.ToInt32(v);
}
