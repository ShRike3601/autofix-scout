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
        var s = new RealGraphicsProbe().Read();
        var f = GraphicsTriage.Evaluate(s);
        return new Diagnosis(s, f, GraphicsTriage.Ladder(s, f), GraphicsTriage.ProbablyHardware(s), GraphicsTriage.NotFixedHint(f));
    });
}
