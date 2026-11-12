using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"My screen or monitor does not show a picture": probe, triage, ladder. The real test is a question to the person.</summary>
public static class DisplayFlow
{
    public const string Topic = "display";
    public const string What = "screen";
    public const string TestQuestion = "Is the picture showing correctly on your screen now?";

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        var s = new RealDisplayProbe().Read();
        var f = DisplayTriage.Evaluate(s);
        return new Diagnosis(s, f, DisplayTriage.Ladder(s, f), DisplayTriage.ProbablyHardware(s), DisplayTriage.NotFixedHint(f));
    });
}
