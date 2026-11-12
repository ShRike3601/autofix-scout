namespace AutoFixScout.Probes;

/// <summary>
/// One installed printer (Win32_Printer). Status numbers are the WMI ones, null = not reported.
/// ExtendedPrinterStatus: 7 Offline, 8 Paused, 9 Error. DetectedErrorState: 4 No paper, 6 No toner, 7 Door open,
/// 8 Jammed, 9 Offline, 10 Service requested, 11 Output bin full. PrinterState is the spooler's status bitmask (1 = paused).
/// </summary>
public sealed record PrinterInfo(
    string Name,
    bool IsDefault,
    bool IsVirtual,
    bool WorkOffline,
    int? PrinterStatus,
    int? ExtendedPrinterStatus,
    int? DetectedErrorState,
    long? PrinterState = null)
{
    public bool Paused => ExtendedPrinterStatus == 8 || (PrinterState is { } s && (s & 1) != 0);
    /// <summary>Windows reports it cannot reach the printer (not the person's "Use printer offline" choice).</summary>
    public bool ReportedOffline => PrinterStatus == 7 || ExtendedPrinterStatus == 7 || DetectedErrorState == 9;
    /// <summary>A problem at the printer itself that the person has to clear (paper, toner, door, jam, service).</summary>
    public bool HasPhysicalError => DetectedErrorState is 4 or 6 or 7 or 8 or 10 or 11 || ExtendedPrinterStatus == 9;
}

/// <summary>A document in a print queue (Win32_PrintJob). StatusMask bits: 2 Error, 0x20 Offline, 0x40 Paper out, 0x200 Blocked, 0x400 Needs attention.</summary>
public sealed record PrintJobInfo(string PrinterName, int? StatusMask, DateTime? SubmittedUtc)
{
    public const int ErrorBits = 0x2 | 0x20 | 0x40 | 0x200 | 0x400;
    public static readonly TimeSpan OldAfter = TimeSpan.FromMinutes(10);

    public bool IsStuck(DateTime nowUtc) =>
        (StatusMask is { } m && (m & ErrorBits) != 0) || (SubmittedUtc is { } t && nowUtc - t > OldAfter);
}

/// <summary>probe.printer. Tri.Unknown = could not read, never "fine".</summary>
public sealed class PrinterState
{
    /// <summary>Print Spooler service (Spooler) running.</summary>
    public Tri SpoolerRunning { get; set; } = Tri.Unknown;
    public IReadOnlyList<PrinterInfo> Printers { get; set; } = new List<PrinterInfo>();
    public IReadOnlyList<PrintJobInfo> Jobs { get; set; } = new List<PrintJobInfo>();
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IPrinterProbe { PrinterState Read(); }

public static class PrinterRules
{
    private static readonly string[] VirtualNames = { "print to pdf", "xps document writer", "onenote", "fax", "pdf" };
    private static readonly string[] VirtualPorts = { "PORTPROMPT:", "SHRFAX:", "nul:", "FILE:" };

    /// <summary>Printers that make a file or a fax instead of paper (Microsoft Print to PDF, OneNote, Fax, XPS).</summary>
    public static bool IsVirtual(string name, string? port) =>
        VirtualNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))
        || (port != null && (VirtualPorts.Any(p => port.Equals(p, StringComparison.OrdinalIgnoreCase))
                             || port.StartsWith("Microsoft.Office.OneNote", StringComparison.OrdinalIgnoreCase)));
}
