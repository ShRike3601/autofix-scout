using System.Text.RegularExpressions;
using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Stress;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>
/// The activity feed: ring buffer, scopes, subscribers, privacy and wording. The feed is process-wide and other tests
/// report to it at the same time, so every test here uses its own area name or its own buffer instance.
/// </summary>
public class ActivityTests
{
    private static ActivityItem Item(int n, string area = "t") => new(DateTime.Now, area, ActivityKind.Analyze, $"line {n}", null);

    /// <summary>Items reported on this logical thread of execution while <paramref name="work"/> runs.</summary>
    private static async Task<List<ActivityItem>> Capture(string area, Func<Task> work)
    {
        var got = new List<ActivityItem>();
        void On(ActivityItem i) { if (i.Area == area) lock (got) got.Add(i); }
        Activity.Reported += On;
        try { await work(); }
        finally { Activity.Reported -= On; }
        lock (got) return got.ToList();
    }

    // ---- ring buffer ----

    [Fact]
    public void Buffer_KeepsTheNewest500_OldestFirst()
    {
        var b = new ActivityBuffer(Activity.Capacity);
        for (var i = 0; i < 1200; i++) b.Add(Item(i));
        var all = b.Snapshot();
        Assert.Equal(500, all.Count);
        Assert.Equal("line 700", all[0].Text);
        Assert.Equal("line 1199", all[^1].Text);
        for (var i = 1; i < all.Count; i++) Assert.Equal(int.Parse(all[i - 1].Text[5..]) + 1, int.Parse(all[i].Text[5..]));
    }

    [Fact]
    public void Buffer_UnderCapacity_KeepsEverythingInOrder()
    {
        var b = new ActivityBuffer(Activity.Capacity);
        for (var i = 0; i < 3; i++) b.Add(Item(i));
        Assert.Equal(new[] { "line 0", "line 1", "line 2" }, b.Snapshot().Select(x => x.Text));
        b.Clear();
        Assert.Empty(b.Snapshot());
    }

