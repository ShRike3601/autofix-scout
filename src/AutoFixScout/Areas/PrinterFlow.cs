using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"My printer does not print": probe, triage, ladder. The real test is a question to the person.</summary>
public static class PrinterFlow
{
    public const string Topic = "printer";
    public const string What = "printer";
    public const string TestQuestion = "Did your document print?";
    public const string ScanTestQuestion = "Can you scan a page now?";

    /// <param name="scanFocus">The person reported a scanning problem: scanner findings come first.</param>
    public static Task<Diagnosis> DiagnoseAsync(bool scanFocus = false) => Task.Run(() =>
    {
        using var _ = Activity.Scope(Topic);
        var s = new RealPrinterProbe().Read();
        s.ScanFocus = scanFocus;
        var f = Analysis.Judge(() => PrinterTriage.Evaluate(s));
        return new Diagnosis(s, f, Analysis.Plan(PrinterTriage.Ladder(s, f)), PrinterTriage.ProbablyHardware(s), PrinterTriage.NotFixedHint(f));
    });
}
