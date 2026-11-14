using System.Text.RegularExpressions;
using AutoFixScout.Probes;

namespace AutoFixScout.Triage;

/// <summary>probe.power: battery health, power plan, sleep settings, hibernate, Fast Startup and wake causes.</summary>
public sealed class PowerState
{
    public bool HasBattery { get; set; }
    public int? DesignCapacity { get; set; }
    public int? FullChargeCapacity { get; set; }
    public int? ChargePercent { get; set; }
    public string? ActiveSchemeGuid { get; set; }
    /// <summary>Seconds; 0 means never. Null when unknown.</summary>
    public long? SleepAcSeconds { get; set; }
    public long? SleepDcSeconds { get; set; }
    public bool? HibernateEnabled { get; set; }
    public bool? FastStartup { get; set; }
    /// <summary>Wake source text from Power-Troubleshooter event 1 (last 7 days), one entry per wake.</summary>
    public IReadOnlyList<string> WakeSources { get; set; } = new List<string>();
    public int UnexpectedShutdowns { get; set; }
    /// <summary>True when the person reports problems after shutting down or starting up.</summary>
    public bool ReportsShutdownProblems { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
    /// <summary>The parsed battery capacity report (powercfg /batteryreport /xml). Null when it was not read or not readable.</summary>
    public BatteryReportData? BatteryReport { get; set; }
    /// <summary>True when this computer has a battery but its capacity report could not be read.</summary>
    public bool BatteryReportFailed { get; set; }

    public double? WearPercentOfDesign =>
        DesignCapacity is > 0 && FullChargeCapacity is > 0 ? Math.Round(100.0 * FullChargeCapacity.Value / DesignCapacity.Value, 1) : null;
}

/// <summary>Read-only reasoning for "the battery drains, the computer will not sleep or wakes by itself".</summary>
public static class PowerTriage
{
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    /// <summary>Wakes in 7 days at or above which the computer counts as waking up by itself often.</summary>
    public const int OftenWakes = 7;

    /// <summary>Wear (1 - full charge / design capacity, in percent) at or above which the battery should be replaced.</summary>
    public const double WornOutWearPercent = 40;
    /// <summary>Wear at or above which the battery counts as ageing (information only).</summary>
    public const double AgeingWearPercent = 20;
    /// <summary>Charge cycles above which the battery counts as heavily used.</summary>
    public const int ManyCycles = 800;

