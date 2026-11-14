using System.Text.RegularExpressions;

namespace AutoFixScout;

/// <summary>What kind of work a line of the activity feed describes.</summary>
public enum ActivityKind
{
    EventLog,
    Wmi,
    Registry,
    Command,
    Network,
    Files,
    Driver,
    Analyze,
    Change,
    Load,
}

/// <summary>One line of the activity feed. <see cref="Area"/> is the check that was running ("graphics"), empty outside a check.</summary>
public sealed record ActivityItem(DateTime Time, string Area, ActivityKind Kind, string Text, TimeSpan? Took);

/// <summary>
/// The live feed of what AutoFix Scout is doing: every event log read, WMI query, command, network test and change
/// reports one short line here. The UI shows the lines in a small panel; a person who knows IT can follow them.
/// Report never throws and costs next to nothing, so it is safe to call from any thread.
/// Privacy: lines never carry network names, user names, user folder paths, serial numbers, MAC or IP addresses or file
/// contents. <see cref="Report"/> also replaces the profile folders and the user name as a last safety net.
/// </summary>
public static class Activity
{
    public const int Capacity = 500;

    private static readonly ActivityBuffer Buffer = new(Capacity);
    private static readonly AsyncLocal<string?> CurrentArea = new();

    /// <summary>Raised on the thread that reported. Subscribers must marshal to their own thread; their exceptions are swallowed.</summary>
    public static event Action<ActivityItem>? Reported;

    /// <summary>The area of the check running on this logical thread of execution, or an empty text.</summary>
    public static string Area => CurrentArea.Value ?? "";

    public static void Report(ActivityKind kind, string text, TimeSpan? took = null)
    {
        try
        {
            var item = new ActivityItem(DateTime.Now, Area, kind, Scrub(text), took);
            Buffer.Add(item);
            var handlers = Reported;
            if (handlers == null) return;
            foreach (var h in handlers.GetInvocationList())
                try { ((Action<ActivityItem>)h)(item); } catch (Exception) { /* a broken subscriber must not stop the check */ }
        }
        catch (Exception) { /* reporting never throws */ }
    }

    /// <summary>The last 500 items, oldest first.</summary>
    public static IReadOnlyList<ActivityItem> Recent() => Buffer.Snapshot();

    /// <summary>Empties the buffer (used by tests and when a new session starts).</summary>
    public static void Clear() => Buffer.Clear();

    /// <summary>
    /// Marks the start of a check: every item reported on this logical thread (also across await and Task.Run) carries
    /// <paramref name="area"/>. Disposing reports "Finished the {area} check in 2.4 s" and restores the previous area.
    /// </summary>
    public static IDisposable Scope(string area, bool announce = true)
    {
        var previous = CurrentArea.Value;
        CurrentArea.Value = area;
        if (announce) Report(ActivityKind.Analyze, $"Starting the {area} check");
        return new ScopeToken(area, previous);
    }

    private sealed class ScopeToken : IDisposable
    {
        private readonly string _area;
        private readonly string? _previous;
        private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
        private int _done;

        public ScopeToken(string area, string? previous) { _area = area; _previous = previous; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            var took = System.Diagnostics.Stopwatch.GetElapsedTime(_start);
            Report(ActivityKind.Analyze, $"Finished the {_area} check in {Seconds(took)}", took);
            CurrentArea.Value = _previous;
        }
    }

    /// <summary>"2.4 s" (one decimal, invariant culture).</summary>
    public static string Seconds(TimeSpan t) => $"{t.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s";

    // ---- privacy safety net and shared wording helpers ----

