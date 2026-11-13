using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace AutoFixScout.Probes;

/// <summary>One boot reported by Diagnostics-Performance event 100 (all times in milliseconds).</summary>
public sealed record BootRecord(DateTime TimeUtc, long BootMs, long MainPathMs, long PostBootMs);

/// <summary>An app, driver or service that Windows reported as slowing start-up (events 101 to 110): friendly name only.</summary>
public sealed record SlowStartupItem(string Name, long TotalMs);

/// <summary>Reads and parses start-up timing events. Read-only; the log may need admin on some machines (then Unknown).</summary>
public static class StartupTimings
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

    /// <summary>Newest boots of the last 30 days; null when the log cannot be read.</summary>
    public static IReadOnlyList<BootRecord>? ReadBoots()
    {
        try
        {
            var week = Days * 24L * 3600 * 1000;
            var list = new List<BootRecord>();
            using var r = new EventLogReader(new EventLogQuery(Log, PathType.LogName,
                $"*[System[EventID=100 and TimeCreated[timediff(@SystemTime) <= {week}]]]"));
            while (list.Count < MaxBoots && r.ReadEvent() is { } e)
                using (e) { if (ParseBoot(e.ToXml()) is { } b) list.Add(b); }
            return list;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Slowest named items in the last 30 days, largest first, one per name; null when the log cannot be read.</summary>
    public static IReadOnlyList<SlowStartupItem>? ReadSlowItems()
    {
        try
        {
            var week = Days * 24L * 3600 * 1000;
            var list = new List<SlowStartupItem>();
            using var r = new EventLogReader(new EventLogQuery(Log, PathType.LogName,
                $"*[System[(EventID>=101 and EventID<=110) and TimeCreated[timediff(@SystemTime) <= {week}]]]"));
            for (var i = 0; i < 300 && r.ReadEvent() is { } e; i++)
                using (e) { if (ParseSlowItem(e.ToXml()) is { } s) list.Add(s); }
            return list.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(x => x.TotalMs)!)
                       .OrderByDescending(x => x.TotalMs).ToList();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Fast Startup switch (HiberbootEnabled); null when unreadable.</summary>
    public static bool? ReadFastStartup()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            return k?.GetValue("HiberbootEnabled") is int v ? v != 0 : null;
        }
        catch (Exception) { return null; }
    }
}
