using System.Diagnostics.Eventing.Reader;
using System.Management;

namespace AutoFixScout;

/// <summary>Creates WMI searchers that report "Querying WMI: Class" to the activity feed. One line per query, class name only.</summary>
internal static class Wmi
{
    public static ManagementObjectSearcher Searcher(string wql)
    {
        Activity.QueryingWmi(wql);
        return new ManagementObjectSearcher(wql);
    }

    public static ManagementObjectSearcher Searcher(string scope, string wql)
    {
        Activity.QueryingWmi(wql, scope);
        return new ManagementObjectSearcher(scope, wql);
    }
}

/// <summary>Opens event log readers that report "Reading the System event log: Kernel-Power 41, last 14 days".</summary>
internal static class EventLogs
{
    public static EventLogReader Open(string log, string xpath, TimeSpan? window = null)
    {
        Activity.ReadingEvents(log, xpath, window);
        return new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath));
    }
}

/// <summary>Names the command a probe or repair is about to run. Only the program name and a fixed description are reported, never user data.</summary>
internal static class Commands
{
    /// <summary>"Running netsh wlan show interfaces". The caller passes only fixed words, never names, paths or addresses.</summary>
    public static void Running(string exe, string fixedArgs = "", string? why = null)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(exe ?? "");
        var line = fixedArgs.Length == 0 ? $"Running {name}" : $"Running {name} {fixedArgs}";
        Activity.Report(ActivityKind.Command, why == null ? line : $"{line} ({why})");
    }
}

/// <summary>The analysis steps every flow shares: comparing the readings with the problem rules and preparing the fix steps.</summary>
internal static class Analysis
{
    /// <summary>"Comparing the readings against the problem rules" then "Found 2 problems: GpuDriverResets, GpuResizableBarOff".</summary>
    public static IReadOnlyList<Triage.Finding> Judge(Func<IReadOnlyList<Triage.Finding>> evaluate)
    {
        Activity.Report(ActivityKind.Analyze, "Comparing the readings against the problem rules");
        var findings = evaluate();
        // "LooksNormal" is the all-clear finding, not a problem.
        var problems = findings.Where(f => f.Code != Triage.FindingCode.LooksNormal).ToList();
        Activity.Report(ActivityKind.Analyze, problems.Count == 0
            ? "Found no problems"
            : $"Found {Activity.Count(problems.Count, "problem")}: {string.Join(", ", problems.Take(8).Select(f => f.Code))}{(problems.Count > 8 ? " and more" : "")}");
        return findings;
    }

    /// <summary>"Prepared 3 fix steps" (only when there is at least one).</summary>
    public static IReadOnlyList<Triage.Rung> Plan(IReadOnlyList<Triage.Rung> ladder)
    {
        if (ladder.Count > 0) Activity.Report(ActivityKind.Analyze, $"Prepared {Activity.Count(ladder.Count, "fix step")}, from the least to the most invasive");
        return ladder;
    }
}
