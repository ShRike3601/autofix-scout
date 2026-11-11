using AutoFixScout.Drivers;

namespace AutoFixScout.Triage;

/// <summary>One rung of a fix ladder: an allowlisted action id, its tier, and the sentence shown to the person.</summary>
public sealed record Rung(string Id, string ActionId, int Tier, string Say, string? Arg = null);

/// <summary>
/// No-sound ladder (spec 8.1), least risky first.
/// shortcut: ladder is hardcoded, move to the YAML playbook engine (spec 7) when camera/Wi-Fi land.
/// T2 driver rungs come with Milestone 3.
/// </summary>
public static class AudioLadder
{
    public static IReadOnlyList<Rung> For(AudioState state, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(f => f.Code).ToHashSet();
        // Fail closed: if we couldn't read the machine, don't change it.
        if (codes.Contains(FindingCode.ProbeIncomplete)) return Array.Empty<Rung>();

        var rungs = new List<Rung>();
        if (codes.Contains(FindingCode.DefaultIsHdmiOrBluetooth))
        {
            var def = state.Endpoints.First(e => e.IsDefault);
            var speaker = state.Endpoints.First(e => !e.IsDefault && e.IsActive && e.KindGuess is "Speakers" or "Headphones");
            rungs.Add(new("wrong_output", "ui.open_settings_page", 0,
                $"Your sound is going to \"{def.Name}\" instead of your speakers. I'll open Sound settings. " +
                $"Under \"Output\", please choose \"{speaker.Name}\".", "sound"));
        }
        if (codes.Contains(FindingCode.Muted) || codes.Contains(FindingCode.VolumeVeryLow))
            rungs.Add(new("unmute", "audio.unmute_and_set_volume", 1,
                "Your sound was turned off or very low. I'd like to turn it up. You can undo this."));

        rungs.Add(new("restart_services", "service.restart", 1,
            "I'd like to restart the part of Windows that plays sound. Windows will ask for permission.", "AudioEndpointBuilder"));
        // An uninstalled or vanished sound device leaves no active output; Windows reinstalls it on a hardware scan.
        if (codes.Contains(FindingCode.NoOutputEndpoint) || codes.Contains(FindingCode.NoActiveOutput))
            rungs.Add(Rescan.Rung("audio", "sound device"));

        var broken = state.Devices.FirstOrDefault(d => d.ProblemCode is > 0);
        if (broken != null)
            rungs.Add(new("toggle_device", "device.disable_enable", 1,
                $"Your sound device \"{broken.Name}\" reports a problem. I'd like to switch it off and on again. Windows will ask for permission.",
                broken.InstanceId));
        // T2, last before escalating (spec 8.1): often the real fix for "it broke after an update".
        var rollback = state.Devices.OrderByDescending(d => d.ProblemCode is > 0)
            .Select(d => DriverRules.Rollback("sound", d.Name, d.InstanceId, d.Driver)).FirstOrDefault(r => r != null);
        if (rollback != null) rungs.Add(rollback);
        if (broken != null && DriverRules.WindowsUpdate("sound", broken.Name, broken.InstanceId, broken.ProblemCode) is { } wu) rungs.Add(wu);
        return rungs;
    }

    /// <summary>Spec 8.1 escalation: no output endpoint and a device still reporting an error.</summary>
    public static bool ProbablyHardware(AudioState state) =>
        state.ProbeErrors.Count == 0 && !state.Endpoints.Any(e => e.IsActive) && state.Devices.Any(d => d.ProblemCode is > 0);
}

/// <summary>
/// "Scan for hardware changes" (T1): brings back a device that was uninstalled in Device Manager or dropped off after
/// an update, using the driver already on the PC. Changes nothing that's already there.
/// </summary>
public static class Rescan
{
    public static readonly string[] Areas = { "audio", "camera", "network" };

    public static Rung Rung(string area, string what) =>
        new("rescan", "device.rescan", 1,
            $"Windows can't see your {what} right now. I'd like to ask Windows to look for it again, like " +
            "\"Scan for hardware changes\" in Device Manager. Windows will ask for permission.", area);
}

/// <summary>Shared T2 rule (spec 7): roll back only if the driver changed recently and an older package that fits is on this PC.</summary>
public static class DriverRules
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

    public static Rung? Rollback(string what, string deviceName, string instanceId, DriverStatus? driver) =>
        driver is not null && driver.CanRollBack(RecentWindow)
            ? new Rung("rollback_driver", "driver.rollback", 2,
                $"The {what} driver for \"{deviceName}\" was updated on {driver.StagedUtc!.Value.ToLocalTime():d MMMM}, " +
                $"and that's often when problems start. I'd like to put back the previous version ({driver.PreviousVersion}). " +
                "I'll make a save point first, and if anything goes wrong I'll put everything back automatically. " +
                "Windows will ask for permission.", instanceId)
            : null;

    /// <summary>
    /// Only for a device that still reports an error: a working device is never touched because a newer driver exists (spec 2).
    /// </summary>
    public static Rung? WindowsUpdate(string what, string deviceName, string instanceId, int? problemCode) =>
        problemCode is > 0 and not 22
            ? new Rung("windows_update_driver", "driver.install_from_windows_update", 2,
                $"Your {what} device \"{deviceName}\" still reports a problem. I'd like to ask Windows Update for a driver for it. " +
                "I'll make a save point first, and if anything goes wrong I'll put everything back automatically. " +
                "This needs the internet, can take a few minutes, and Windows will ask for permission.", instanceId)
            : null;
}

