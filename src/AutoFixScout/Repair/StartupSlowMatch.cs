using System.Globalization;
using System.Text;
using AutoFixScout.Probes;

namespace AutoFixScout.Repair;

/// <summary>An entry of the person's own start-up list that Windows measured as slowing start-up.</summary>
public sealed record SlowStartupMatch(StartupEntry Entry, SlowStartupItem Item, long Ms);

/// <summary>
/// Links what Windows named as slowing start-up (Diagnostics-Performance events 101 to 110) to the person's start-up
/// entries (pure). Drivers and services are never matched. An item that matches no entry is left out.
/// </summary>
public static class StartupSlowMatch
{
    private static readonly string[] NotAnApp = { ".sys", ".dll", ".drv", ".inf", ".cat" };

    /// <summary>Lower case file name with no folder, or "" when the text has no program extension.</summary>
    public static string ExeName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim().Trim('"');
        var i = t.LastIndexOfAny(new[] { '\\', '/' });
        if (i >= 0) t = t[(i + 1)..];
        t = t.ToLowerInvariant();
        return t.EndsWith(".exe", StringComparison.Ordinal) ? t : "";
    }

    /// <summary>Lower case letters and digits only, without a trailing ".exe" or ".lnk": "Spotify.exe" and "spotify" are equal.</summary>
    public static string NormaliseName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        foreach (var ext in new[] { ".exe", ".lnk" })
            if (t.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) t = t[..^ext.Length];
        var sb = new StringBuilder();
        foreach (var c in t) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>True for an item that is a driver, a service or another part of Windows rather than an app.</summary>
    public static bool IsNotAnApp(SlowStartupItem item)
    {
        if (item.EventId is not (0 or 101)) return true;
        foreach (var text in new[] { item.FileName, item.Name })
            if (!string.IsNullOrWhiteSpace(text) && NotAnApp.Any(x => text.Trim().Trim('"').EndsWith(x, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    /// <summary>True when the item and the entry are the same program: executable file name first, then the normalised display name.</summary>
    public static bool IsSame(SlowStartupItem item, StartupEntry entry)
    {
        if (IsNotAnApp(item)) return false;
        var entryExe = ExeName(StartupApps.ExecutablePath(entry.Command));
        if (entryExe.Length > 0 && (ExeName(item.FileName) == entryExe || ExeName(item.Name) == entryExe)) return true;
        var shown = NormaliseName(StartupApps.DisplayName(entry));
        return shown.Length > 0 && (NormaliseName(item.Name) == shown || NormaliseName(item.FileName) == shown);
    }

    /// <summary>One match per entry (its largest delay), the largest delay first. Items without an entry are left out.</summary>
    public static IReadOnlyList<SlowStartupMatch> Match(IEnumerable<SlowStartupItem>? slow, IEnumerable<StartupEntry> entries)
    {
        if (slow is null) return Array.Empty<SlowStartupMatch>();
        var items = slow.Where(i => i is not null && i.TotalMs > 0).ToList();
        var result = new List<SlowStartupMatch>();
        foreach (var e in entries)
        {
            var best = items.Where(i => IsSame(i, e)).MaxBy(i => i.TotalMs);
            if (best is not null) result.Add(new SlowStartupMatch(e, best, best.TotalMs));
        }
        return result.OrderByDescending(m => m.Ms).ToList();
    }

    /// <summary>"about 9 seconds" (at least one second).</summary>
    public static string About(long ms)
    {
        var n = Math.Max(1L, (long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero));
        return "about " + n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " second" : " seconds");
    }

    /// <summary>The finding sentence for up to three matched apps; empty when there is none.</summary>
    public static string Sentence(IReadOnlyList<SlowStartupMatch> matches)
    {
        var m = matches.Take(3).ToList();
        string One(SlowStartupMatch x) => $"\"{StartupApps.DisplayName(x.Entry)}\"";
        return m.Count switch
        {
            0 => "",
            1 => $"Windows measured that {One(m[0])} delayed the start of Windows by {About(m[0].Ms)}.",
            2 => $"Windows measured that {One(m[0])} delayed the start of Windows by {About(m[0].Ms)}, and {One(m[1])} by {About(m[1].Ms)}.",
            _ => $"Windows measured that these apps delayed the start of Windows: {One(m[0])} by {About(m[0].Ms)}, {One(m[1])} by {About(m[1].Ms)}, and {One(m[2])} by {About(m[2].Ms)}.",
        };
    }
}
