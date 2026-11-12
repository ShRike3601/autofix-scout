using AutoFixScout.Probes;

namespace AutoFixScout.Triage;

/// <summary>
/// "My microphone is not working". Pure local logic over a MicrophoneState.
/// shortcut: ladder is hardcoded like the others; move them all to YAML playbooks together.
/// </summary>
public static class MicrophoneTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private const string Incomplete = "AutoFix Scout could not complete the microphone check.";

    /// <summary>Below this input level a microphone is close to silent.</summary>
    public const int LowLevelPercent = 10;

    private const string MuteSwitchHint =
        "Many headsets and laptops have a microphone mute switch, or a key with a crossed-out microphone symbol on it. " +
        "Please check that the microphone is not muted there, and try pressing that key once.";

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(MicrophoneState s)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)) };

        var findings = new List<Finding>();
        var stopped = s.Services.Where(x => x.Status == "Stopped").Select(x => x.Name).ToList();
        if (stopped.Count > 0)
            findings.Add(F(FindingCode.AudioServiceStopped, "The part of Windows that handles sound and microphones is not running.",
                "Stopped: " + string.Join(", ", stopped)));

        if (s.Endpoints.Count == 0)
            findings.Add(F(FindingCode.NoMicrophone, "Windows cannot find a microphone in this computer at the moment.", "No capture endpoints"));
        else if (!s.Endpoints.Any(e => e.IsActive))
            findings.Add(F(FindingCode.NoActiveMicrophone, "A microphone is installed, but none is currently available to use.",
                string.Join("; ", s.Endpoints.Select(e => $"'{e.Name}' {(e.Disabled ? "disabled" : "unplugged")}"))));

        if (BrokenDevice(s) is { } broken)
            findings.Add(F(FindingCode.MicDeviceError, "A sound device that your microphone uses reports a problem.",
                $"'{broken.Name}' problem code {broken.ProblemCode}"));

        var blocked = BlockedSwitches(s);
        if (blocked.Count > 0)
            findings.Add(F(FindingCode.MicPrivacyOff, "The microphone is blocked by a privacy setting in Windows.", "Off: " + string.Join(", ", blocked)));

        if (s.Endpoints.Any(e => e.IsActive) && WrongDefault(s) is var (current, better) && better != null)
            findings.Add(F(FindingCode.MicWrongDefault,
                current is null ? "Windows has no microphone selected for input." : $"Windows is listening to \"{current.Name}\" instead of your other microphone.",
                $"Default: '{current?.Name ?? "none"}' ({current?.KindGuess}); alternative: '{better.Name}'"));

        if (s.DefaultMuted == Tri.Yes)
            findings.Add(F(FindingCode.MicMuted, "The microphone is muted in Windows.", "Default input muted"));
        else if (s.DefaultLevelPercent is < LowLevelPercent)
            findings.Add(F(FindingCode.MicLevelVeryLow, "The microphone level is set very low.", $"Default input level {s.DefaultLevelPercent}%"));

        if (findings.Count > 0) return findings;

        // Fail closed: an unread privacy switch or input level means it cannot be called normal.
        if (new[] { s.DeviceAccess, s.AppAccess, s.DesktopAppAccess, s.DefaultMuted }.Contains(Tri.Unknown) || s.DefaultLevelPercent is null)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete,
                $"privacy device={s.DeviceAccess} apps={s.AppAccess} desktop={s.DesktopAppAccess} muted={s.DefaultMuted} level={s.DefaultLevelPercent}") };
        return new[] { F(FindingCode.LooksNormal, "Windows reports no problem with the microphone.", "Input present, unmuted, privacy allowed") };
    }

    public static IReadOnlyList<Rung> Ladder(MicrophoneState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(f => f.Code).ToHashSet();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return Array.Empty<Rung>(); // fail closed

        var rungs = new List<Rung>();
        if (codes.Contains(FindingCode.LooksNormal))
            rungs.Add(new("mute_switch", "ui.human_step", 0, MuteSwitchHint));
        if (codes.Contains(FindingCode.MicPrivacyOff))
            rungs.Add(new("privacy", "ui.open_settings_page", 0,
                "AutoFix Scout will open the microphone privacy settings. " +
                $"Please set the following to \"On\": {string.Join(", ", BlockedSwitches(s).Select(b => $"\"{b}\""))}.", "microphone"));
        if (codes.Contains(FindingCode.MicWrongDefault) && WrongDefault(s).Better is { } better)
            rungs.Add(new("wrong_input", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Sound\" settings. " +
                $"Under \"Input\", please select \"{better.Name}\".", "sound"));
        if (codes.Contains(FindingCode.NoActiveMicrophone))
        {
            if (s.Endpoints.FirstOrDefault(e => e.Disabled) is { } off)
                rungs.Add(new("allow_input", "ui.open_settings_page", 0,
                    $"Your microphone \"{off.Name}\" is turned off in Windows. AutoFix Scout will open the \"All sound devices\" settings. " +
                    $"Please select \"{off.Name}\" under \"Input devices\", then select \"Allow\".", "sound_devices"));
            if (s.Endpoints.FirstOrDefault(e => !e.Disabled) is { } unplugged)
                rungs.Add(new("plug_in", "ui.human_step", 0,
                    $"Windows reports that \"{unplugged.Name}\" is not plugged in. " +
                    "Please check that your microphone or headset is firmly plugged in, then return to AutoFix Scout."));
        }
        if (codes.Contains(FindingCode.MicMuted) || codes.Contains(FindingCode.MicLevelVeryLow))
            rungs.Add(new("unmute_mic", "audio.unmute_microphone", 1,
                "The microphone was muted or set very low. AutoFix Scout can turn it back on. You can change it again at any time."));
        if (!codes.Contains(FindingCode.LooksNormal) && !codes.Contains(FindingCode.NoMicrophone))
            rungs.Add(new("mute_switch", "ui.human_step", 0, MuteSwitchHint));

        rungs.Add(new("restart_services", "service.restart", 1,
            "AutoFix Scout can restart the part of Windows that handles sound and microphones. Windows will ask for your permission.",
            "AudioEndpointBuilder"));
        if (codes.Contains(FindingCode.NoMicrophone) || codes.Contains(FindingCode.NoActiveMicrophone))
            rungs.Add(Rescan.Rung("microphone", "microphone"));

        // Off and on again: the device reporting a problem, otherwise the one behind the selected microphone.
        if (BrokenDevice(s) is { } broken)
            rungs.Add(new("toggle_device", "device.disable_enable", 1,
                $"Your sound device \"{broken.Name}\" reports a problem. AutoFix Scout can turn it off and on again. " +
                "Sound may stop for a moment. Windows will ask for your permission.", broken.InstanceId));
        else if (DefaultDevice(s) is { } dev)
            rungs.Add(new("toggle_device", "device.disable_enable", 1,
                $"AutoFix Scout can turn your sound device \"{dev.Name}\" off and on again. " +
                "Sound may stop for a moment. Windows will ask for your permission.", dev.InstanceId));
        return rungs.DistinctBy(r => r.Id).ToList();
    }

    /// <summary>No microphone at all, or none usable and a sound device reporting an error.</summary>
    public static bool ProbablyHardware(MicrophoneState s) =>
        s.ProbeErrors.Count == 0 && (s.Endpoints.Count == 0 || (!s.Endpoints.Any(e => e.IsActive) && BrokenDevice(s) != null));

    /// <summary>The honest "not fixed" sentence.</summary>
    public static string? NotFixedHint(IReadOnlyList<Finding> after) => after[0].Code switch
    {
        FindingCode.LooksNormal => "Windows reports that your microphone is set up correctly. " +
            "If one app still cannot hear you, please check the microphone setting inside that app.",
        FindingCode.NoMicrophone => "Windows still cannot find a microphone. If you use a headset or an external microphone, " +
            "please try a different USB port or socket.",
        _ => null,
    };

    /// <summary>Names as they appear on the Settings page, so the person can find them.</summary>
    private static List<string> BlockedSwitches(MicrophoneState s) =>
        new[] { (s.DeviceAccess, "Microphone access"), (s.AppAccess, "Let apps access your microphone"),
                (s.DesktopAppAccess, "Let desktop apps access your microphone") }
            .Where(x => x.Item1 == Tri.No).Select(x => x.Item2).ToList();

    /// <summary>
    /// A wrong selected input: none selected while one is available, or a Bluetooth headset selected while another
    /// microphone is available (a headset's microphone often sounds poor or silent, or the headset is out of range).
    /// </summary>
    private static (MicEndpointInfo? Current, MicEndpointInfo? Better) WrongDefault(MicrophoneState s)
    {
        var current = s.Endpoints.FirstOrDefault(e => e.IsDefault);
        var others = s.Endpoints.Where(e => e.IsActive && !e.IsDefault && e.KindGuess != "Bluetooth");
        if (current is null) return (null, s.Endpoints.FirstOrDefault(e => e.IsActive));
        return current.KindGuess == "Bluetooth" ? (current, others.FirstOrDefault()) : (current, null);
    }

    /// <summary>A sound device with an error that an input belongs to, or any such device when no input works.</summary>
    private static AudioDeviceInfo? BrokenDevice(MicrophoneState s)
    {
        var owners = s.Endpoints.Select(e => e.DeviceInstanceId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noneWorks = !s.Endpoints.Any(e => e.IsActive);
        return s.Devices.FirstOrDefault(d => d.ProblemCode is > 0 && (noneWorks || owners.Contains(d.InstanceId)));
    }

    private static AudioDeviceInfo? DefaultDevice(MicrophoneState s) =>
        s.Endpoints.FirstOrDefault(e => e.IsDefault)?.DeviceInstanceId is { } id
            ? s.Devices.FirstOrDefault(d => string.Equals(d.InstanceId, id, StringComparison.OrdinalIgnoreCase))
            : null;
}
