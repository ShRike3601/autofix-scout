using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>"Camera is not working": camera devices and the privacy switches. Read-only; the person confirms every fix.</summary>
public static class CameraFlow
{
    public const string Topic = "camera";
    public const string What = "camera";

    public static Task<Diagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        using var _ = Activity.Scope(Topic);
        var s = new RealCameraProbe().Read();
        var f = Analysis.Judge(() => CameraTriage.Evaluate(s));
        return new Diagnosis(s, f, Analysis.Plan(CameraTriage.Ladder(s, f)), CameraTriage.ProbablyHardware(s));
    });
}
