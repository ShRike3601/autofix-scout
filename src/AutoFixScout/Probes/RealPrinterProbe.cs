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
        try
        {
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

    private static List<PrinterInfo> ReadPrinters()
    {
        var list = new List<PrinterInfo>();
        using var q = new ManagementObjectSearcher(
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
        using var q = new ManagementObjectSearcher("SELECT Name, StatusMask, TimeSubmitted FROM Win32_PrintJob");
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
