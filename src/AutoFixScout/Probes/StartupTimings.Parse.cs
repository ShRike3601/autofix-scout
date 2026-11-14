using System.Globalization;
using System.Xml.Linq;

#if AFS_EXECUTOR
// Linked into the elevated helper: a separate namespace, so the app and the helper never clash (the tests reference both).
namespace AutoFixScout.Executor.Shared;
#else
namespace AutoFixScout.Probes;
#endif

/// <summary>One boot reported by Diagnostics-Performance event 100 (all times in milliseconds).</summary>
public sealed record BootRecord(DateTime TimeUtc, long BootMs, long MainPathMs, long PostBootMs);

/// <summary>An app, driver or service that Windows reported as slowing start-up (events 101 to 110): friendly name only.</summary>
public sealed record SlowStartupItem(string Name, long TotalMs);

/// <summary>Parses start-up timing events (pure; shared with the elevated helper).</summary>
public static partial class StartupTimings
{
    public const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";
    public const int Days = 30, MaxBoots = 10;

    private static Dictionary<string, string> Data(string xml)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var doc = XDocument.Parse(xml);
        foreach (var e in doc.Descendants().Where(x => x.Name.LocalName == "Data"))
            if ((string?)e.Attribute("Name") is { } n) d[n] = e.Value;
        return d;
    }

    private static DateTime Time(string xml)
    {
        var t = XDocument.Parse(xml).Descendants().FirstOrDefault(x => x.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var r) ? r : DateTime.MinValue;
    }

    private static long? Num(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;

    /// <summary>Event 100 XML to a boot record; null when the event is another one or lacks the timings.</summary>
    public static BootRecord? ParseBoot(string xml)
    {
        try
        {
            var id = XDocument.Parse(xml).Descendants().FirstOrDefault(x => x.Name.LocalName == "EventID")?.Value;
            if (id != "100") return null;
            var d = Data(xml);
            if (Num(d, "BootTime") is not { } b || Num(d, "MainPathBootTime") is not { } m) return null;
            return new BootRecord(Time(xml), b, m, Num(d, "BootPostBootTime") ?? 0);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Events 101 to 110 XML to the friendly name of the slow item; null when there is none.</summary>
    public static SlowStartupItem? ParseSlowItem(string xml)
    {
        try
        {
            var id = XDocument.Parse(xml).Descendants().FirstOrDefault(x => x.Name.LocalName == "EventID")?.Value;
            if (!int.TryParse(id, out var n) || n is < 101 or > 110) return null;
            var d = Data(xml);
            var name = d.TryGetValue("FriendlyName", out var f) && !string.IsNullOrWhiteSpace(f) ? f.Trim() : null;
            if (name is null) return null;
            return new SlowStartupItem(name, Num(d, "TotalTime") ?? 0);
        }
        catch (Exception) { return null; }
    }
}
