using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"Games stutter or crash, or the picture shows artifacts": graphics card probe, triage, ladder. Read-only; the person does every fix.</summary>
public static class GraphicsFlow
{
    public const string Topic = "graphics";
    public const string What = "graphics card";
    public const string TestQuestion = "Is the picture and the game running correctly now?";

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        using var _ = Activity.Scope(Topic);
        var s = new RealGraphicsProbe().Read();
        var f = Analysis.Judge(() => GraphicsTriage.Evaluate(s));
        return new Diagnosis(s, f, Analysis.Plan(GraphicsTriage.Ladder(s, f)), GraphicsTriage.ProbablyHardware(s), GraphicsTriage.NotFixedHint(f));
    });
}