    /// <summary>"; full charge 52000 mWh on 2025-01-05, 47000 mWh now" from the oldest and newest history entries (numbers and dates only).</summary>
    private static string TrendDetail(BatteryReportData r) =>
        r.History.Count >= 2
            ? $"; history {r.History[0].FullChargeCapacity} mWh on {r.History[0].Start:yyyy-MM-dd}, {r.History[^1].FullChargeCapacity} mWh on {r.History[^1].Start:yyyy-MM-dd}"
            : "";

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    public static bool IsHighPlan(string? guid) =>
        guid is not null && (guid.Equals(HighPerformanceGuid, StringComparison.OrdinalIgnoreCase) || guid.Equals(UltimateGuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>The GUID in the output of "powercfg /getactivescheme" (locale-independent).</summary>
    public static string? ParseActiveScheme(string output) =>
        Regex.Match(output ?? "", @"\b([0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})\b") is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>
    /// The last two hexadecimal values in "powercfg /query" output for one setting: AC then DC, in seconds.
    /// Only the hex values are used, so the text language does not matter.
    /// </summary>
    public static (long Ac, long Dc)? ParseAcDc(string output)
    {
        var hex = Regex.Matches(output ?? "", @"\b0x([0-9a-fA-F]{1,8})\b").Select(m => Convert.ToInt64(m.Groups[1].Value, 16)).ToList();
        return hex.Count >= 2 ? (hex[^2], hex[^1]) : null;
    }

    /// <summary>The wake source line of a Power-Troubleshooter event text ("Wake Source: ..."), or null.</summary>
    public static string? ParseWakeSource(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var m = Regex.Match(description, @"^\s*[^\r\n:]*(?:Wake Source|wake source)\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static IReadOnlyList<Finding> Evaluate(PowerState s)
    {
        if (s.ProbeErrors.Count > 0 && s.ActiveSchemeGuid is null && s.WakeSources.Count == 0 && !s.HasBattery)
            return new[] { F(FindingCode.ProbeIncomplete, "AutoFix Scout could not complete the check.", string.Join("; ", s.ProbeErrors)) };
        var f = new List<Finding>();
        var report = s.HasBattery ? s.BatteryReport : null;
        if (report?.WearPercent is { } rw)
        {
            var held = Math.Round(100 - rw, MidpointRounding.AwayFromZero);
            var detail = $"full charge {report.FullTotal} of design {report.DesignTotal} mWh (wear {rw:0.#}%){(report.MaxCycles is { } cc ? $"; cycles {cc}" : "")}{TrendDetail(report)}";
            if (rw >= WornOutWearPercent)
                f.Add(F(FindingCode.PowerBatteryWornOut, $"The battery can now hold {held:0} percent of the charge it held when it was new. It is worn out and should be replaced.", detail));
            else if (rw >= AgeingWearPercent)
                f.Add(F(FindingCode.PowerBatteryAgeing, $"The battery can now hold {held:0} percent of the charge it held when it was new. This is normal ageing, and the battery runs out somewhat sooner than before.", detail));
            if (report.MaxCycles is > ManyCycles and { } cycles)
                f.Add(F(FindingCode.PowerBatteryManyCycles, $"The battery has been through about {cycles} charge cycles, which is more than most batteries are designed for.", $"cycle count {cycles}"));
        }
        else if (s.HasBattery && s.BatteryReportFailed)
            f.Add(F(FindingCode.PowerBatteryReportUnreadable, "AutoFix Scout could not read the battery report from Windows, so the battery wear could not be measured.", "powercfg /batteryreport failed or returned no usable capacity"));
        if (report?.WearPercent is null && s.HasBattery && s.WearPercentOfDesign is { } wear)
        {
            if (wear < 60)
                f.Add(F(FindingCode.PowerBatteryReplace, $"The battery has worn out. It now holds only about {wear:0} percent of the charge it held when new, so it needs replacing.",
                    $"full charge {s.FullChargeCapacity} of design {s.DesignCapacity} mWh ({wear:0.#}%)"));
            else if (wear < 80)
                f.Add(F(FindingCode.PowerBatteryWorn, $"The battery is worn. It now holds about {wear:0} percent of the charge it held when new, so it runs out sooner than before.",
                    $"full charge {s.FullChargeCapacity} of design {s.DesignCapacity} mWh ({wear:0.#}%)"));
        }
        if (s.HasBattery && s.SleepDcSeconds == 0)
            f.Add(F(FindingCode.PowerSleepNeverOnBattery, "The computer is set never to go to sleep while it runs on battery, so the battery drains when it is left alone.", "sleep on battery: never"));
        if (s.HasBattery && IsHighPlan(s.ActiveSchemeGuid))
            f.Add(F(FindingCode.PowerPlanHighOnBattery, "The power plan is set for high performance, which drains the battery faster.", $"scheme {s.ActiveSchemeGuid}"));
        if (s.WakeSources.Count >= OftenWakes)
        {
            var top = s.WakeSources.GroupBy(w => w, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} x{g.Count()}");
            f.Add(F(FindingCode.PowerWakesOften, $"The computer woke up {s.WakeSources.Count} times in the last 7 days. The most common reasons: {string.Join("; ", top)}.",
                string.Join("; ", s.WakeSources.GroupBy(w => w).Select(g => $"{g.Key} x{g.Count()}"))));
        }
        if (s.FastStartup == true && s.ReportsShutdownProblems)
            f.Add(F(FindingCode.PowerFastStartupOn, "Fast Startup is switched on. It can make a shut down computer keep old problems until it is restarted.", "HiberbootEnabled=1"));
        if (f.Count > 0) return f;
        return new[] { F(FindingCode.LooksNormal,
            s.HasBattery ? "The battery and the power settings look normal." : "This computer has no battery, and its power settings look normal.",
            $"scheme {s.ActiveSchemeGuid}; wear {(s.BatteryReport?.WearPercent ?? (s.WearPercentOfDesign is { } w ? 100 - w : (double?)null))?.ToString("0.#") ?? "unknown"}") };
    }

    public static IReadOnlyList<Rung> Ladder(PowerState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var r = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return r;
        r.Add(new("open_power_sleep", "ui.open_settings_page", 0,
            "AutoFix Scout can open the Windows page for power and sleep settings, where the time before the screen turns off and before the computer sleeps can be changed.", "powersleep"));
        if (s.HasBattery)
            r.Add(new("open_battery_saver", "ui.open_settings_page", 0,
                "AutoFix Scout can open the Windows page for battery use, which shows which programs drain the battery and where Battery Saver is set.", "batterysaver"));
        if (s.ActiveSchemeGuid is not null && !s.ActiveSchemeGuid.Equals(BalancedGuid, StringComparison.OrdinalIgnoreCase))
            r.Add(new("balanced_plan", "power.balanced_plan", 0,
                "AutoFix Scout can switch the power plan to Balanced, the plan Windows recommends. The change can be undone in the power options."));
        if (codes.Contains(FindingCode.PowerWakesOften))
            r.Add(new("check_wake_causes", "ui.human_step", 0,
                "Open the Windows search, type \"Device Manager\", open it, and for the network adapter and the mouse open \"Properties\", \"Power Management\" and clear \"Allow this device to wake the computer\" where it is not needed."));
        var batteryCodes = new[] { FindingCode.PowerBatteryWornOut, FindingCode.PowerBatteryAgeing, FindingCode.PowerBatteryManyCycles };
        if (s.HasBattery && batteryCodes.Any(codes.Contains))
            r.Add(new("reduce_battery_drain", "ui.human_step", 0,
                "To make the battery last longer, lower the screen brightness, turn on Battery Saver when the charge is low, and close programs that are not needed. " +
                "The Windows page for battery use shows which programs use the most."));
        if (codes.Contains(FindingCode.PowerBatteryWornOut) || codes.Contains(FindingCode.PowerBatteryManyCycles))
            r.Add(new("replace_battery", "ui.human_step", 0,
                "A battery that has lost this much of its capacity cannot be restored. The computer maker or a repair shop can replace it. Please ask for a battery made for this exact computer model."));
        if (codes.Contains(FindingCode.PowerBatteryAgeing) && !codes.Contains(FindingCode.PowerBatteryWornOut))
            r.Add(new("battery_replace_later", "ui.human_step", 0,
                "No action is needed yet. If the battery later becomes too short-lived for daily use, the computer maker or a repair shop can replace it."));
        if (s.FastStartup == true)
            r.Add(new("fast_startup_off", "ui.human_step", 0,
                "To switch Fast Startup off, open the Windows search, type \"Control Panel\", open \"Hardware and Sound\", \"Power Options\", \"Choose what the power buttons do\", " +
                "select \"Change settings that are currently unavailable\" and clear \"Turn on fast start-up\". Windows will ask for your permission."));
        return r;
    }

    public static string? NotFixedHint(IReadOnlyList<Finding> after) =>
        after.Any(x => x.Code is FindingCode.PowerBatteryReplace or FindingCode.PowerBatteryWorn or FindingCode.PowerBatteryWornOut or FindingCode.PowerBatteryManyCycles)
            ? "A worn battery cannot be repaired by settings. A replacement battery from the maker or a repair shop is the lasting solution."
            : "If the battery still drains quickly or the computer still wakes by itself, the person helping you can check the wake timers and the programs that keep the computer awake. This needs administrator rights.";
}
