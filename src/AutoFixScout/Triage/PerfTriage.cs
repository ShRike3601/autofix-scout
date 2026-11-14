using System.Text.RegularExpressions;

namespace AutoFixScout.Triage;

/// <summary>
/// "My computer got slow or games stutter". T0 and human steps only: nothing is changed, because the usual causes
/// (heat, power limits, an old BIOS, memory speed, an overclock) are fixed in Windows settings, in the BIOS or with hardware.
/// shortcut: ladder is hardcoded like the others; move all of them to YAML playbooks together.
/// </summary>
public static partial class PerfTriage
{
    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private const string Incomplete = "AutoFix Scout could not complete the computer speed check.";

    /// <summary>"% Performance Limit" below this percent counts as the processor being slowed down.</summary>
    public const double ThrottledBelowPercent = 80;
    /// <summary>Kernel-Processor-Power event 37 (7 days) at or above this count counts as a firmware speed limit.</summary>
    public const int FirmwareLimitEventsMin = 3;
    /// <summary>A BIOS released more than this many days ago counts as old (about 3 years).</summary>
    public const int BiosOldDays = 3 * 365;
    /// <summary>The first microcode revision that includes the final Intel fix for 13th and 14th generation desktop processors.</summary>
    public const uint IntelFixedMicrocode = 0x12B;
    /// <summary>Memory running more than this fraction below its rated speed counts as running below its rating.</summary>
    public const double RamBelowRatedFraction = 0.10;
    public const long LowRamBytes = 8L * 1024 * 1024 * 1024;
    public const int InstabilityShutdownsMin = 2;

    public const string PowerSaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    /// <summary>Windows 11 power mode "Best power efficiency".</summary>
    public const string OverlayBestEfficiencyGuid = "961cc777-2547-4f9d-8174-7d86181b8a7a";

    [GeneratedRegex(@"\bi([579])-(1[34])(\d{3})([A-Za-z]{0,3})\b")]
    private static partial Regex CoreModel();

