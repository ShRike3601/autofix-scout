using AutoFixScout.Probes;
using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>
/// "Something else is wrong" (and "a program keeps closing"): the broad software check-up.
/// Real test: when the check found live problems (clock, proxy, hosts file, services, PATH, TEMP, Store apps),
/// <see cref="AutoTestAsync"/> checks they are gone; otherwise the person answers <see cref="TestQuestion"/>,
/// because crash and update history stays in the event log after a successful repair.
/// </summary>
public static class HealthFlow
{
    public const string Topic = "health";
    public const string What = "computer";
    public const string TestQuestion = "Is the problem resolved now?";

    private static readonly IHealthProbe Probe = new RealHealthProbe();

    public static async Task<Diagnosis> DiagnoseAsync()
    {
        var s = await Task.Run(Probe.Read);
        var f = HealthTriage.Evaluate(s);
        return new Diagnosis(s, f, HealthTriage.Ladder(s, f), HealthTriage.ProbablyHardware(s), HealthTriage.NotFixedHint(f));
    }

    /// <summary>Triage again for a state the person refined (for example the program they picked).</summary>
    public static Diagnosis Diagnose(HealthState s)
    {
        var f = HealthTriage.Evaluate(s);
        return new Diagnosis(s, f, HealthTriage.Ladder(s, f), HealthTriage.ProbablyHardware(s), HealthTriage.NotFixedHint(f));
    }

    public static string ProgramQuestion(CrashInfo c) =>
        $"Is \"{c.App.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)}\" the program that keeps closing?";

    public const string NoCrashRecorded =
        "Windows has not recorded a program closing unexpectedly in the last two weeks. AutoFix Scout will check the rest of the computer instead.";

    public static bool UsesAutoTest(IReadOnlyList<Finding> findings) =>
        findings.Any(f => HealthTriage.Rechecked.Contains(f.Code));

    public static async Task<bool> AutoTestAsync(IReadOnlyList<Finding> before)
    {
        try { return HealthTriage.Resolved(before, (await DiagnoseAsync()).Findings); }
        catch (Exception) { return false; } // fail closed
    }
}
