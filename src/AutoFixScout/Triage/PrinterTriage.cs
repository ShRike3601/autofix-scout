namespace AutoFixScout.Triage;

/// <summary>
/// "My printer does not print". Pure local logic, T0/T1 only: no driver changes.
/// shortcut: ladder is hardcoded like the others; move all of them to YAML playbooks together.
/// </summary>
public static class PrinterTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private const string Incomplete = "AutoFix Scout could not complete the printer check.";

    private const string CheckPrinterHint =
        "Please check that the printer is turned on, that its cable or Wi-Fi connection is in place, and that it has paper. " +
        "If the printer shows a message or a blinking light, please follow the instructions on the printer.";

    /// <summary>The printer the person most likely means: the default one if it is a real printer, else a real one with a problem.</summary>
    public static PrinterInfo? Target(PrinterState s)
    {
        var real = s.Printers.Where(p => !p.IsVirtual).ToList();
        return real.FirstOrDefault(p => p.IsDefault)
            ?? real.FirstOrDefault(p => p.WorkOffline || p.Paused || p.ReportedOffline || p.HasPhysicalError || StuckJobs(s, p).Count > 0)
            ?? real.FirstOrDefault();
    }

    private static List<PrintJobInfo> StuckJobs(PrinterState s, PrinterInfo p) =>
        s.Jobs.Where(j => string.Equals(j.PrinterName, p.Name, StringComparison.OrdinalIgnoreCase) && j.IsStuck(DateTime.UtcNow)).ToList();

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(PrinterState s)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)) };
        if (s.SpoolerRunning == Tri.No)
            return new[] { F(FindingCode.PrintSpoolerStopped, "The Windows printing service (\"Print Spooler\") is not running.", "Spooler stopped") };
        if (s.SpoolerRunning != Tri.Yes)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, $"spooler={s.SpoolerRunning}") };

        var target = Target(s);
        if (target == null)
            return new[] { F(FindingCode.NoPrinter, "No printer is set up on this computer.",
                $"Only virtual printers: {string.Join(", ", s.Printers.Select(p => p.Name))}") };

        var findings = new List<Finding>();
        var def = s.Printers.FirstOrDefault(p => p.IsDefault);
        if (def == null || def.IsVirtual)
            findings.Add(F(FindingCode.DefaultPrinterVirtual,
                def == null
                    ? $"No printer is chosen as the default printer. Documents may not be sent to \"{target.Name}\"."
                    : $"Documents are sent to \"{def.Name}\", which saves a file instead of printing on paper.",
                $"Default: {def?.Name ?? "none"}; real printer: {target.Name}"));
        if (target.WorkOffline || target.ReportedOffline)
            findings.Add(F(FindingCode.PrinterOffline,
                target.WorkOffline
                    ? $"The printer \"{target.Name}\" is set to \"Use printer offline\"."
                    : $"Windows cannot reach the printer \"{target.Name}\" at the moment.",
                $"WorkOffline={target.WorkOffline} PrinterStatus={target.PrinterStatus} Extended={target.ExtendedPrinterStatus} Detected={target.DetectedErrorState}"));
        if (target.Paused)
            findings.Add(F(FindingCode.PrinterPaused, $"Printing is paused for the printer \"{target.Name}\".",
                $"Extended={target.ExtendedPrinterStatus} PrinterState={target.PrinterState}"));
        if (target.HasPhysicalError)
            findings.Add(F(FindingCode.PrinterError, $"The printer \"{target.Name}\" reports a problem: {ErrorText(target)}.",
                $"Detected={target.DetectedErrorState} Extended={target.ExtendedPrinterStatus}"));
        var stuck = StuckJobs(s, target);
        if (stuck.Count > 0)
            findings.Add(F(FindingCode.PrintJobsStuck,
                stuck.Count == 1
                    ? $"One document is stuck in the queue of the printer \"{target.Name}\"."
                    : $"{stuck.Count} documents are stuck in the queue of the printer \"{target.Name}\".",
                string.Join("; ", stuck.Select(j => $"status=0x{j.StatusMask ?? 0:x} submitted={j.SubmittedUtc:u}"))));
        if (findings.Count == 0)
            findings.Add(F(FindingCode.LooksNormal, $"Windows reports no problem with the printer \"{target.Name}\".",
                $"Default={def?.Name}; no error, not offline, not paused, no stuck documents"));
        return findings;
    }

    private static string ErrorText(PrinterInfo p) => p.DetectedErrorState switch
    {
        4 => "it is out of paper",
        6 => "it is out of ink or toner",
        7 => "a door or cover is open",
        8 => "the paper is jammed",
        10 => "it needs service",
        11 => "the output tray is full",
        _ => "an error at the printer",
    };

    /// <summary>Least risky first. Every rung is T0 or T1.</summary>
    public static IReadOnlyList<Rung> Ladder(PrinterState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(f => f.Code).ToHashSet();
        var rungs = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return rungs; // fail closed: do not change what could not be read

        if (codes.Contains(FindingCode.PrintSpoolerStopped))
        {
            rungs.Add(RestartSpooler());
            rungs.Add(new("check_printer", "ui.human_step", 0, CheckPrinterHint));
            return rungs;
        }
        if (codes.Contains(FindingCode.NoPrinter))
        {
            rungs.Add(new("check_printer", "ui.human_step", 0, CheckPrinterHint));
            rungs.Add(new("add_printer", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Printers & scanners\" settings. Please select \"Add device\" and choose your printer.", "printers"));
            return rungs;
        }

        var target = Target(s)!;
        if (codes.Contains(FindingCode.DefaultPrinterVirtual))
            rungs.Add(new("choose_default", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Printers & scanners\" settings. " +
                $"Please select \"{target.Name}\" and then \"Set as default\".", "printers"));
        if (codes.Contains(FindingCode.PrinterError))
            rungs.Add(new("printer_error", "ui.human_step", 0,
                $"The printer \"{target.Name}\" reports that {ErrorText(target)}. Please clear this at the printer, then return to AutoFix Scout."));
        if (target.WorkOffline || target.Paused)
            rungs.Add(new("set_online", "printer.set_online", 1,
                $"AutoFix Scout can set the printer \"{target.Name}\" back to online and resume printing. " +
                "You can change this again at any time. Windows will ask for your permission.", target.Name));
        // Windows cannot reach it: the printer itself, its cable or its Wi-Fi is the most likely cause.
        if (target.ReportedOffline && !target.WorkOffline)
            rungs.Add(new("check_printer", "ui.human_step", 0, CheckPrinterHint));
        if (codes.Contains(FindingCode.PrintJobsStuck))
            rungs.Add(new("clear_queue", "printer.clear_queue", 1,
                $"AutoFix Scout can remove the documents waiting for the printer \"{target.Name}\". " +
                "This cannot be undone, so you will need to print them again. Windows will ask for your permission.", target.Name));
        rungs.Add(RestartSpooler());
        rungs.Add(new("check_printer", "ui.human_step", 0, CheckPrinterHint));
        return rungs.DistinctBy(r => r.Id).ToList();
    }

    private static Rung RestartSpooler() =>
        new("restart_spooler", "service.restart", 1,
            "AutoFix Scout can restart the Windows printing service (\"Print Spooler\"). Windows will ask for your permission.", "Spooler");

    /// <summary>The printer itself reports a fault that only it can clear (jam, open door, service), or cannot be reached.</summary>
    public static bool ProbablyHardware(PrinterState s) =>
        s.ProbeErrors.Count == 0 && s.SpoolerRunning == Tri.Yes && Target(s) is { } t
        && (t.DetectedErrorState is 7 or 8 or 10 || t.ExtendedPrinterStatus == 9 || (t.ReportedOffline && !t.WorkOffline));

    /// <summary>The honest "not fixed" sentence: say when the cause is most likely the printer, not this computer.</summary>
    public static string? NotFixedHint(IReadOnlyList<Finding> after) => after[0].Code switch
    {
        FindingCode.NoPrinter => "No printer is set up on this computer. A helper can set up your printer for you.",
        FindingCode.PrinterError => "The printer itself reports a problem. Its display or its manual usually explains what to do. " +
            "If the problem remains, the printer may need service.",
        FindingCode.PrinterOffline => "Windows still cannot reach the printer. The problem most likely lies with the printer, its cable " +
            "or its Wi-Fi connection. A helper can check the printer for you.",
        FindingCode.LooksNormal => "Windows reports no problem with the printer. If a document still does not print, please print it " +
            "again from the app. If that does not help, a helper can check the printer for you.",
        _ => null,
    };
}
