using System.Globalization;
using System.Text.Json;
using AutoFixScout.Areas;
using AutoFixScout.Triage;
using AutoFixScout.Updates;

namespace AutoFixScout.Weekly;

/// <summary>What weekly.json keeps: the date, how many problems, and the finding codes. No names, no paths, no details.</summary>
public sealed record WeeklyResult(string Date, int Problems, string[] Codes);

public static class WeeklyCheckup
{
    public const string ToastTitle = "AutoFix Scout found something to check";
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(100);

    public static string FileIn(string dir) => Path.Combine(dir, "weekly.json");

    /// <summary>Findings that are a real problem: neither an all-clear result nor plain information.</summary>
    public static int CountProblems(IEnumerable<FindingCode> codes) =>
        codes.Count(c => !LadderRunner.IsAllClear(c) && !LadderRunner.IsInformation(c));

    public static bool ShouldNotify(IEnumerable<FindingCode> codes) => CountProblems(codes) > 0;

    public static string ToastBody(int problems) => problems == 1
        ? "The weekly check-up found 1 thing that needs attention. Open AutoFix Scout to see it."
        : $"The weekly check-up found {problems} things that need attention. Open AutoFix Scout to see them.";

    public static WeeklyResult Summarize(IReadOnlyList<FindingCode> codes, DateTime today) =>
        new(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CountProblems(codes),
            codes.Select(c => c.ToString()).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray());

    /// <summary>"Last check-up: 10 Nov, no problems found" (no year, the date is recent).</summary>
    public static string Describe(WeeklyResult? r)
    {
        if (r is null || !DateTime.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return "No check-up has run yet.";
        var day = d.ToString("d MMM", CultureInfo.InvariantCulture);
        return r.Problems switch
        {
            0 => $"Last check-up: {day}, no problems found",
            1 => $"Last check-up: {day}, 1 problem found",
            _ => $"Last check-up: {day}, {r.Problems} problems found",
        };
    }

    public static void Save(WeeklyResult r, string? dir = null)
    {
        dir ??= AppPaths.DataDir;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FileIn(dir), JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static WeeklyResult? Load(string? dir = null)
    {
        try
        {
            var f = FileIn(dir ?? AppPaths.DataDir);
            return File.Exists(f) ? JsonSerializer.Deserialize<WeeklyResult>(File.ReadAllText(f)) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The headless run: read-only diagnosis, one line into weekly.json, a notification only when something needs attention.
    /// Never throws; gives up after <see cref="TimeLimit"/>. Returns the number of problems, or -1 when the check did not finish.
    /// </summary>
    public static async Task<int> RunAsync(Action<string, string> notify, Func<Task<IReadOnlyList<Finding>>>? diagnose = null,
        string? dir = null, DateTime? today = null, TimeSpan? limit = null)
    {
        try
        {
            diagnose ??= async () => (await HealthFlow.DiagnoseAsync()).Findings;
            var work = diagnose();
            if (await Task.WhenAny(work, Task.Delay(limit ?? TimeLimit)) != work) return -1;
            var codes = (await work).Select(f => f.Code).ToList();
            var result = Summarize(codes, today ?? DateTime.Now);
            try { Save(result, dir); } catch (Exception) { }
            if (ShouldNotify(codes))
                try { notify(ToastTitle, ToastBody(result.Problems)); } catch (Exception) { }
            return result.Problems;
        }
        catch (Exception) { return -1; }
    }
}
