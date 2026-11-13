using System.Text.RegularExpressions;

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
        if (s.HasBattery && s.WearPercentOfDesign is { } wear)
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
            $"scheme {s.ActiveSchemeGuid}; wear {(s.WearPercentOfDesign?.ToString("0.#") ?? "unknown")}") };
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
        if (s.FastStartup == true)
            r.Add(new("fast_startup_off", "ui.human_step", 0,
                "To switch Fast Startup off, open the Windows search, type \"Control Panel\", open \"Hardware and Sound\", \"Power Options\", \"Choose what the power buttons do\", " +
                "select \"Change settings that are currently unavailable\" and clear \"Turn on fast start-up\". Windows will ask for your permission."));
        return r;
    }

    public static string? NotFixedHint(IReadOnlyList<Finding> after) =>
        after.Any(x => x.Code is FindingCode.PowerBatteryReplace or FindingCode.PowerBatteryWorn)
            ? "A worn battery cannot be repaired by settings. A replacement battery from the maker or a repair shop is the lasting solution."
            : "If the battery still drains quickly or the computer still wakes by itself, the person helping you can check the wake timers and the programs that keep the computer awake. This needs administrator rights.";
}
