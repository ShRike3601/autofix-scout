namespace AutoFixScout.Triage;

/// <summary>
/// "My camera isn't working" (spec 8.2).
/// shortcut: ladder is hardcoded like the others; move all three to YAML playbooks together.
/// </summary>
public static class CameraTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private const string ShutterHint =
        "Many laptops have a small sliding cover over the camera, or a key with a camera picture on it (often one of the F keys). " +
        "Please check the cover is open and try pressing that key once.";

    private const string CloseAppsHint =
        "Another app may be using your camera. Please close any app that could be using it, such as Teams, Zoom, Skype, " +
        "WhatsApp or a video call in your web browser, then come back here.";

    public static IReadOnlyList<Finding> Evaluate(CameraState s)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, "We couldn't complete the camera check.", string.Join("; ", s.ProbeErrors)) };
        if (s.Devices.Count == 0)
            return new[] { F(FindingCode.NoCamera, "I can't find a camera in this computer right now.", "No Camera/Image class devices") };

        var disabled = s.Devices.FirstOrDefault(d => d.Disabled);
        if (disabled != null && !s.Devices.Any(d => d.ProblemCode == 0))
            return new[] { F(FindingCode.CameraDisabled, "Your camera is switched off in Windows.", $"'{disabled.Name}' disabled (code 22)") };
        var broken = s.Devices.FirstOrDefault(d => d.ProblemCode is > 0 and not 22);
        if (broken != null && !s.Devices.Any(d => d.ProblemCode == 0))
            return new[] { F(FindingCode.CameraError, "Your camera reports a problem.", $"'{broken.Name}' problem code {broken.ProblemCode}") };

        var blocked = BlockedSwitches(s);
        if (blocked.Count > 0)
            return new[] { F(FindingCode.CameraPrivacyOff, "Your camera is blocked by a privacy setting.", "Off: " + string.Join(", ", blocked)) };

        if (s.AppsUsingCamera.Count > 0)
            return new[] { F(FindingCode.CameraInUse, $"Another app is using your camera: {string.Join(", ", s.AppsUsingCamera)}.", "Consent store: start time without stop time") };

        // Fail closed: unread privacy switches mean we can't say it looks normal.
        if (new[] { s.DeviceAccess, s.AppAccess, s.DesktopAppAccess }.Contains(Tri.Unknown))
            return new[] { F(FindingCode.ProbeIncomplete, "We couldn't complete the camera check.", $"privacy device={s.DeviceAccess} apps={s.AppAccess} desktop={s.DesktopAppAccess}") };
        // Newer Windows builds (26300 on the test laptop) no longer write live camera use to the consent store, so
        // "no app holding it" only means none was reported; the ladder still asks the person to close other apps.
        return new[] { F(FindingCode.LooksNormal, "Your camera looks fine to Windows.", "Device ok, privacy allowed, no app reported holding it") };
    }

    public static IReadOnlyList<Rung> Ladder(CameraState s, IReadOnlyList<Finding> findings)
    {
        var rungs = new List<Rung>();
        switch (findings[0].Code)
        {
            case FindingCode.ProbeIncomplete:
                return rungs; // fail closed: don't change what we couldn't read
            case FindingCode.LooksNormal:
                // Another app holding the camera is the most common cause, and Windows may not report it (see Evaluate).
                rungs.Add(new("close_other_apps", "ui.human_step", 0, CloseAppsHint));
                rungs.Add(new("shutter", "ui.human_step", 0, ShutterHint));
                break;
            case FindingCode.NoCamera:
                rungs.Add(Rescan.Rung("camera", "camera"));
                rungs.Add(new("shutter", "ui.human_step", 0, ShutterHint));
                break;
            case FindingCode.CameraDisabled:
                var off = s.Devices.First(d => d.Disabled);
                rungs.Add(new("enable_camera", "device.enable", 1,
                    $"Your camera \"{off.Name}\" is switched off. I'd like to switch it on. Windows will ask for permission.", off.InstanceId));
                break;
            case FindingCode.CameraPrivacyOff:
                rungs.Add(new("privacy", "ui.open_settings_page", 0,
                    $"I'll open the camera privacy settings. Please switch these to On: {string.Join(", ", BlockedSwitches(s).Select(b => $"\"{b}\""))}.", "camera"));
                break;
            case FindingCode.CameraInUse:
                rungs.Add(new("close_app", "ui.human_step", 0,
                    $"{string.Join(" and ", s.AppsUsingCamera)} is using your camera, and only one app can use it at a time. " +
                    "Please close it, then come back here."));
                break;
        }

        // Next rung for everything but "missing" or "switched off": off and on again (spec 8.2), on the device most likely to be the camera.
        var target = s.Devices.OrderByDescending(d => d.PnpClass == "Camera").FirstOrDefault(d => !d.Disabled);
        if (target != null)
            rungs.Add(new("toggle_camera", "device.disable_enable", 1,
                $"I'd like to switch your camera \"{target.Name}\" off and on again. Windows will ask for permission.", target.InstanceId));
        var rollback = s.Devices.OrderByDescending(d => d.PnpClass == "Camera")
            .Select(d => DriverRules.Rollback("camera", d.Name, d.InstanceId, d.Driver)).FirstOrDefault(r => r != null);
        if (rollback != null) rungs.Add(rollback);
        var broken = s.Devices.FirstOrDefault(d => d.ProblemCode is > 0 and not 22);
        if (broken != null && DriverRules.WindowsUpdate("camera", broken.Name, broken.InstanceId, broken.ProblemCode) is { } wu) rungs.Add(wu);
        rungs.Add(new("shutter", "ui.human_step", 0, ShutterHint));
        return rungs.DistinctBy(r => r.Id).ToList();
    }

    /// <summary>Names as they appear on the Settings page, so the person can find them.</summary>
    private static List<string> BlockedSwitches(CameraState s) =>
        new[] { (s.DeviceAccess, "Camera access"), (s.AppAccess, "Let apps access your camera"), (s.DesktopAppAccess, "Let desktop apps access your camera") }
            .Where(x => x.Item1 == Tri.No).Select(x => x.Item2).ToList();

    public static bool ProbablyHardware(CameraState s) =>
        s.ProbeErrors.Count == 0 && (s.Devices.Count == 0 || s.Devices.All(d => d.ProblemCode is > 0 and not 22));
}