    private static readonly Regex MacAddress = new(@"\b[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(\1[0-9A-Fa-f]{2}){4}\b", RegexOptions.Compiled);

    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly Regex? UserPattern = Environment.UserName.Length >= 3
        ? new Regex(@"(?<![\w])" + Regex.Escape(Environment.UserName) + @"(?![\w])", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        : null;

    internal static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            text = ReplacePath(text, LocalAppData, "%LOCALAPPDATA%");
            text = ReplacePath(text, Profile, "%USERPROFILE%");
            if (UserPattern != null) text = UserPattern.Replace(text, "the signed-in user");
            return MacAddress.Replace(text, "a hardware address");
        }
        catch (Exception) { return text; }
    }

    private static string ReplacePath(string text, string folder, string token) =>
        string.IsNullOrEmpty(folder) ? text : text.Replace(folder, token, StringComparison.OrdinalIgnoreCase);

    /// <summary>"3 events" / "1 event".</summary>
    public static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>Turns an event log XPath filter into short words: "Kernel-Power 41, last 14 days".</summary>
    internal static string DescribeEvents(string filter, TimeSpan? window)
    {
        var providers = Regex.Matches(filter, @"Provider\[@Name='([^']+)'\]").Select(m => m.Groups[1].Value.Replace("Microsoft-Windows-", "")).Distinct().ToList();
        var ids = Regex.Matches(filter, @"EventID=(\d+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        var range = Regex.Match(filter, @"EventID>=(\d+) and EventID<=(\d+)");
        if (range.Success) ids.Add($"{range.Groups[1].Value}-{range.Groups[2].Value}");
        var parts = new List<string>();

        if (providers.Count > 0) parts.Add(providers.Count > 3 ? $"{string.Join(" or ", providers.Take(2))} and {providers.Count - 2} more" : string.Join(" or ", providers));
        if (ids.Count > 0) parts.Add(string.Join("/", ids.Take(6)) + (ids.Count > 6 ? "/..." : ""));
        else if (filter.Contains("Level=", StringComparison.Ordinal)) parts.Add(parts.Count > 0 ? "errors only" : "errors");
        var what = parts.Count == 0 ? "events" : string.Join(" ", parts);
        if (window is { } w) what += $", last {(w.TotalDays >= 1 ? Count((int)Math.Round(w.TotalDays), "day") : Count((int)Math.Round(w.TotalHours), "hour"))}";
        return what;
    }

    /// <summary>Reports "Reading the System event log: ..." for a query the probes are about to run.</summary>
    internal static void ReadingEvents(string log, string filter, TimeSpan? window = null) =>
        Report(ActivityKind.EventLog, $"Reading the {log.Replace("Microsoft-Windows-", "").Replace("/Operational", "")} event log: {DescribeEvents(filter, window)}");

    /// <summary>Reports "Querying WMI: Win32_VideoController" from a WQL text (class name only, never the values).</summary>
    internal static void QueryingWmi(string wql, string? scope = null)
    {
        var m = Regex.Match(wql ?? "", @"\bFROM\s+(\w+)", RegexOptions.IgnoreCase);
        var cls = m.Success ? m.Groups[1].Value : "a class";
        var ns = string.IsNullOrEmpty(scope) || scope.Equals(@"root\cimv2", StringComparison.OrdinalIgnoreCase) ? "" : $" ({scope})";
        Report(ActivityKind.Wmi, $"Querying WMI: {cls}{ns}");
    }

    /// <summary>Reports "Reading HKLM\..." for a registry key.</summary>
    internal static void ReadingRegistry(string hive, string path, string? what = null) =>
        Report(ActivityKind.Registry, what == null ? $"Reading {hive}\\{path}" : $"Reading {hive}\\{path} {what}");
}

/// <summary>A thread-safe ring buffer: keeps the newest <c>capacity</c> items, oldest first when read.</summary>
internal sealed class ActivityBuffer
{
    private readonly object _gate = new();
    private readonly ActivityItem?[] _ring;
    private int _next;
    private int _count;

    public ActivityBuffer(int capacity) => _ring = new ActivityItem?[capacity];

    public void Add(ActivityItem item)
    {
        lock (_gate)
        {
            _ring[_next] = item;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
    }

    public IReadOnlyList<ActivityItem> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<ActivityItem>(_count);
            var start = _count < _ring.Length ? 0 : _next;
            for (var i = 0; i < _count; i++) list.Add(_ring[(start + i) % _ring.Length]!);
            return list;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _next = 0;
            _count = 0;
        }
    }
}
