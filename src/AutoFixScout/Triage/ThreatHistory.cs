using System.Text.RegularExpressions;

namespace AutoFixScout.Triage;

/// <summary>One threat that Microsoft Defender found recently. File paths are never kept: they contain user names.</summary>
public sealed record ThreatRecord(string ThreatId, string Name, int? SeverityId, DateTime DetectedUtc, bool? ActionSuccess, int? ExecutionStatus, bool? IsActive, int ResourceCount)
{
    /// <summary>Still active, or Defender could not take its action. MSFT_MpThreat.IsActive wins when it is known.</summary>
    public bool Active => IsActive ?? ActionSuccess == false;
}

/// <summary>A row of MSFT_MpThreatDetection (resources reduced to their count).</summary>
public sealed record DetectionRow(string ThreatId, DateTime DetectedUtc, bool? ActionSuccess, int? ExecutionStatus, int ResourceCount);

/// <summary>A row of MSFT_MpThreat.</summary>
public sealed record ThreatRow(string ThreatId, string Name, int? SeverityId, bool? IsActive);

/// <summary>An event of the Windows Defender operational log: 1116 detected, 1117 action taken, 1118 and 1119 action failed.</summary>
public sealed record DefenderEventRow(int EventId, DateTime TimeUtc, string? ThreatId, string? ThreatName);

/// <summary>Pure parsing of the Defender threat history (WMI first, the operational event log when WMI is denied).</summary>
public static class ThreatHistory
{
    public const int Days = 30;

    public static IReadOnlyList<ThreatRecord> FromWmi(IEnumerable<DetectionRow> detections, IEnumerable<ThreatRow> threats, DateTime nowUtc)
    {
        var byId = new Dictionary<string, ThreatRow>();
        foreach (var t in threats) byId[t.ThreatId] = t;
        return detections.Where(d => d.DetectedUtc >= nowUtc.AddDays(-Days))
            .Select(d =>
            {
                byId.TryGetValue(d.ThreatId, out var t);
                return new ThreatRecord(d.ThreatId, CleanName(t?.Name), t?.SeverityId, d.DetectedUtc, d.ActionSuccess, d.ExecutionStatus, t?.IsActive, d.ResourceCount);
            })
            .OrderByDescending(r => r.DetectedUtc).ToList();
    }

    /// <summary>
    /// Events of one threat are read in time order: the last of 1117 (action taken), 1118 and 1119 (action failed) decides.
    /// A detection (1116) without any later outcome is still active.
    /// </summary>
    public static IReadOnlyList<ThreatRecord> FromEvents(IEnumerable<DefenderEventRow> events, DateTime nowUtc)
    {
        var result = new List<ThreatRecord>();
        var recent = events.Where(e => e.TimeUtc >= nowUtc.AddDays(-Days) && e.EventId is 1116 or 1117 or 1118 or 1119).OrderBy(e => e.TimeUtc).ToList();
        foreach (var g in recent.GroupBy(e => !string.IsNullOrWhiteSpace(e.ThreatId) ? e.ThreatId! : CleanName(e.ThreatName)))
        {
            var list = g.ToList();
            var detected = list.FirstOrDefault(e => e.EventId == 1116);
            if (detected is null) continue;
            var outcome = list.LastOrDefault(e => e.EventId is 1117 or 1118 or 1119 && e.TimeUtc >= detected.TimeUtc);
            var success = outcome is null ? (bool?)null : outcome.EventId == 1117;
            var name = CleanName(list.Select(e => e.ThreatName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)));
            result.Add(new ThreatRecord(g.Key, name, null, detected.TimeUtc, success, null, success == true ? false : true, 0));
        }
        return result.OrderByDescending(r => r.DetectedUtc).ToList();
    }

    public static IReadOnlyList<ThreatRecord> ActiveThreats(IReadOnlyList<ThreatRecord>? all) =>
        all?.Where(t => t.Active).ToList() ?? new List<ThreatRecord>();

    public static int RemovedCount(IReadOnlyList<ThreatRecord>? all) =>
        all?.Where(t => !t.Active).Select(t => t.ThreatId).Distinct().Count() ?? 0;

    /// <summary>A threat name such as "Trojan:Win32/Wacatac.B!ml". Anything that looks like a path is dropped.</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown threat";
        var n = name.Trim();
        if (Regex.IsMatch(n, @"[A-Za-z]:\\|\\|file:", RegexOptions.IgnoreCase)) return "Unknown threat";
        return n.Length > 80 ? n[..80] : n;
    }

    public static string Names(IEnumerable<ThreatRecord> threats, int max = 3)
    {
        var names = threats.Select(t => t.Name).Distinct().ToList();
        var shown = string.Join(", ", names.Take(max).Select(n => $"\"{n}\""));
        return names.Count > max ? shown + $" and {names.Count - max} more" : shown;
    }
}
