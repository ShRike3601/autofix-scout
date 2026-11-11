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

        var broken = state.Devices.FirstOrDefault(d => d.ProblemCode is > 0);
        if (broken != null)
            rungs.Add(new("toggle_device", "device.disable_enable", 1,
                $"Your sound device \"{broken.Name}\" reports a problem. I'd like to switch it off and on again. Windows will ask for permission.",
                broken.InstanceId));
        return rungs;
    }

    /// <summary>Spec 8.1 escalation: no output endpoint and a device still reporting an error.</summary>
    public static bool ProbablyHardware(AudioState state) =>
        state.ProbeErrors.Count == 0 && !state.Endpoints.Any(e => e.IsActive) && state.Devices.Any(d => d.ProblemCode is > 0);
}

/// <summary>What the runner needs from the outside world (UI + system actions), so the flow is testable.</summary>
public interface IFixHost
{
    /// <summary>Shows the rung's sentence with [Yes, try it] [Not now]. False = Not now.</summary>
    Task<bool> ConfirmAsync(Rung rung);
    /// <summary>Executes the rung. Returns success plus a detail line for the helper report.</summary>
    Task<(bool Ok, string Detail)> RunAsync(Rung rung);
    /// <summary>Real test: plays a tone and asks "Did you hear it?".</summary>
    Task<bool> TestAsync();
}

public static class LadderRunner
{
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
            if (!await host.ConfirmAsync(rung)) { step.Result = StepResult.Skipped; return Outcome.Abandoned; }
            step.Approved = true;
            step.ApprovedAt = step.StartedAt = DateTime.UtcNow;
            var (ok, detail) = await host.RunAsync(rung);
            step.FinishedAt = DateTime.UtcNow;
            step.Detail = detail;
            if (!ok) { step.Result = StepResult.Failed; step.Error = detail; continue; }
            step.Result = StepResult.Success;
            step.UserConfirmedFixed = await host.TestAsync();
            if (step.UserConfirmedFixed == true) return Outcome.Fixed;
        }
        return Outcome.NotFixed;
    }
}

/// <summary>One symptom's check result: what the probe saw, what triage found, the ladder, and how to end honestly.</summary>
public sealed record Diagnosis(object State, IReadOnlyList<Finding> Findings, IReadOnlyList<Rung> Ladder, bool ProbablyHardware, string? NotFixedHint = null);
