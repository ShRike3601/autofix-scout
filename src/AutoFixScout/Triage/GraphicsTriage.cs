namespace AutoFixScout.Triage;

/// <summary>
/// "Games stutter, crash or show artifacts": graphics card causes. T0/T1 only: nothing here changes a driver, a BIOS
/// setting or an overclock. Every fix is something the person does, or a Settings page that AutoFix Scout opens.
/// </summary>
public static class GraphicsTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    public const int HotCelsius = 87;
    /// <summary>A dedicated card driver older than this many days is reported.</summary>
    public const int OldDriverDays = 365;

    private const string Incomplete = "AutoFix Scout could not complete the graphics check.";

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(GraphicsState s, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var f = new List<Finding>();
        if (s.Gpus.Count == 0)
        {
            f.Add(F(FindingCode.ProbeIncomplete, Incomplete, s.ProbeErrors.Count > 0 ? string.Join("; ", s.ProbeErrors) : "no graphics adapter listed"));
            return f;
        }
        var dedicated = s.Dedicated;
        var tools = s.Tools;

        var hot = s.Nvidia.FirstOrDefault(n => n.ThermalSlowdown || n.TemperatureC >= HotCelsius);
        if (hot != null)
            f.Add(F(FindingCode.GpuOverheating,
                $"The graphics card \"{hot.Name}\" is running very hot{(hot.TemperatureC is { } t ? $" ({t} degrees Celsius)" : "")} and slows itself down to cool off. Games may stutter or close.",
                $"temp={hot.TemperatureC} thermalSlowdown={hot.ThermalSlowdown} reasons=0x{hot.ThrottleReasons:X}"));

        if (s.GraphicsResets > 0)
        {
            if (tools.OverclockActive)
                f.Add(F(FindingCode.GpuOverclockUnstable,
                    $"The graphics driver has stopped and restarted {Times(s.GraphicsResets)} in the last two weeks, and MSI Afterburner applies an overclock when Windows starts. The overclock is the most likely cause.",
                    $"resets={s.GraphicsResets} afterburner overclock at startup: {Describe(tools)}"));
            else
                f.Add(F(FindingCode.GpuDriverResets,
                    $"The graphics driver has stopped and restarted {Times(s.GraphicsResets)} in the last two weeks. This can cause black screens, flickering and games that close.",
                    $"resets={s.GraphicsResets} overclock=none"));
        }
        else if (tools.OverclockActive)
            f.Add(F(FindingCode.GpuOverclockActive,
                "MSI Afterburner applies an overclock every time Windows starts. An overclock that is slightly too high can cause flickering or games that close.",
                Describe(tools)));

        var power = s.Nvidia.FirstOrDefault(n => n.PowerBrake);
        if (power != null)
            f.Add(F(FindingCode.GpuPowerBrake,
                $"The graphics card \"{power.Name}\" has been slowed down because of its power supply. A weak or loose power cable is a common reason.",
                $"hwPowerBrake=true reasons=0x{power.ThrottleReasons:X} power={power.PowerDrawW}/{power.PowerLimitW} W"));

        var narrow = s.Nvidia.FirstOrDefault(n => n.PcieWidthReduced);
        if (narrow != null)
            f.Add(F(FindingCode.GpuPcieLinkNarrow,
                $"The graphics card \"{narrow.Name}\" is connected with only {narrow.PcieWidthCurrent} of {narrow.PcieWidthMax} lanes. The card may not be seated fully in its slot, which lowers game performance.",
                $"pcie width={narrow.PcieWidthCurrent}/{narrow.PcieWidthMax} gen={narrow.PcieGenCurrent}/{narrow.PcieGenMax}"));

        if (s.IsDesktop == Tri.Yes && dedicated != null && !dedicated.DrivesDisplay
            && s.Gpus.Any(g => g.Kind == GpuKind.Integrated && g.DrivesDisplay))
            f.Add(F(FindingCode.GpuMonitorOnIntegrated,
                "The monitor appears to be connected to the motherboard, so Windows draws the picture with the processor graphics and not with the graphics card.",
                $"integrated drives the display, dedicated '{dedicated.Name}' does not"));

        if (dedicated?.DriverDate is { } date && (now - date).TotalDays > OldDriverDays)
            f.Add(F(FindingCode.GpuDriverOld,
                $"The driver of the graphics card \"{dedicated.Name}\" is from {date:MMMM yyyy}, more than a year old. Newer drivers often fix game problems.",
                $"driver {dedicated.DriverVersion} dated {date:yyyy-MM-dd}"));

        if (dedicated != null && dedicated.IsNvidia && s.ResizableBar == Tri.No)
            f.Add(F(FindingCode.GpuResizableBarOff,
                "Resizable BAR is switched off. Some newer games run a little faster with it. This is a setting in the BIOS, and not every computer supports it.",
                $"BAR1 total={s.Bar1TotalMiB} MiB"));

        AddGameSettings(f, s);

        if (!tools.OverclockActive && tools.ToolInstalled && f.All(x => x.Code is FindingCode.GpuResizableBarOff or FindingCode.GpuDriverOld))
            f.Add(F(FindingCode.GpuOverclockToolInstalled,
                "A graphics tuning tool is installed (" + string.Join(", ", ToolNames(tools)) + "). Such tools can change the speed and voltage of the graphics card.",
                $"afterburner={tools.AfterburnerInstalled} running={tools.AfterburnerRunning} others={string.Join("; ", tools.OtherTools)}"));

        if (f.Count == 0 || f.All(x => x.Code is FindingCode.GpuOverclockToolInstalled))
        {
            if (s.ProbeErrors.Count > 0)
                f.Insert(0, F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)));
            else if (f.Count == 0)
                f.Add(F(FindingCode.LooksNormal, "No problem was found with the graphics card.",
                    $"gpus={s.Gpus.Count} nvidia={s.Nvidia.Count} resets={s.GraphicsResets}"));
        }
        return f;
    }

    /// <summary>A computer with at most this much memory counts as weaker for game recording (8 GiB plus room for reserved memory).</summary>
    public const long WeakRamBytes = 9L * 1024 * 1024 * 1024;

    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"\"{n}\""));

    /// <summary>Windows game settings. Only settings that were read and are clearly set the wrong way raise a finding; Unknown never does.</summary>
    private static void AddGameSettings(List<Finding> f, GraphicsState s)
    {
        var g = s.Games;
        var dedicated = s.Dedicated;
        if (g.GameMode == Tri.No)
            f.Add(F(FindingCode.GameSetGameModeOff,
                "Game Mode is turned off in Windows. Some games run more smoothly with it turned on.", "AutoGameModeEnabled=0"));

        bool schedulingCard = dedicated != null && (dedicated.IsNvidia || dedicated.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || dedicated.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase));
        if (g.HardwareScheduling == Tri.No && schedulingCard)
            f.Add(F(FindingCode.GameSetHardwareSchedulingOff,
                "The Windows setting \"Hardware-accelerated GPU scheduling\" is turned off. Some games run more smoothly with it turned on.", "HwSchMode=1"));

        if (g.PowerSavingGames.Count > 0 && s.IsDesktop == Tri.No && dedicated != null)
        {
            var one = g.PowerSavingGames.Count == 1;
            f.Add(F(FindingCode.GameSetGameOnPowerSaving,
                one ? $"The game \"{g.PowerSavingGames[0]}\" is set to use the power-saving graphics chip instead of the graphics card."
                    : $"The games {Quoted(g.PowerSavingGames)} are set to use the power-saving graphics chip instead of the graphics card.",
                $"GpuPreference=1 for {g.PowerSavingGames.Count} game(s): {string.Join(", ", g.PowerSavingGames)}; dedicated '{dedicated.Name}'"));
        }

        if (g.OnBattery == false && string.Equals(g.PowerOverlayGuid, PerfTriage.OverlayBestEfficiencyGuid, StringComparison.OrdinalIgnoreCase))
            f.Add(F(FindingCode.GameSetPowerEfficiencyPluggedIn,
                "The Windows power mode is set to \"Best power efficiency\" while the computer is plugged in. Games can run more slowly in this mode.",
                $"overlay={g.PowerOverlayGuid} onBattery=false"));

        var off = new List<string>();
        if (g.WindowedOptimisations == Tri.No) off.Add("Optimizations for windowed games");
        if (g.VariableRefresh == Tri.No) off.Add("Variable refresh rate");
        if (off.Count > 0)
            f.Add(F(FindingCode.GameSetWindowedOptimisationsOff,
                off.Count == 1
                    ? $"The Windows setting \"{off[0]}\" is turned off. Some games run more smoothly with it turned on."
                    : $"The Windows settings \"{off[0]}\" and \"{off[1]}\" are turned off. Some games run more smoothly with them turned on.",
                $"windowed={g.WindowedOptimisations} vrr={g.VariableRefresh}"));

        if (g.BackgroundRecording == Tri.Yes && g.TotalRamBytes is > 0 and <= WeakRamBytes)
            f.Add(F(FindingCode.GameSetBackgroundRecording,
                "The Xbox Game Bar is recording gameplay in the background. On a computer with little memory, this can make games run more slowly.",
                $"GameDVR_Enabled=1 AppCaptureEnabled=1 HistoricalCaptureEnabled=1 ram={g.TotalRamBytes / 1048576} MB"));
    }

    /// <summary>At most this many games get their own one-click rung (each one is a separate approval).</summary>
    public const int MaxGameFixes = 3;

    private static void AddGameRungs(List<Rung> rungs, HashSet<FindingCode> codes, IReadOnlyList<string> games)
    {
        // One-click fixes first (the person's own settings, undoable from History); the advice rungs stay as the fallback.
        if (codes.Contains(FindingCode.GameSetGameModeOff))
            rungs.Add(new("gpu_game_mode_fix", Repair.GameFixes.GameModeAction, 1,
                "AutoFix Scout will switch Game Mode on. This can be undone from History."));
        if (codes.Contains(FindingCode.GameSetGameOnPowerSaving))
        {
            var fixable = games.Where(Repair.GameFixes.IsValidExeName).Take(MaxGameFixes).ToList();
            for (var i = 0; i < fixable.Count; i++)
                rungs.Add(new($"gpu_game_preference_fix_{i + 1}", Repair.GameFixes.GraphicsCardAction, 1,
                    $"AutoFix Scout will set the game \"{fixable[i]}\" to use the graphics card. This can be undone from History.", fixable[i]));
        }
        if (codes.Contains(FindingCode.GameSetBackgroundRecording))
            rungs.Add(new("gpu_game_capture_fix", Repair.GameFixes.RecordingAction, 1,
                "AutoFix Scout will turn off background recording in the Xbox Game Bar. This can be undone from History."));

        if (codes.Contains(FindingCode.GameSetGameModeOff))
            rungs.Add(new("gpu_game_mode", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Game Mode\" settings. Please turn on \"Game Mode\". You can turn it off again at any time.", "gaming"));
        if (codes.Contains(FindingCode.GameSetGameOnPowerSaving))
            rungs.Add(new("gpu_game_preference", "ui.human_step", 0,
                "Please open the \"Graphics\" settings, select the game in the list, and select \"Options\". Choose \"High performance\" and select \"Save\". You can change this again at any time."));
        var defaults = new List<string>();
        if (codes.Contains(FindingCode.GameSetHardwareSchedulingOff)) defaults.Add("\"Hardware-accelerated GPU scheduling\"");
        if (codes.Contains(FindingCode.GameSetWindowedOptimisationsOff)) defaults.Add("\"Optimizations for windowed games\" and \"Variable refresh rate\"");
        if (defaults.Count > 0)
            rungs.Add(new("gpu_default_graphics", "ui.human_step", 0,
                "Please open the \"Graphics\" settings and select \"Change default graphics settings\". There you can turn on " + string.Join(" and ", defaults) +
                ". Windows may ask you to restart the computer."));
        if (codes.Contains(FindingCode.GameSetPowerEfficiencyPluggedIn))
            rungs.Add(PerfTriage.PowerModeRung());
        if (codes.Contains(FindingCode.GameSetBackgroundRecording))
            rungs.Add(new("gpu_game_capture", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Captures\" settings. Please turn off \"Record what happened\". You can turn it on again at any time.", "capture"));
    }

    private static IEnumerable<string> ToolNames(OverclockToolState t)
    {
        if (t.AfterburnerInstalled == Tri.Yes) yield return "MSI Afterburner";
        foreach (var n in t.OtherTools) yield return n;
    }

    private static string Times(int n) => n == 1 ? "once" : $"{n} times";

    private static string Describe(OverclockToolState t) =>
        string.Join("; ", t.Profiles.Where(p => p.InStartupSection && p.HasOffsets)
            .Select(p => $"core {p.CoreClockKhz / 1000} MHz, memory {p.MemoryClockKhz / 1000} MHz, voltage {p.VoltageMv} mV{(p.CustomCurve ? ", custom curve" : "")}"));

    // Rungs shared with the stress test.
    public static Rung CoolingRung() => new("gpu_cooling", "ui.human_step", 0,
        "Please make sure the fans of the graphics card turn and the air can flow freely. Switch the computer off, clean the dust from the card and the case fans with a soft brush or compressed air, " +
        "and leave space around the computer. On a laptop, please use it on a hard, flat surface.");
    public static Rung PowerCableRung() => new("gpu_power_cable", "ui.human_step", 0,
        "Please switch the computer off and check that the power cables of the graphics card are pushed in firmly at both ends. A helper can check the power supply if the problem remains.");
    public static Rung CleanDriverRung() => new("gpu_clean_driver", "ui.human_step", 0,
        "Please download the latest driver for your graphics card from nvidia.com, amd.com or intel.com. In the installer, select \"Custom\" and turn on \"Perform a clean installation\". " +
        "Windows may ask for your permission, and the screen may go black for a moment.");

    /// <summary>Least risky first. Nothing changes a driver; the person does every step.</summary>
    public static IReadOnlyList<Rung> Ladder(GraphicsState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(f => f.Code).ToHashSet();
        var rungs = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return rungs;

        if (codes.Contains(FindingCode.GpuOverclockUnstable) || codes.Contains(FindingCode.GpuOverclockActive) || codes.Contains(FindingCode.GpuOverclockToolInstalled))
            rungs.Add(new("gpu_reset_overclock", "ui.human_step", 0,
                "Please open MSI Afterburner and select the \"Reset\" button. Then turn off \"Apply overclocking at system startup\" under \"Settings\", and select \"Apply\". " +
                "This returns the graphics card to its factory speed, and you can set the overclock again later."));
        if (codes.Contains(FindingCode.GpuOverheating))
            rungs.Add(CoolingRung());
        if (codes.Contains(FindingCode.GpuPowerBrake))
            rungs.Add(PowerCableRung());
        if (codes.Contains(FindingCode.GpuPcieLinkNarrow))
            rungs.Add(new("gpu_reseat", "ui.human_step", 0,
                "Please switch the computer off, unplug it and press the graphics card firmly into its slot until the latch clicks. Check that the card sits in the top, longest slot of the motherboard."));
        if (codes.Contains(FindingCode.GpuMonitorOnIntegrated))
            rungs.Add(new("gpu_cable", "ui.human_step", 0,
                "Please switch the computer off and move the monitor cable from the back panel of the motherboard to the connector on the graphics card, " +
                "which sits lower on the back of the computer. Then start the computer again."));
        if (codes.Contains(FindingCode.GpuDriverResets) || codes.Contains(FindingCode.GpuOverclockUnstable) || codes.Contains(FindingCode.GpuDriverOld))
            rungs.Add(CleanDriverRung());
        if (codes.Contains(FindingCode.GpuResizableBarOff))
            rungs.Add(new("gpu_enable_rebar", "ui.human_step", 0,
                "Resizable BAR is switched on in the BIOS. Please restart the computer and open the BIOS setup. Turn on \"Above 4G Decoding\" and \"Re-Size BAR Support\", and turn off \"CSM\". " +
                "The names differ by maker, and an update of the motherboard BIOS may be needed first. A helper can do this with you."));
        AddGameRungs(rungs, codes, s.Games.PowerSavingGames);
        rungs.Add(new("gpu_settings", "ui.open_settings_page", 0,
            "AutoFix Scout will open the \"Graphics\" settings. There you can choose which graphics card each game uses.", "graphics"));
        return rungs;
    }

    /// <summary>A card that is overheating or short of power is a hardware matter.</summary>
    public static bool ProbablyHardware(GraphicsState s) =>
        s.Nvidia.Any(n => n.ThermalSlowdown || n.TemperatureC >= HotCelsius || n.PowerBrake || n.PcieWidthReduced);

    public static string? NotFixedHint(IReadOnlyList<Finding> after)
    {
        if (after.Count == 0) return null;
        return after[0].Code switch
        {
            FindingCode.GpuOverheating or FindingCode.GpuPowerBrake or FindingCode.GpuPcieLinkNarrow =>
                "The graphics card still reports a hardware limit. Cleaning, fans or a loose card or cable are the most likely causes. A helper can look at the computer with you.",
            FindingCode.GpuOverclockUnstable or FindingCode.GpuDriverResets =>
                "The graphics driver keeps stopping. A faulty card, its power supply or the temperature are possible causes. A helper can check them for you.",
            FindingCode.LooksNormal =>
                "No problem was found with the graphics card. If a game still stutters, please run the check again while the problem is happening.",
            _ => null,
        };
    }
}
