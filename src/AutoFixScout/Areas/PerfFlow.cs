using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"My computer got slow or games stutter": probe, triage, ladder. The real test is a question to the person.</summary>
public static class PerfFlow
{
    public const string Topic = "performance";
    public const string What = "computer speed";
    public const string TestQuestion = "Does the computer run faster now?";

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        var s = new RealPerfProbe().Read();
        var f = PerfTriage.Evaluate(s);
        return new Diagnosis(s, f, PerfTriage.Ladder(s, f), PerfTriage.ProbablyHardware(s), PerfTriage.NotFixedHint(f));
    });
}
