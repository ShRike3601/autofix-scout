using AutoFixScout.Stress;
using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>
/// "The computer feels slow under load": an opt-in short load test (about 2 minutes) that shows whether the processor or
/// the graphics card slows down, and when. Needs the person's consent first (see Consent). Changes no setting.
/// </summary>
public static class StressFlow
{
    public const string Topic = "stress";
    public const string What = "short load test";
    public const string TestQuestion = "Does the computer keep its speed under load now?";

    /// <summary>Shown on the consent screen before anything starts.</summary>
    public const string Consent =
        "AutoFix Scout will put the computer under heavy load for about 2 minutes and measure whether the processor or the graphics card slows down. " +
        "The fans will become loud, and the computer may feel slow during the test. " +
        "Please plug in the charger, and close games and large programs first. " +
        "The test stops by itself if anything gets too hot, and you can stop it at any time. " +
        "The test does not change any setting.";

    public static readonly string[] ConsentBullets =
    {
        "It takes about 2 minutes.",
        "The fans will become loud.",
        "The computer may feel slow during the test.",
        "Please plug in the charger. The test does not start on battery.",
        "Please close games and large programs first.",
        "The test stops by itself if anything gets too hot.",
    };

    public static Diagnosis Diagnose(StressResult r)
    {
        var f = StressAnalysis.Evaluate(r);
        return new Diagnosis(r, f, StressAnalysis.Ladder(r, f), StressAnalysis.ProbablyHardware(f), StressAnalysis.NotFixedHint(f));
    }

    /// <summary>Runs the test (call only after the person agreed) and returns the diagnosis. State is the StressResult.</summary>
    public static async Task<Diagnosis> DiagnoseAsync(IProgress<StressSample>? progress = null, CancellationToken ct = default) =>
        Diagnose(await StressTest.RunAsync(new StressOptions(), progress, ct));
}
