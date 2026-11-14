using System.Diagnostics.Eventing.Reader;

namespace AutoFixScout.Probes;

/// <summary>Reads start-up timing events. Read-only; the log may need admin on some machines (then Unknown). Parsers: StartupTimings.Parse.cs.</summary>
public static partial class StartupTimings
{
    /// <summary>Newest boots of the last 30 days; null when the log cannot be read.</summary>
    public static IReadOnlyList<BootRecord>? ReadBoots()
    {
        try
        {
            var week = Days * 24L * 3600 * 1000;
            var list = new List<BootRecord>();
            using var r = EventLogs.Open(Log, $"*[System[EventID=100 and TimeCreated[timediff(@SystemTime) <= {week}]]]", TimeSpan.FromDays(Days));
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
            using var r = EventLogs.Open(Log, $"*[System[(EventID>=101 and EventID<=110) and TimeCreated[timediff(@SystemTime) <= {week}]]]", TimeSpan.FromDays(Days));
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
            Activity.ReadingRegistry("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "(Fast Startup setting)");
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");

            return k?.GetValue("HiberbootEnabled") is int v ? v != 0 : null;
        }
        catch (Exception) { return null; }
    }
}