/// <summary>What the runner needs from the outside world (UI + system actions), so the flow is testable.</summary>
public interface IFixHost
{
    /// <summary>Shows the rung's sentence with [Yes, try it] [Not now]. False = Not now.</summary>
    Task<bool> ConfirmAsync(Rung rung);
    /// <summary>Executes the rung. Returns success plus a detail line for the helper report.</summary>
    Task<(bool Ok, string Detail)> RunAsync(Rung rung);
    /// <summary>Real test for the symptom (beep, camera picture, internet).</summary>
    Task<bool> TestAsync();
    /// <summary>After a T2 change: keep it (commit the save point) or put everything back. Returns false if the restore failed.</summary>
    Task<bool> SettleAsync(Rung rung, bool keep);
}

public static class LadderRunner
{
    /// <summary>Spec 6 hard invariant.</summary>
    public const int MaxT2PerSession = 3;

    /// <summary>
    /// Act on symptoms (spec 2): when the check found nothing wrong, run the real test before offering any fix.
    /// Returns NoFaultFound if it works; null means go on to the ladder (something's wrong, or the test failed).
    /// </summary>
    public static async Task<Outcome?> NoFaultAsync(IReadOnlyList<Finding> findings, Func<Task<bool>> test) =>
        findings.Count > 0 && findings.All(f => f.Code == FindingCode.LooksNormal) && await test()
            ? Outcome.NoFaultFound
            : null;

    /// <summary>
    /// Runs each rung at most once (spec 6: no loops) and stops at the first confirmed fix.
    /// Every rung offered gets a step_log row; nothing defaults to success (spec 12).
    /// </summary>
    public static async Task<Outcome> RunAsync(IReadOnlyList<Rung> ladder, IFixHost host, List<StepLog> steps)
    {
        foreach (var rung in ladder)
        {
            var step = new StepLog { PlaybookStep = rung.Id, ActionId = rung.ActionId, Tier = rung.Tier, ProposedBy = "playbook" };
            steps.Add(step);
            if (rung.Tier >= 2 && steps.Count(s => s.Tier >= 2 && s.StartedAt != null) >= MaxT2PerSession)
            {
                step.Result = StepResult.Skipped;
                step.Error = "T2 limit for this session reached";
                continue;
            }
            if (!await host.ConfirmAsync(rung)) { step.Result = StepResult.Skipped; return Outcome.Abandoned; }
            step.Approved = true;
            step.ApprovedAt = step.StartedAt = DateTime.UtcNow;
            var (ok, detail) = await host.RunAsync(rung);
            step.FinishedAt = DateTime.UtcNow;
            step.Detail = detail;
            if (!ok)
            {
                step.Result = StepResult.Failed;
                step.Error = detail;
                // A failed T2 step may have left a half-done change: settling retries the restore.
                if (rung.Tier >= 2 && !await host.SettleAsync(rung, keep: false))
                {
                    step.Error += "; automatic undo failed, save point kept for the helper";
                    return Outcome.NotFixed;
                }
                continue;
            }
            step.Result = StepResult.Success;
            step.UserConfirmedFixed = await host.TestAsync();

            if (rung.Tier >= 2)
            {
                // A T2 change that didn't fix it is undone (spec 7: on_fail: undo).
                var settled = await host.SettleAsync(rung, keep: step.UserConfirmedFixed == true);
                if (step.UserConfirmedFixed != true)
                {
                    step.Result = settled ? StepResult.Undone : StepResult.Failed;
                    if (!settled)
                    {
                        // The machine is in a state we didn't plan for: stop changing things and hand over to a helper.
                        step.Error = "automatic undo failed; save point kept for the helper";
                        return Outcome.NotFixed;
                    }
                }
            }
            if (step.UserConfirmedFixed == true) return Outcome.Fixed;
        }
        return Outcome.NotFixed;
    }
}

/// <summary>One symptom's check result: what the probe saw, what triage found, the ladder, and how to end honestly.</summary>
public sealed record Diagnosis(object State, IReadOnlyList<Finding> Findings, IReadOnlyList<Rung> Ladder, bool ProbablyHardware, string? NotFixedHint = null);
