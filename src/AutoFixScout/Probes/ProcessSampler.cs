using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoFixScout.Probes;

/// <summary>
/// Read-only: which programs use the processor and memory right now. Processor time is sampled twice, about two
/// seconds apart; processes of one program are added together. Windows' own processes are left out, and so is
/// AutoFix Scout. Nothing is ever closed or changed here.
/// </summary>
public static class ProcessSampler
{
    /// <summary>Core Windows processes that are never named to the person, even when their path cannot be read.</summary>
    private static readonly HashSet<string> CoreWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "LsaIso", "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "conhost",
        "spoolsv", "audiodg", "SearchIndexer", "SearchHost", "SearchProtocolHost", "SearchFilterHost", "StartMenuExperienceHost",
        "ShellExperienceHost", "TextInputHost", "WmiPrvSE", "dllhost", "MsMpEng", "NisSrv", "MpDefenderCoreService",
        "SecurityHealthService", "SecurityHealthSystray", "smartscreen", "TiWorker", "TrustedInstaller", "MoUsoCoreWorker",
        "usocoreworker", "wuauclt", "CompPkgSrv", "WUDFHost", "dasHost", "lsm", "SgrmBroker", "MsMpEngCP", "Widgets", "WidgetService",
        "ApplicationFrameHost", "SystemSettings", "LockApp", "backgroundTaskHost", "AggregatorHost", "WmiApSrv", "vmmem", "vmmemWSL",
        "AutoFixScout", "AutoFixScout.UI", "AutoFixScout.Executor", "testhost",
    };

    /// <summary>Part of Windows: a known core name, or a program under the Windows folder.</summary>
    public static bool IsWindowsProcess(string name, string? path, string windowsDir) =>
        CoreWindows.Contains(name)
        || (path is not null && path.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Per-process sample, before grouping.</summary>
    public sealed record ProcSample(int Pid, string Name, string? Path, TimeSpan CpuDelta, long WorkingSet);

    /// <summary>
    /// Groups processes by program (path, else name), turns CPU time into percent of all cores over the interval and
    /// memory into percent of installed memory. Windows' own processes and processes with no readable path are dropped
    /// (a process that hides its path from a normal user is usually a service or security software, never offered).
    /// Returns the busiest few by processor and by memory.
    /// </summary>
    public static IReadOnlyList<ProcessUsage> Summarise(IEnumerable<ProcSample> samples, TimeSpan interval, int processors,
        long? totalMemory, string windowsDir, int top = 5)
    {
        var wall = Math.Max(1, interval.TotalMilliseconds) * Math.Max(1, processors);
        var programs = samples
            .Where(s => s.Path is not null && !IsWindowsProcess(s.Name, s.Path, windowsDir))
            .GroupBy(s => s.Path!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cpu = 100.0 * g.Sum(s => s.CpuDelta.TotalMilliseconds) / wall;
                var ws = g.Sum(s => s.WorkingSet);
                var mem = totalMemory is > 0 ? 100.0 * ws / totalMemory.Value : 0;
                return new ProcessUsage(g.First().Name, g.Key, Math.Round(Math.Min(100, cpu), 1), ws, Math.Round(mem, 1));
            })
            .ToList();
        return programs.OrderByDescending(p => p.CpuPercent).Take(top)
            .Concat(programs.OrderByDescending(p => p.WorkingSetBytes).Take(top))
            .DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<ProcessUsage> Sample(TimeSpan interval, long? totalMemory)
    {
        Activity.Report(ActivityKind.Load, $"Sampling processor use of running programs for {Activity.Seconds(interval)} (program names stay on this computer)");
        var first = new Dictionary<int, TimeSpan>();
        foreach (var p in Process.GetProcesses())
            using (p)
                if (CpuTime(p) is { } t) first[p.Id] = t;
        var sw = Stopwatch.StartNew();
        Thread.Sleep(interval);
        var samples = new List<ProcSample>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id is 0 or 4 || !first.TryGetValue(p.Id, out var before) || CpuTime(p) is not { } after) continue;
                    samples.Add(new ProcSample(p.Id, p.ProcessName, ImagePath(p.Id), after - before, p.WorkingSet64));
                }
                catch (Exception) { /* exited meanwhile */ }
            }
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Activity.Report(ActivityKind.Analyze, $"Grouping {Activity.Count(samples.Count, "process")} by program and leaving out Windows itself");
        // The name a person recognises is read here, so the triage stays pure.

        return Summarise(samples, sw.Elapsed, Environment.ProcessorCount, totalMemory, windows)
            .Select(u => u with { Name = FriendlyName(u) }).ToList();
    }

    private static TimeSpan? CpuTime(Process p)
    {
        try { return p.TotalProcessorTime; } catch (Exception) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>Full program path with the least access right (works for most processes without admin); null if refused.</summary>
    private static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The name a person recognises: the program's description ("Google Chrome"), else its file name.</summary>
    private static string FriendlyName(ProcessUsage p)
    {
        try
        {
            if (p.Path is not null && FileVersionInfo.GetVersionInfo(p.Path).FileDescription is { Length: > 0 } d) return d.Trim();
        }
        catch (Exception) { }
        return p.Name;
    }
}
