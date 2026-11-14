using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"Sound is not working": sound outputs, services and app sound settings. Read-only; the person confirms every fix.</summary>
public static class AudioFlow
{
    public const string Topic = "sound";
    public const string What = "sound";

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        using var _ = Activity.Scope(Topic);
        var s = new RealAudioProbe().Read();
        var f = Analysis.Judge(() => AudioTriage.Evaluate(s));
        return new Diagnosis(s, f, Analysis.Plan(AudioLadder.For(s, f)), AudioLadder.ProbablyHardware(s));
    });
}