    /// <summary>
    /// True for 13th and 14th generation Intel Core i5, i7 and i9 desktop processors (no suffix, F, K, KF, KS).
    /// Mobile models (H, HX, HK, P, U), the low-power T models and i3 are not part of the known instability problem.
    /// </summary>
    public static bool IsIntelAffectedDesktop(string? cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName) || !cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return false;
        var m = CoreModel().Match(cpuName);
        return m.Success && m.Groups[4].Value.ToUpperInvariant() is "" or "F" or "K" or "KF" or "KS";
    }

    /// <summary>Microcode revision from the registry value bytes: the revision is the upper four bytes (little-endian). Null when unreadable.</summary>
    public static uint? ParseMicrocode(byte[]? bytes) =>
        bytes is { Length: >= 8 } ? BitConverter.ToUInt32(bytes, 4) : bytes is { Length: 4 } ? BitConverter.ToUInt32(bytes, 0) : null;

    public static bool MicrocodeTooOld(string? cpuName, uint? revision) =>
        IsIntelAffectedDesktop(cpuName) && revision is { } r && r != 0 && r < IntelFixedMicrocode;

    public static bool BiosOld(DateTime? releaseUtc, DateTime nowUtc) =>
        releaseUtc is { } d && (nowUtc - d).TotalDays > BiosOldDays;

    public static bool Throttled(PerfState s) =>
        s.PerformanceLimitPercent is < ThrottledBelowPercent || s.FirmwareLimitEvents >= FirmwareLimitEventsMin;

    public static bool Unstable(PerfState s) =>
        s.HardwareErrors > 0 || s.BlueScreens > 0 || s.UnexpectedShutdowns >= InstabilityShutdownsMin;

    public static bool PowerSaverPlan(PerfState s) =>
        string.Equals(s.ActiveSchemeGuid, PowerSaverGuid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.OverlayGuid, OverlayBestEfficiencyGuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Memory (removable modules only) that runs more than 10 percent below the speed printed on it.</summary>
    public static RamModule? BelowRated(PerfState s) => s.Ram.FirstOrDefault(m => m.Removable
        && m.RatedSpeed is > 0 && m.ConfiguredSpeed is > 0
        && m.ConfiguredSpeed < m.RatedSpeed * (1 - RamBelowRatedFraction));

    public static bool SingleChannel(PerfState s) => s.RamRead && s.Ram.Count == 1 && s.Ram[0].Removable;

    private static string Gb(long bytes) => $"{Math.Round(bytes / 1024.0 / 1024 / 1024)} GB";

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(PerfState s) => Evaluate(s, DateTime.UtcNow);

    public static IReadOnlyList<Finding> Evaluate(PerfState s, DateTime nowUtc)
    {
        var findings = new List<Finding>();
        var readNothing = s.CpuName is null && s.PerformanceLimitPercent is null && s.ActiveSchemeGuid is null
                          && !s.RamRead && !s.InstabilityRead && s.BiosDateUtc is null;
        if (readNothing)
        {
            findings.Add(F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)));
            return findings;
        }

        if (s.InstabilityRead && Unstable(s))
            findings.Add(F(FindingCode.PerfHardwareInstability,
                "Windows recorded blue screens, hardware errors or unexpected shutdowns recently. This points to unstable hardware, often caused by an overclock, an undervolt or a power problem.",
                $"bluescreens={s.BlueScreens} shutdowns={s.UnexpectedShutdowns} whea={s.HardwareErrors} (14 days)"));
        if (MicrocodeTooOld(s.CpuName, s.MicrocodeRevision))
            findings.Add(F(FindingCode.PerfIntelMicrocode,
                $"The processor \"{s.CpuName!.Trim()}\" is a model that can become unstable and slow over time. Its BIOS contains an older update than the one Intel released to prevent this.",
                $"cpu={s.CpuName!.Trim()} microcode=0x{s.MicrocodeRevision:X} fixed=0x{IntelFixedMicrocode:X}"));
        if (Throttled(s))
            findings.Add(F(FindingCode.PerfProcessorThrottled,
                "The processor is being slowed down, usually because of heat or a power limit.",
                $"performance limit={s.PerformanceLimitPercent?.ToString("0") ?? "n/a"}% processor performance={s.ProcessorPerformancePercent?.ToString("0") ?? "n/a"}% firmware limit events (7 days)={s.FirmwareLimitEvents}"));
        if (s.BatterySaverOn == true)
            findings.Add(F(FindingCode.PerfBatterySaver, "Battery saver is turned on. It reduces the speed of the computer to save power.", "battery saver on"));
        if (PowerSaverPlan(s))
            findings.Add(F(FindingCode.PerfPowerSaver, "The power plan or power mode is set to save energy, which makes the computer slower.",
                $"scheme={s.ActiveSchemeGuid} overlay={s.OverlayGuid ?? "n/a"}"));
        if (s.HasBattery && s.OnBattery == true && s.BatterySaverOn != true && !PowerSaverPlan(s))
            findings.Add(F(FindingCode.PerfOnBattery, "The computer is running on battery. Many computers run more slowly on battery than when plugged in.", "on battery"));
        if (BelowRated(s) is { } slow && !s.HasBattery)
            findings.Add(F(FindingCode.PerfRamBelowRated,
                $"The memory runs at {slow.ConfiguredSpeed} MT/s, but it is rated for {slow.RatedSpeed} MT/s. A BIOS setting such as \"XMP\" or \"EXPO\" may be turned off.",
                $"configured={slow.ConfiguredSpeed} rated={slow.RatedSpeed}"));
        if (SingleChannel(s))
            findings.Add(F(FindingCode.PerfRamSingleChannel,
                "The computer has only one memory module, so the memory works on a single channel. Two matching modules are noticeably faster in games.",
                $"modules=1 total={Gb(s.TotalRamBytes)}"));
        if (BiosOld(s.BiosDateUtc, nowUtc))
            findings.Add(F(FindingCode.PerfBiosOld,
                $"The BIOS of this computer was released in {s.BiosDateUtc:yyyy}. A newer BIOS may fix speed and stability problems.",
                $"bios date={s.BiosDateUtc:yyyy-MM-dd}"));
        if (s.RamRead && s.TotalRamBytes > 0 && s.TotalRamBytes < LowRamBytes)
            findings.Add(F(FindingCode.PerfLowRam, $"The computer has only {Gb(s.TotalRamBytes)} of memory, which is little for current games and programs.",
                $"total={Gb(s.TotalRamBytes)}"));
        if (findings.Count == 0)
            findings.Add(F(FindingCode.LooksNormal, "Windows reports no known cause for slow performance.",
                $"limit={s.PerformanceLimitPercent?.ToString("0") ?? "n/a"}% cpu={s.CpuName?.Trim() ?? "n/a"}"));
        return findings;
    }

    // Rungs shared with the stress test (same ids and sentences everywhere).
    public static Rung PlugInRung() => new("perf_plug_in", "ui.human_step", 0,
        "Please plug the computer into its charger. Many computers run at full speed only while plugged in.");
    public static Rung PowerModeRung() => new("perf_power_mode", "ui.open_settings_page", 0,
        "AutoFix Scout will open the \"Power\" settings. Please set \"Power mode\" to \"Best performance\" or \"Balanced\". You can change it back at any time.", "powersleep");
    public static Rung CoolingRung() => new("perf_cooling", "ui.human_step", 0,
        "Please make sure the air vents and fans are free of dust and that nothing blocks them. " +
        "A laptop should stand on a hard, flat surface, not on a bed or a blanket. A cooling pad can also help.");

    /// <summary>Least risky first. Nothing here changes the computer; every rung is a Settings page or a step for the person.</summary>
    public static IReadOnlyList<Rung> Ladder(PerfState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(f => f.Code).ToHashSet();
        var rungs = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return rungs;

        if (codes.Contains(FindingCode.PerfBatterySaver))
            rungs.Add(new("perf_battery_saver", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Battery\" settings. Please turn off \"Battery saver\", or plug the computer in. You can turn it on again at any time.", "batterysaver"));
        if (codes.Contains(FindingCode.PerfOnBattery))
            rungs.Add(PlugInRung());
        if (codes.Contains(FindingCode.PerfPowerSaver) || codes.Contains(FindingCode.PerfProcessorThrottled) || codes.Contains(FindingCode.PerfOnBattery))
            rungs.Add(PowerModeRung());
        if (codes.Contains(FindingCode.PerfProcessorThrottled))
            rungs.Add(CoolingRung());
        if (codes.Contains(FindingCode.PerfBiosOld) || codes.Contains(FindingCode.PerfIntelMicrocode))
            rungs.Add(new("perf_bios_update", "ui.human_step", 0,
                "Please look for a BIOS update on the website of the maker of this computer or of its motherboard, and follow their instructions. " +
                "Windows cannot install a BIOS update for you. Keep the computer plugged in while it installs."));
        if (codes.Contains(FindingCode.PerfRamBelowRated))
            rungs.Add(new("perf_xmp", "ui.human_step", 0,
                "Please restart the computer and open the BIOS setup. Look for a memory profile setting, called \"XMP\", \"DOCP\" or \"EXPO\" depending on the maker, and turn it on. " +
                "If the computer then fails to start, the BIOS goes back to its default settings after a few attempts."));
        if (codes.Contains(FindingCode.PerfRamSingleChannel))
            rungs.Add(new("perf_second_module", "ui.human_step", 0,
                "A second memory module of the same type and speed, placed in the slot the motherboard manual names, can make games run more smoothly. A helper can choose the right one."));
        if (codes.Contains(FindingCode.PerfHardwareInstability))
            rungs.Add(new("perf_undo_overclock", "ui.human_step", 0,
                "If an overclock or an undervolt was ever set, in the BIOS or in a tool such as MSI Afterburner or Intel XTU, please set it back to the default values. " +
                "Also check that the power supply is strong enough and that the power cables are firmly plugged in."));
        // Optional and never automatic: a worn or failing drive makes a computer slow, and its counters need administrator rights.
        if (ReadRungs.OfferDriveRead(s.PhysicalDriveCount, s.DriveHealthRead)) rungs.Add(ReadRungs.DriveHealth());
        return rungs;
    }

    /// <summary>Blue screens together with hardware errors point to the hardware; a helper should look at it.</summary>
    public static bool ProbablyHardware(PerfState s) => s.InstabilityRead && s.HardwareErrors > 0 && s.BlueScreens > 0;

    public static string? NotFixedHint(IReadOnlyList<Finding> after) => after[0].Code switch
    {
        FindingCode.PerfHardwareInstability => "Windows keeps recording hardware errors or sudden shutdowns. " +
            "The power supply, the memory or the processor settings are the most likely cause. A helper can check them for you.",
        FindingCode.PerfIntelMicrocode => "The processor can only be made stable again with a BIOS update from the maker of the computer or motherboard. " +
            "A helper can do this for you.",
        FindingCode.PerfProcessorThrottled => "The processor is still being slowed down. Dust in the cooler or old thermal paste is the most likely cause. " +
            "A helper can clean the computer and check its cooling.",
        FindingCode.LooksNormal => "Windows reports no known cause for slow performance. If the computer is still slow, " +
            "a helper can look at the programs that use the most resources while the problem is happening.",
        _ => null,
    };
}
