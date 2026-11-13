namespace AutoFixScout.Repair;

/// <summary>
/// Wi-Fi adapter settings that make older drivers drop the connection on busy enterprise networks (campus, office):
/// the newest Wi-Fi standard on an old driver, eager roaming between access points, and radio power saving.
/// Shared with the elevated executor (linked file), which changes only these values, only to these targets, and only
/// when the adapter's own driver lists the target as a supported value (Ndi\Params\&lt;keyword&gt;\Enum).
/// Found on the test bench (2026-11-13): an Intel AX201 with a 2021 driver on university WPA2-Enterprise Wi-Fi.
/// </summary>
internal static class WifiTuning
{
    /// <summary>Registry keyword (in the adapter's class key) and the target value, as the driver's Enum stores it.</summary>
    public static readonly IReadOnlyList<(string Keyword, string Target, string Meaning)> Targets = new[]
    {
        ("IEEE11nMode", "2", "802.11ac instead of 802.11ax"),
        ("RoamAggressiveness", "1", "roaming aggressiveness Medium-low"),
        ("MIMOPowerSaveMode", "3", "no radio power saving (No SMPS)"),
        ("RoamingPreferredBandType", "2", "prefer the 5 GHz band"),
    };

    /// <summary>A Wi-Fi driver this old is likely to be missing years of fixes.</summary>
    public static readonly TimeSpan OldDriver = TimeSpan.FromDays(2 * 365);

    /// <summary>
    /// The changes to make: keyword, current value, target. Only keywords the driver has, whose target it supports,
    /// and whose current value differs. Empty = nothing to do (already tuned or not this kind of adapter).
    /// </summary>
    /// <param name="current">Keyword -> current value (absent keywords are skipped).</param>
    /// <param name="supported">Keyword -> values the driver lists as valid.</param>
    public static IReadOnlyList<(string Keyword, string From, string To)> Plan(
        IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, IReadOnlyCollection<string>> supported) =>
        Targets
            .Where(t => current.ContainsKey(t.Keyword) && supported.TryGetValue(t.Keyword, out var ok) && ok.Contains(t.Target))
            .Where(t => current[t.Keyword] != t.Target)
            .Select(t => (t.Keyword, current[t.Keyword], t.Target))
            .ToList();

    /// <summary>"6-29-2021" (the class key's DriverDate format) or "2021-06-29".</summary>
    public static DateTime? ParseDriverDate(string? value) =>
        DateTime.TryParseExact(value?.Trim(), new[] { "M-d-yyyy", "yyyy-MM-dd", "M/d/yyyy" },
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
