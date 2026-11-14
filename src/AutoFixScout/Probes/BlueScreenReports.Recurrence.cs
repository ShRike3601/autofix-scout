namespace AutoFixScout.Probes;

public static partial class BlueScreenReports
{
    /// <summary>
    /// How often the blue screens recur. Days = different calendar days; KeepsHappening = three or more in all, or two
    /// on two different days; SameCodeEachTime = one stop code, seen at least twice.
    /// </summary>
    public sealed record Recurrence(int Total, int Days, bool SameCodeEachTime, bool KeepsHappening);

    public static Recurrence Recur(int total, IEnumerable<DateTime> times, IReadOnlyList<StopCodeCount> codes)
    {
        var days = times.Select(t => t.Date).Distinct().Count();
        var same = codes.Count == 1 && codes[0].Count >= 2;
        var keeps = total >= 3 || (total >= 2 && days >= 2);
        return new Recurrence(total, days, same, keeps);
    }
}
