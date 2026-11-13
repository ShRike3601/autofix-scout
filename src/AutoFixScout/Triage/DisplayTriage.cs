namespace AutoFixScout.Triage;

/// <summary>
/// "My screen or monitor does not show a picture". T0/T1 only. Display adapters and their drivers are never changed:
/// a bad one can stop Windows starting, so an adapter problem is reported and handed to a helper.
/// shortcut: ladder is hardcoded like the others; move all of them to YAML playbooks together.
/// </summary>
public static class DisplayTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private const string Incomplete = "AutoFix Scout could not complete the screen check.";
    public const int LowBrightness = 10;
    /// <summary>The refresh rate counts as low when it is at least this many Hz below the best one at the same resolution.</summary>
    public const int LowRefreshGapHz = 20;
    /// <summary>Scale above this percent on a screen narrower than <see cref="SmallScreenWidth"/> is reported (info only).</summary>
    public const int HighScalePercent = 200;
    public const int SmallScreenWidth = 2560;

    private const string HelperHint =
        "The display adapter in this computer reports a problem. AutoFix Scout does not change display adapters, " +
        "because a mistake there can stop Windows from starting. A helper should look at it.";

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(DisplayState s)
    {
        var findings = new List<Finding>();
        // An adapter problem is reported even when other reads failed: it is the most important thing to know.
        var broken = s.Adapters.FirstOrDefault(a => a.ProblemCode is > 0);
        if (broken != null)
            findings.Add(F(FindingCode.DisplayAdapterError, $"The display adapter \"{broken.Name}\" reports a problem.",
                $"'{broken.Name}' problem code {broken.ProblemCode}"));
        if (s.ProbeErrors.Count > 0 || s.ConnectedScreens is null || s.ShowingScreens is null)
        {
            findings.Add(F(FindingCode.ProbeIncomplete, Incomplete,
                s.ProbeErrors.Count > 0 ? string.Join("; ", s.ProbeErrors) : $"connected={s.ConnectedScreens} showing={s.ShowingScreens}"));
            return findings;
        }
        if (s.ConnectedScreens > s.ShowingScreens)
            findings.Add(F(FindingCode.ExternalDisplayNotShown,
                "A second screen is connected, but Windows is not showing a picture on it.",
                $"connected={s.ConnectedScreens} showing={s.ShowingScreens}"));
        if (s.BrightnessPercent is < LowBrightness)
            findings.Add(F(FindingCode.BrightnessVeryLow, "The screen brightness is set very low.", $"brightness={s.BrightnessPercent}%"));
        AddPictureQualityFindings(s, findings);
        if (findings.Count == 0)
            findings.Add(F(FindingCode.LooksNormal, "Windows reports no problem with your screens.",
                $"connected={s.ConnectedScreens} showing={s.ShowingScreens} brightness={s.BrightnessPercent?.ToString() ?? "n/a"}"));
        return findings;
    }

    private static readonly int[] RecommendedScales = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    private static string Size(DisplayMode m) => $"{m.Width} x {m.Height}";

    /// <summary>Resolution, refresh rate and scaling of each active screen. Read-only; screens with no mode list are skipped.</summary>
    private static void AddPictureQualityFindings(DisplayState s, List<Finding> findings)
    {
        foreach (var scr in s.Screens)
        {
            if (scr.Native is { } native && scr.Current.Pixels > 0 && scr.Current.Pixels < native.Pixels)
                findings.Add(F(FindingCode.DisplayNotNativeResolution,
                    $"The screen is set to {Size(scr.Current)}, but it can show {Size(native)}. Text and pictures may look blurry.",
                    $"{scr.Name} current={Size(scr.Current)} native={Size(native)}"));
            if (scr.BestRefresh is { } best && scr.Current.Refresh > 1 && best - scr.Current.Refresh >= LowRefreshGapHz)
                findings.Add(F(FindingCode.DisplayLowRefreshRate,
                    $"The screen is running at {scr.Current.Refresh} Hz, but it can run at {best} Hz at this size. Motion may look less smooth than it could.",
                    $"{scr.Name} refresh={scr.Current.Refresh} best={best} at {Size(scr.Current)}"));
            if (scr.ScalePercent is { } pct && (!RecommendedScales.Contains(pct) || (pct > HighScalePercent && scr.Current.Width < SmallScreenWidth)))
                findings.Add(F(FindingCode.DisplayUnusualScaling,
                    $"Text and apps are scaled to {pct} percent, which is unusual for a screen of this size. Some text may look blurry or too large.",
                    $"{scr.Name} scale={pct}% at {Size(scr.Current)}"));
        }
    }

    /// <summary>Least risky first. Nothing here touches a display adapter or driver.</summary>
    public static IReadOnlyList<Rung> Ladder(DisplayState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(f => f.Code).ToHashSet();
        var rungs = new List<Rung>();
        var adapterProblem = codes.Contains(FindingCode.DisplayAdapterError);
        // Fail closed: nothing is changed when the screens could not be read, but the person may still check the cable.
        if (codes.Contains(FindingCode.ProbeIncomplete))
            return adapterProblem ? new[] { CheckCable() } : rungs;

        var qualityOnly = !codes.Contains(FindingCode.BrightnessVeryLow) && !codes.Contains(FindingCode.ExternalDisplayNotShown) && !adapterProblem;
        if (codes.Contains(FindingCode.DisplayNotNativeResolution))
            rungs.Add(new("display_resolution", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Display\" settings. Under \"Display resolution\", please choose the one marked \"Recommended\". " +
                "Windows asks to keep the change; if the picture looks wrong, it goes back by itself after a few seconds.", "display"));
        if (codes.Contains(FindingCode.DisplayLowRefreshRate))
            rungs.Add(new("display_refresh", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Display\" settings. Please select \"Advanced display\", then under \"Choose a refresh rate\" " +
                "pick the highest rate in the list.", "display"));
        if (codes.Contains(FindingCode.DisplayUnusualScaling))
            rungs.Add(new("display_scale", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Display\" settings. Under \"Scale\", please choose the value marked \"Recommended\", " +
                "if text looks blurry or too large.", "display"));
        if (qualityOnly) return rungs;

        if (codes.Contains(FindingCode.BrightnessVeryLow))
            rungs.Add(new("brightness", "display.set_brightness", 1,
                "The screen brightness is set very low. AutoFix Scout can set it to 70 percent. You can change it again at any time."));
        if (!adapterProblem)
            rungs.Add(new("extend", "display.extend", 1,
                "AutoFix Scout can tell Windows to show the desktop on every connected screen (\"Extend\"). " +
                "You can undo this at any time by pressing the Windows key and P together."));
        rungs.Add(new("display_settings", "ui.open_settings_page", 0,
            "AutoFix Scout will open the \"Display\" settings. Under \"Multiple displays\", please select \"Detect\". " +
            "If a screen is shown there, please choose how it should be used.", "display"));
        rungs.Add(CheckCable());
        return rungs;
    }

    private static Rung CheckCable() =>
        new("check_cable", "ui.human_step", 0,
            "Please check that the monitor is turned on and that its cable is firmly plugged in at both ends. " +
            "Then use the buttons on the monitor to select the input the cable is plugged into, such as \"HDMI\" or \"DisplayPort\".");

    /// <summary>A display adapter reporting a problem goes to a helper; nothing is fixed automatically.</summary>
    public static bool ProbablyHardware(DisplayState s) => s.Adapters.Any(a => a.ProblemCode is > 0);

    public static string? NotFixedHint(IReadOnlyList<Finding> after)
    {
        if (after.Any(f => f.Code == FindingCode.DisplayAdapterError)) return HelperHint;
        return after[0].Code switch
        {
            FindingCode.ExternalDisplayNotShown => "Windows detects the second screen but cannot show a picture on it. " +
                "The cable or the monitor itself is the most likely cause. A helper can check them for you.",
            FindingCode.LooksNormal => "Windows reports no problem with your screens. If a monitor is plugged in but Windows does not detect it, " +
                "the cable, the monitor or its input setting is the most likely cause.",
            _ => null,
        };
    }
}