    [Fact]
    public void Buffer_ParallelAdds_AreThreadSafe_AndKeepPerThreadOrder()
    {
        var b = new ActivityBuffer(Activity.Capacity);
        const int threads = 8, each = 2000;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < each; i++) b.Add(Item(i, $"thread{t}"));
        });
        var all = b.Snapshot();
        Assert.Equal(500, all.Count);
        foreach (var g in all.GroupBy(x => x.Area))
        {
            var seq = g.Select(x => int.Parse(x.Text[5..])).ToList();
            Assert.Equal(seq.OrderBy(x => x), seq); // a thread's own lines never swap
        }
    }

    [Fact]
    public void Activity_Recent_NeverExceedsTheLimit()
    {
        for (var i = 0; i < 700; i++) Activity.Report(ActivityKind.Analyze, "Filling the feed");
        Assert.True(Activity.Recent().Count <= Activity.Capacity);
        Assert.Equal(500, Activity.Capacity);
    }

    [Fact]
    public async Task Activity_ParallelReports_AreNotLost()
    {
        const string area = "parallel-test";
        var got = await Capture(area, async () =>
        {
            using var _ = Activity.Scope(area, announce: false);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++) Activity.Report(ActivityKind.Files, $"Thread {t} line {i}");
            })));
        });
        Assert.Equal(1600, got.Count(x => x.Kind == ActivityKind.Files));
    }

    // ---- scope ----

    [Fact]
    public async Task Scope_CarriesTheArea_ThroughAwaitAndTaskRun()
    {
        const string area = "scope-test";
        var got = await Capture(area, async () =>
        {
            using (Activity.Scope(area))
            {
                Activity.Report(ActivityKind.Registry, "First line");
                await Task.Delay(5);
                Activity.Report(ActivityKind.Wmi, "After the await");
                await Task.Run(() => Activity.Report(ActivityKind.Command, "Inside Task.Run"));
                await Task.Run(async () => { await Task.Yield(); Activity.Report(ActivityKind.Network, "Inside async Task.Run"); });
            }
            Assert.Equal("", Activity.Area);
        });
        var texts = got.Select(x => x.Text).ToList();
        Assert.Contains("First line", texts);
        Assert.Contains("After the await", texts);
        Assert.Contains("Inside Task.Run", texts);
        Assert.Contains("Inside async Task.Run", texts);
        Assert.All(got, x => Assert.Equal(area, x.Area));
    }

    [Fact]
    public async Task Scope_Dispose_ReportsTheFinishedLine_WithTheTime()
    {
        const string area = "finish-test";
        var got = await Capture(area, async () =>
        {
            using (Activity.Scope(area)) await Task.Delay(30);
        });
        Assert.Equal($"Starting the {area} check", got[0].Text);
        var last = got[^1];
        Assert.Matches($@"^Finished the {area} check in \d+\.\d s$", last.Text);
        Assert.NotNull(last.Took);
        Assert.True(last.Took >= TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task Scope_Nested_RestoresTheOuterArea()
    {
        var inner = await Capture("inner-test", async () =>
        {
            using (Activity.Scope("outer-test", announce: false))
            {
                using (Activity.Scope("inner-test", announce: false)) Activity.Report(ActivityKind.Analyze, "Inner work");
                Assert.Equal("outer-test", Activity.Area);
            }
            await Task.CompletedTask;
        });
        Assert.Contains(inner, x => x.Text == "Inner work");
    }

    [Fact]
    public void Scope_DisposedTwice_ReportsOnce()
    {
        var count = 0;
        void On(ActivityItem i) { if (i.Area == "twice-test" && i.Text.StartsWith("Finished")) Interlocked.Increment(ref count); }
        Activity.Reported += On;
        try
        {
            var s = Activity.Scope("twice-test");
            s.Dispose();
            s.Dispose();
        }
        finally { Activity.Reported -= On; }
        Assert.Equal(1, count);
    }

    // ---- subscribers ----

    [Fact]
    public void Subscriber_Exceptions_AreSwallowed_AndOthersStillRun()
    {
        var seen = new List<string>();
        void Bad(ActivityItem i) { if (i.Text == "Subscriber test line") throw new InvalidOperationException("broken subscriber"); }
        void Good(ActivityItem i) { if (i.Text == "Subscriber test line") lock (seen) seen.Add(i.Text); }
        Activity.Reported += Bad;
        Activity.Reported += Good;
        try
        {
            var ex = Record.Exception(() => Activity.Report(ActivityKind.Analyze, "Subscriber test line"));
            Assert.Null(ex);
        }
        finally { Activity.Reported -= Bad; Activity.Reported -= Good; }
        Assert.Single(seen);
    }

    [Fact]
    public void Report_NeverThrows_OnOddInput()
    {
        Assert.Null(Record.Exception(() => Activity.Report(ActivityKind.Analyze, null!)));
        Assert.Null(Record.Exception(() => Activity.Report((ActivityKind)99, "")));
    }

    // ---- fake-state run of a flow ----

    [Fact]
    public async Task HealthFlow_FakeState_ProducesActivityLines()
    {
        const string area = HealthFlow.Topic;
        var got = await Capture(area, () =>
        {
            using var _ = Activity.Scope(area);
            var d = HealthFlow.Diagnose(new HealthState { BlueScreens = 4, UnexpectedShutdowns = 3 });
            Assert.NotEmpty(d.Findings);
            return Task.CompletedTask;
        });
        var texts = got.Select(x => x.Text).ToList();
        Assert.Equal("Starting the health check", texts[0]);
        Assert.Contains("Comparing the readings against the problem rules", texts);
        Assert.Contains(texts, t => Regex.IsMatch(t, @"^Found \d+ problems?: \w+"));
        Assert.Matches(@"^Finished the health check in \d+\.\d s$", texts[^1]);
        Assert.All(got, x => Assert.Equal(area, x.Area));
    }

    [Fact]
    public async Task StressTest_FakeEnvironment_ReportsLoadLines_WithoutOneLinePerReading()
    {
        const string area = "stress-fake-test";
        var reader = new StressFake(); // from StressTests: no-op reader and loads
        var got = await Capture(area, async () =>
        {
            using var _ = Activity.Scope(area);
            var options = new StressOptions { IdleSeconds = 0.2, CpuSeconds = 0.3, GpuSeconds = 0, CooldownSeconds = 0.2, SampleIntervalSeconds = 0.05, CpuThreads = 1 };
            await StressTest.RunAsync(options, null, CancellationToken.None, reader.Env());
        });
        var texts = got.Select(x => x.Text).ToList();
        Assert.Contains(texts, t => t.StartsWith("Starting the load test"));
        Assert.Contains(texts, t => t.StartsWith("Loading the processor"));
        Assert.Contains(texts, t => t.StartsWith("The load test ended"));
        Assert.True(texts.Count < 15, "readings must not be reported one by one");
        Assert.All(got.Where(x => x.Text.StartsWith("Loading") || x.Text.StartsWith("Measuring")), x => Assert.Equal(ActivityKind.Load, x.Kind));
    }

    // ---- privacy ----

    [Fact]
    public async Task Privacy_ProfilePathsAndUserNameAreReplaced()
    {
        const string area = "privacy-path-test";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var got = await Capture(area, () =>
        {
            using var _ = Activity.Scope(area, announce: false);
            Activity.Report(ActivityKind.Files, $"Measuring {Path.Combine(profile, "Downloads")}");
            Activity.Report(ActivityKind.Files, $"Reading {Path.Combine(local, "Temp")}");
            Activity.Report(ActivityKind.Files, $"Reading a file of {Environment.UserName}");
            Activity.Report(ActivityKind.Network, "Adapter 00:1A:2B:3C:4D:5E answered");
            return Task.CompletedTask;
        });
        Assert.Contains(got, x => x.Text == @"Measuring %USERPROFILE%\Downloads");
        Assert.Contains(got, x => x.Text == @"Reading %LOCALAPPDATA%\Temp");
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(got, x => x.Text.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(got, x => x.Text.Contains("00:1A:2B", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Privacy_HelpersNeverCopyValuesFromQueriesOrFilters()
    {
        const string area = "privacy-helper-test";
        const string ssid = "SecretNet";
        var got = await Capture(area, () =>
        {
            using var _ = Activity.Scope(area, announce: false);
            // A WQL text and an XPath filter that happen to hold a network name or a user folder: only classes, providers and ids are reported.
            Activity.QueryingWmi($"SELECT Name FROM Win32_NetworkAdapter WHERE NetConnectionID = '{ssid}'");
            Activity.ReadingEvents("Microsoft-Windows-WLAN-AutoConfig/Operational", $"*[System[Provider[@Name='Microsoft-Windows-WLAN-AutoConfig'] and EventID=8003 and Data='{ssid}']]", TimeSpan.FromDays(3));
            Commands.Running(@"C:\Windows\System32\netsh.exe", "wlan show interfaces", "sign-in type only");
            Activity.ReadingRegistry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", "(proxy settings)");
            // A fake finished check with a network name in the state, as the real triage would see it.
            var d = HealthFlow.Diagnose(new HealthState { UserProxyEnabled = true, UserProxyServer = ssid + ":8080", UserProxyReachable = Tri.No });
            Assert.NotNull(d);
            return Task.CompletedTask;
        });
        Assert.NotEmpty(got);
        Assert.DoesNotContain(got, x => x.Text.Contains(ssid, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(got, x => x.Text == "Querying WMI: Win32_NetworkAdapter");
        Assert.Contains(got, x => x.Text == "Reading the WLAN-AutoConfig event log: WLAN-AutoConfig 8003, last 3 days");
        Assert.Contains(got, x => x.Text == "Running netsh wlan show interfaces (sign-in type only)");
    }

    // ---- wording (docs/WRITING.md) ----

    private static readonly Regex Contraction = new(@"n't\b|\b(I|we|it|that|there|what)'(s|m|d|ll|re|ve)\b|'(re|ve|ll)\b", RegexOptions.IgnoreCase);
    private static readonly Regex FirstPerson = new(@"\b(I|we|my|our|us)\b", RegexOptions.IgnoreCase);

    public static IEnumerable<string> BadWording(string line)
    {
        if (line.Length == 0) yield return "empty";
        else if (!char.IsUpper(line[0]) && !char.IsDigit(line[0])) yield return "not sentence case";
        if (line.Contains('!')) yield return "exclamation mark";
        if (Contraction.IsMatch(line)) yield return "contraction";
        if (FirstPerson.IsMatch(line)) yield return "first person";
        if (line.Length > 220) yield return "too long for one line";
        if (line.EndsWith('.')) yield return "ends with a full stop (feed lines are phrases)";
    }

    [Theory]
    [InlineData("Reading the System event log: Kernel-Power 41, last 14 days")]
    [InlineData("Querying WMI: Win32_VideoController")]
    [InlineData("Running nvidia-smi --query-gpu (temperature, clocks, throttle reasons)")]
    [InlineData("Running elevated helper: network.wifi_stability (Windows asks for permission)")]
    [InlineData("Restoring driver save point")]
    [InlineData("Finished the graphics check in 2.4 s")]
    [InlineData("Found 2 problems: GpuDriverResets, GpuResizableBarOff")]
    public void GoodSampleLines_PassTheWritingGuide(string line) => Assert.Empty(BadWording(line));

    [Theory]
    [InlineData("It's reading the log")]
    [InlineData("We read the log")]
    [InlineData("reading the log")]
    [InlineData("Done!")]
    [InlineData("I could not read it")]
    public void BadSampleLines_AreCaught(string line) => Assert.NotEmpty(BadWording(line));

    [Fact]
    public async Task GeneratedLines_PassTheWritingGuide()
    {
        const string area = "wording-test";
        var got = await Capture(area, async () =>
        {
            using var _ = Activity.Scope(area);
            HealthFlow.Diagnose(new HealthState { BlueScreens = 4, UnexpectedShutdowns = 3 });
            Activity.ReadingEvents("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41", TimeSpan.FromDays(14));
            Activity.QueryingWmi("SELECT Name FROM Win32_VideoController");
            Activity.QueryingWmi(@"SELECT Active FROM WmiMonitorBrightness", @"root\wmi");
            await Task.CompletedTask;
        });
        foreach (var x in got) Assert.True(!BadWording(x.Text).Any(), $"\"{x.Text}\": {string.Join(", ", BadWording(x.Text))}");
    }

    /// <summary>Every text literal passed to the feed in the source follows the guide (no contractions, no "I" or "we", no "!").</summary>
    [Fact]
    public void SourceLiterals_FollowTheWritingGuide()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "AutoFixScout.sln"))) dir = Path.GetDirectoryName(dir);
        if (dir == null) return; // not running from a checkout
        var src = Path.Combine(dir, "src", "AutoFixScout");
        var call = new Regex(@"(Activity\.Report|Activity\.ReadingRegistry|Commands\.Running)\(([^;]*)\);", RegexOptions.Singleline);
        var literal = new Regex(@"\$?@?""((?:[^""\\]|\\.)*)""");
        var checkedCount = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
            foreach (Match m in call.Matches(File.ReadAllText(file)))
                foreach (Match l in literal.Matches(m.Groups[2].Value))
                {
                    var text = l.Groups[1].Value;
                    if (text.Length < 12 || !text.Contains(' ')) continue; // ids, hive names, keys
                    checkedCount++;
                    Assert.False(Contraction.IsMatch(text) || FirstPerson.IsMatch(text) || text.Contains('!'),
                        $"{Path.GetFileName(file)}: \"{text}\"");
                }
        }
        Assert.True(checkedCount > 50, $"only {checkedCount} literals found; the scan is broken");
    }

    // ---- helpers for the stress fake ----

    private sealed class StressFake
    {
        private sealed class Reader : IStressReader
        {
            public bool HasBattery => false;
            public Task<NvidiaReading?> ProbeGpuAsync() => Task.FromResult<NvidiaReading?>(null);
            public Task<StressSnapshot> ReadAsync() => Task.FromResult(new StressSnapshot(100, 100, false, null));
            public void Dispose() { }
        }

        private sealed class Load : ILoad
        {
            public void Start() { }
            public bool Stop(TimeSpan wait) => true;
            public void Dispose() { }
        }

        public StressEnvironment Env() => new(() => new Reader(), (n, ct) => new Load(), () => null);
    }
}

/// <summary>
/// Reads the real machine, read-only (AFS_LIVE set to a file path): graphics, performance and network checks. Writes every
/// activity line to that file and proves that neither the Wi-Fi network name nor the user name appears in it.
/// </summary>
[Collection("Live machine")]
public class LiveActivityTests
{
    [Fact]
    public async Task Activity_LiveChecks_WriteLines_AndLeakNothingPrivate()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var ssid = WifiNameForTest(); // held in memory only, to prove it never reaches the feed
        var lines = new List<string>();
        var items = new List<ActivityItem>();
        void On(ActivityItem i) { lock (items) items.Add(i); }
        Activity.Reported += On;
        try
        {
            await GraphicsFlow.DiagnoseAsync();
            await PerfFlow.DiagnoseAsync();
            await NetworkFlow.DiagnoseAsync();
        }
        finally { Activity.Reported -= On; }
        lock (items)
            foreach (var i in items)
                lines.Add($"{i.Time:HH:mm:ss.fff} [{i.Area}] {i.Kind,-8} {i.Text}");
        File.WriteAllLines(output, lines);

        Assert.True(lines.Count > 30, $"only {lines.Count} lines");
        Assert.Contains(items, i => i.Area == "graphics");
        Assert.Contains(items, i => i.Area == "performance");
        Assert.Contains(items, i => i.Area == "network");
        Assert.Contains(items, i => i.Text.StartsWith("Finished the network check in"));
        if (!string.IsNullOrEmpty(ssid)) Assert.DoesNotContain(items, i => i.Text.Contains(ssid, StringComparison.OrdinalIgnoreCase));
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(items, i => i.Text.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Text.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase));
        Assert.All(items.Where(i => i.Area is "graphics" or "performance" or "network"), i => Assert.Empty(ActivityTests.BadWording(i.Text).Where(w => w != "ends with a full stop (feed lines are phrases)")));
    }

    private static string? WifiNameForTest()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        var m = Regex.Match(text, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }
}
