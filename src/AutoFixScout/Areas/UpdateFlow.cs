using AutoFixScout.Triage;

namespace AutoFixScout.Areas;

/// <summary>
/// "Windows Update is not working". Two real tests: after a service or restart fix, use <see cref="AutoTestAsync"/>
/// (services ready and no restart waiting); after the person checks for updates on the settings page, ask
/// <see cref="TestQuestion"/>. <see cref="UsesAutoTest"/> picks by the rung that was just run.
/// </summary>
public static class UpdateFlow
{
    public const string Topic = "update";
    public const string What = "Windows Update";
    public const string TestQuestion = "Does Windows Update now check for updates without an error?";

    private static readonly IUpdateProbe Probe = new RealUpdateProbe();

    public static async Task<Diagnosis> DiagnoseAsync()
    {
        var s = await Task.Run(Probe.Read);
        var f = UpdateTriage.Evaluate(s);
        return new Diagnosis(s, f, UpdateTriage.Ladder(s, f), UpdateTriage.ProbablyHardware(s), UpdateTriage.NotFixedHint(f));
    }

    /// <summary>Re-reads the machine: nothing switched off, "Cryptographic Services" running, no restart waiting.</summary>
    public static Task<bool> AutoTestAsync() => Task.Run(() => UpdateTriage.Healthy(Probe.Read()));

    /// <summary>Service rungs are checked automatically; the settings pages need the person's answer.</summary>
    public static bool UsesAutoTest(Rung rung) => rung.ActionId is "service.restart" or "service.set_manual";
}
