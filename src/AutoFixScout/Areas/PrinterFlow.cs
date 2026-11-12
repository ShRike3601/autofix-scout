using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"My printer does not print": probe, triage, ladder. The real test is a question to the person.</summary>
public static class PrinterFlow
{
    public const string Topic = "printer";
    public const string What = "printer";
    public const string TestQuestion = "Did your document print?";

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        var s = new RealPrinterProbe().Read();
        var f = PrinterTriage.Evaluate(s);
        return new Diagnosis(s, f, PrinterTriage.Ladder(s, f), PrinterTriage.ProbablyHardware(s), PrinterTriage.NotFixedHint(f));
    });
}
