using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>The two read-only elevated actions: diag.read_boot_times and diag.read_crash_reports. None of these tests runs the helper.</summary>
[Collection("ElevatedDiagnosticsCache")]
public class ElevatedReadTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    // ---- the executor refuses any argument ----

    [Theory]
    [InlineData("diag.read_boot_times", "x")]
    [InlineData("diag.read_boot_times", "system")]
    [InlineData("diag.read_boot_times", "C:\\Windows")]
    [InlineData("diag.read_crash_reports", "x")]
    [InlineData("diag.read_crash_reports", "system")]
    public void Executor_OneArgument_IsRejected(string action, string arg) =>
        Assert.Equal(2, AutoFixScout.Executor.Program.Main(new[] { action, arg }));

    [Theory]
    [InlineData("diag.read_boot_times")]
    [InlineData("diag.read_crash_reports")]
    public void Executor_ManyArguments_AreRejected(string action)
    {
        Assert.Equal(2, AutoFixScout.Executor.Program.Main(new[] { action, "a", "b" }));
        Assert.Equal(2, AutoFixScout.Executor.Program.Main(new[] { action, "a", "b", "c" }));
        Assert.Equal(2, AutoFixScout.Executor.Program.Main(new[] { action, "" }));
    }

    [Fact]
    public void BothActions_AreInElevatedActions_AndUseAFixedFile()
    {
        Assert.Contains("diag.read_boot_times", FixActions.ElevatedActions);
        Assert.Contains("diag.read_crash_reports", FixActions.ElevatedActions);
        Assert.True(DiagResults.IsDiagAction("diag.read_boot_times"));
        Assert.True(DiagResults.IsDiagAction("diag.read_crash_reports"));
        Assert.False(DiagResults.IsDiagAction("repair.sfc"));
        Assert.Equal("diag-boot-times.json", Path.GetFileName(DiagResults.FileFor("diag.read_boot_times")));
        Assert.Equal("diag-crash-reports.json", Path.GetFileName(DiagResults.FileFor("diag.read_crash_reports")));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoFixScout", "logs"), DiagResults.Folder);
        Assert.Throws<ArgumentException>(() => DiagResults.FileFor("repair.sfc"));
    }

    // ---- the result parser ----

    private static DiagBootResult SampleBoots() => DiagResults.CleanBoots(
        new[] { new BootRecord(Now.AddDays(-1), 90000, 70000, 15000), new BootRecord(Now.AddDays(-3), 80000, 65000, 12000) },
        new[] { new SlowStartupItem("OneDrive.exe", 21000), new SlowStartupItem("Updater", 9000) }, Now);

    [Fact]
    public void Boots_RoundTrip()
    {
        var r = DiagResults.ParseBoots(DiagResults.Serialize(SampleBoots()), Now);
        Assert.NotNull(r);
        Assert.Equal(2, r!.Boots.Count);
        Assert.Equal(70000, r.Boots[0].MainPathMs);
        Assert.Equal(new[] { "OneDrive.exe", "Updater" }, r.SlowItems.Select(x => x.Name));
    }

    [Fact]
    public void Crashes_RoundTrip()
    {
        var c = DiagResults.CleanCrashes(new[] { new CrashReport("BlueScreen", 0x133, "nvlddmkm.sys", Now.AddDays(-2)) }, Now);
        var r = DiagResults.ParseCrashes(DiagResults.Serialize(c), Now);
        Assert.NotNull(r);
        var one = Assert.Single(r!.Reports);
        Assert.Equal(0x133u, one.Code);
        Assert.Equal("nvlddmkm.sys", one.Module);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"Boots\": [")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":99,\"Action\":\"diag.read_boot_times\"}")]
    [InlineData("{\"Version\":1,\"Action\":\"diag.read_crash_reports\"}")]
    [InlineData("{\"Version\":1,\"Action\":\"diag.read_boot_times\",\"Boots\":[{\"TimeUtc\":\"yesterday\"}]}")]
    public void Boots_MalformedOrMissing_IsNull(string? json) => Assert.Null(DiagResults.ParseBoots(json, Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"Version\":1,\"Action\":\"diag.read_boot_times\"}")]
    [InlineData("garbage")]
    [InlineData("{\"Version\":1,\"Action\":\"diag.read_crash_reports\",\"Reports\":[null,{\"EventType\":null}]}")]
    public void Crashes_MalformedOrMissing_IsNullOrEmpty(string? json)
    {
        var r = DiagResults.ParseCrashes(json, Now);
        Assert.True(r is null || r.Reports.Count == 0);
    }

    [Fact]
    public void TooBigText_IsRefused()
    {
        var big = new string(' ', DiagResults.MaxChars + 1) + DiagResults.Serialize(SampleBoots());
        Assert.Null(DiagResults.ParseBoots(big, Now));
    }

    [Fact]
    public void Validation_DropsImpossibleValues_AndCapsTheCounts()
    {
        var boots = new List<BootRecord>
        {
            new(Now.AddDays(-1), 90000, 70000, 1),
            new(Now.AddDays(-1), -5, 70000, 1),                  // negative
            new(Now.AddDays(-1), 999_999_999_999, 1, 1),         // longer than a day
            new(Now.AddDays(-90), 90000, 70000, 1),              // too old
            new(Now.AddDays(+10), 90000, 70000, 1),              // in the future
        };
        for (var i = 0; i < 40; i++) boots.Add(new(Now.AddHours(-i - 30), 50000, 40000, 1));
        var r = DiagResults.CleanBoots(boots, Enumerable.Range(0, 500).Select(i => new SlowStartupItem("item" + i, i)), Now);
        Assert.Equal(DiagResults.MaxBoots, r.Boots.Count);
        Assert.All(r.Boots, b => Assert.InRange(b.TimeUtc, Now.AddDays(-31), Now.AddDays(1)));
        Assert.All(r.Boots, b => Assert.True(b.BootMs is >= 0 and <= DiagResults.MaxDurationMs));
        Assert.Equal(Now.AddDays(-1), r.Boots[0].TimeUtc); // newest first
        Assert.Equal(DiagResults.MaxSlowItems, r.SlowItems.Count);
        Assert.Equal("item499", r.SlowItems[0].Name); // slowest first
    }

    [Fact]
    public void Validation_CrashReports_OnlyKernelTypes_RecentOnly()
    {
        var r = DiagResults.CleanCrashes(new[]
        {
            new CrashReport("BlueScreen", 0x50, "win32k.sys", Now.AddDays(-1)),
            new CrashReport("LiveKernelEvent", 0x141, null, Now.AddDays(-2)),
            new CrashReport("APPCRASH", 0xC0000005, "app.dll", Now.AddDays(-1)),   // not a kernel report
            new CrashReport("BlueScreen", 0x50, "old.sys", Now.AddDays(-80)),      // too old
            new CrashReport("BlueScreen", 0xFFFFFFFF, "x.sys", Now.AddDays(-1)),   // not a stop code
        }, Now);
        Assert.Equal(new[] { "win32k.sys", null, "x.sys" }, r.Reports.Select(x => x.Module));
        Assert.Null(r.Reports[2].Code);
    }

    // ---- the privacy filter ----

    [Theory]
    [InlineData(@"C:\Users\Bob\AppData\Local\Foo\foo.exe")]
    [InlineData(@"c:\users\bob\foo.exe")]
    [InlineData("C:/Users/Bob/foo.exe")]
    [InlineData(@"D:\Backup\Users\Bob\x.sys")]
    [InlineData("foo\u0000.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Privacy_DropsAnythingNamingAUserFolder(string? name) => Assert.Null(DiagResults.CleanName(name));

    [Theory]
    [InlineData("OneDrive.exe", "OneDrive.exe")]
    [InlineData("Microsoft Teams", "Microsoft Teams")]
    [InlineData(@"C:\Windows\System32\drivers\nvlddmkm.sys", "nvlddmkm.sys")]
    [InlineData("\"C:\\Program Files\\Vendor\\app.exe\"", "app.exe")]
    public void Privacy_KeepsOnlyTheFileOrFriendlyName(string name, string expected) => Assert.Equal(expected, DiagResults.CleanName(name));

    [Fact]
    public void Privacy_AppliesToBothResultFiles_AndSurvivesTheJsonRoundTrip()
    {
        // The filter also runs on the app side, so a file written by anything else still cannot carry a user name.
        var json = "{\"Version\":1,\"Action\":\"diag.read_boot_times\",\"Boots\":[],\"SlowItems\":[" +
                   "{\"Name\":\"C:\\\\Users\\\\Bob\\\\AppData\\\\x.exe\",\"TotalMs\":5000},{\"Name\":\"Good.exe\",\"TotalMs\":4000}]}";
        var r = DiagResults.ParseBoots(json, Now)!;
        Assert.Equal(new[] { "Good.exe" }, r.SlowItems.Select(x => x.Name));
        Assert.DoesNotContain("Users", DiagResults.Serialize(r));

        var cj = "{\"Version\":1,\"Action\":\"diag.read_crash_reports\",\"Reports\":[{\"EventType\":\"BlueScreen\",\"Code\":19,\"Module\":\"C:\\\\Users\\\\Bob\\\\evil.sys\",\"WhenUtc\":\"2026-11-13T00:00:00Z\"}]}";
        var c = DiagResults.ParseCrashes(cj, Now)!;
        Assert.Null(Assert.Single(c.Reports).Module);
        Assert.DoesNotContain("Bob", DiagResults.Serialize(c));
    }

    // ---- finishing a run: read, validate, delete ----

    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "afs-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Finish_Boots_ReadsValidatesAndDeletesTheFile()
    {
        var dir = Temp();
        try
        {
            ElevatedDiagnostics.Reset();
            var file = Path.Combine(dir, DiagResults.BootFile);
            File.WriteAllText(file, DiagResults.Serialize(DiagResults.CleanBoots(
                new[] { new BootRecord(DateTime.UtcNow.AddDays(-1), 90000, 70000, 15000) }, new[] { new SlowStartupItem("OneDrive.exe", 21000) }, DateTime.UtcNow)));
            Activity.Clear();
            var (ok, detail) = ElevatedDiagnostics.Finish(DiagResults.BootAction, 0, dir);
            Assert.True(ok);
            Assert.False(File.Exists(file));
            Assert.Single(ElevatedDiagnostics.Boots!.Boots);
            Assert.Contains(Activity.Recent(), a => a.Text == "Read 1 boot");
            Assert.Contains("nothing was changed", detail);
        }
        finally { ElevatedDiagnostics.Reset(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Finish_MissingOrBrokenFile_IsAFailure_AndStillDeletes()
    {
        var dir = Temp();
        try
        {
            ElevatedDiagnostics.Reset();
            Assert.False(ElevatedDiagnostics.Finish(DiagResults.BootAction, 0, dir).Ok); // missing
            File.WriteAllText(Path.Combine(dir, DiagResults.CrashFile), "{ broken");
            Assert.False(ElevatedDiagnostics.Finish(DiagResults.CrashAction, 0, dir).Ok);
            Assert.False(File.Exists(Path.Combine(dir, DiagResults.CrashFile)));
            Assert.Null(ElevatedDiagnostics.Boots);
            Assert.Null(ElevatedDiagnostics.Crashes);
        }
        finally { ElevatedDiagnostics.Reset(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Finish_ExitThree_IsAKnownEmptyResult_OtherCodesAreNot()
    {
        var dir = Temp();
        try
        {
            ElevatedDiagnostics.Reset();
            File.WriteAllText(Path.Combine(dir, DiagResults.BootFile), "stale");
            Assert.True(ElevatedDiagnostics.Finish(DiagResults.BootAction, 3, dir).Ok);
            Assert.False(File.Exists(Path.Combine(dir, DiagResults.BootFile)));
            Assert.Empty(ElevatedDiagnostics.Boots!.Boots);
            ElevatedDiagnostics.Reset();
            Assert.False(ElevatedDiagnostics.Finish(DiagResults.CrashAction, 1, dir).Ok);
            Assert.False(ElevatedDiagnostics.Finish(DiagResults.CrashAction, 2, dir).Ok);
            Assert.Null(ElevatedDiagnostics.Crashes);
        }
        finally { ElevatedDiagnostics.Reset(); Directory.Delete(dir, true); }
    }

    // ---- merging into the states, and the findings that follow ----

    private static StorageState Storage() => new()
    {
        FreeBytes = 120L << 30, TotalBytes = 256L << 30, ReclaimableTempBytes = 0, RecycleBinBytes = 0, StartupAppCount = 0,
        StartupEntries = Array.Empty<StartupEntry>(), TopProcesses = Array.Empty<ProcessUsage>(),
        Uptime = TimeSpan.FromDays(1), MemoryLoadPercent = 40, ProblemDevices = Array.Empty<StorageDeviceInfo>(),
    };

    private static DiagBootResult SlowBoots() => DiagResults.CleanBoots(
        new[] { new BootRecord(Now.AddDays(-1), 180000, 120000, 20000), new BootRecord(Now.AddDays(-2), 170000, 110000, 20000), new BootRecord(Now.AddDays(-3), 160000, 100000, 20000) },
        new[] { new SlowStartupItem("OneDrive.exe", 40000) }, Now);

    [Fact]
    public void MergeBoots_FillsUnknown_AndTheStartupFindingAppears()
    {
        var s = Storage();
        Assert.Null(s.BootTimes);
        Assert.DoesNotContain(StorageTriage.Evaluate(s), f => f.Code == FindingCode.StorageSlowStartup);
        ElevatedDiagnostics.Merge(s, SlowBoots());
        Assert.Equal(3, s.BootTimes!.Count);
        Assert.Equal("OneDrive.exe", s.SlowStartupItems![0].Name);
        var findings = StorageTriage.Evaluate(s);
        var slow = Assert.Single(findings, f => f.Code == FindingCode.StorageSlowStartup);
        Assert.Contains("\"OneDrive.exe\"", slow.PlainMessage);
    }

    [Fact]
    public void MergeBoots_NeverReplacesWhatTheAppAlreadyRead()
    {
        var s = Storage();
        s.BootTimes = new[] { new BootRecord(Now, 30000, 20000, 1000) };
        s.SlowStartupItems = Array.Empty<SlowStartupItem>();
        ElevatedDiagnostics.Merge(s, SlowBoots());
        Assert.Single(s.BootTimes);
        Assert.Empty(s.SlowStartupItems);
    }

    [Fact]
    public void MergeBoots_EmptyResult_MakesBootTimesKnownButEmpty()
    {
        var s = Storage();
        ElevatedDiagnostics.Merge(s, DiagResults.CleanBoots(null, null, Now));
        Assert.NotNull(s.BootTimes);
        Assert.Empty(s.BootTimes!);
        Assert.DoesNotContain(StorageTriage.Ladder(s, StorageTriage.Evaluate(s)), r => r.Id == ReadRungs.BootId);
    }

    private static HealthState Health(int blueScreens)
    {
        var s = new HealthState
        {
            ClockOffsetSeconds = 2,
            Services = ServiceDefaults.All.Select(d => new UpdateServiceInfo(d.Name, d.ShouldRun ? "Running" : "Stopped", d.Start.ToString())).ToList(),
            RebootPending = Tri.No,
            BlueScreens = blueScreens,
        };
        if (blueScreens > 0)
        {
            var t = new DateTime(2026, 11, 8, 10, 0, 0);
            s.BlueScreenTimes = Enumerable.Range(0, blueScreens).Select(i => t.AddDays(i)).ToList();
            s.StopCodes = new[] { new StopCodeCount(0x116, blueScreens) };
        }
        return s;
    }

    private static DiagCrashResult Nvidia() => DiagResults.CleanCrashes(new[]
    {
        new CrashReport("BlueScreen", 0x116, "nvlddmkm.sys", Now.AddDays(-1)),
        new CrashReport("BlueScreen", 0x116, "nvlddmkm.sys", Now.AddDays(-3)),
        new CrashReport("LiveKernelEvent", 0x141, "dxgkrnl.sys", Now.AddDays(-4)),   // not a blue screen: no module counted
    }, Now);

    [Fact]
    public void MergeCrashes_FillsModules_AndTheFindingNamesTheDriver()
    {
        var s = Health(2);
        var before = HealthTriage.Evaluate(s).First(f => f.Code == FindingCode.HealthBlueScreens).PlainMessage;
        Assert.DoesNotContain("NVIDIA", before);
        ElevatedDiagnostics.Merge(s, Nvidia());
        var m = Assert.Single(s.BlueScreenModules);
        Assert.Equal(("nvlddmkm.sys", 2), (m.Module, m.Count));
        Assert.True(s.CrashReportsRead);
        var after = HealthTriage.Evaluate(s).First(f => f.Code == FindingCode.HealthBlueScreens).PlainMessage;
        Assert.Contains("NVIDIA", after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void MergeCrashes_KeepsModulesAlreadyRead_AndEmptyResultMarksRead()
    {
        var s = Health(2);
        s.BlueScreenModules = new[] { new ModuleCount("storport.sys", 1) };
        ElevatedDiagnostics.Merge(s, Nvidia());
        Assert.Equal("storport.sys", Assert.Single(s.BlueScreenModules).Module);
        var t = Health(2);
        ElevatedDiagnostics.Merge(t, DiagResults.CleanCrashes(null, Now));
        Assert.Empty(t.BlueScreenModules);
        Assert.True(t.CrashReportsRead);
    }

    [Fact]
    public void Probes_ApplyWhatTheHelperReadEarlier()
    {
        var dir = Temp();
        try
        {
            ElevatedDiagnostics.Reset();
            var s = Storage();
            ElevatedDiagnostics.ApplyTo(s);
            Assert.Null(s.BootTimes); // nothing read yet: stays Unknown
            File.WriteAllText(Path.Combine(dir, DiagResults.BootFile), DiagResults.Serialize(DiagResults.CleanBoots(
                new[] { new BootRecord(DateTime.UtcNow.AddDays(-1), 180000, 120000, 20000) }, null, DateTime.UtcNow)));
            ElevatedDiagnostics.Finish(DiagResults.BootAction, 0, dir);
            ElevatedDiagnostics.ApplyTo(s);
            Assert.Single(s.BootTimes!);
        }
        finally { ElevatedDiagnostics.Reset(); Directory.Delete(dir, true); }
    }

    // ---- when the rungs are offered ----

    [Fact]
    public void StartupRung_OnlyWhenBootTimesAreUnknown()
    {
        var s = Storage();
        var unknown = StorageTriage.Ladder(s, StorageTriage.Evaluate(s));
        var rung = Assert.Single(unknown, r => r.ActionId == "diag.read_boot_times");
        Assert.Equal(1, rung.Tier);
        Assert.Null(rung.Arg);
        Assert.Equal(ReadRungs.BootId, rung.Id);

        s.BootTimes = Array.Empty<BootRecord>();
        Assert.DoesNotContain(StorageTriage.Ladder(s, StorageTriage.Evaluate(s)), r => r.ActionId == "diag.read_boot_times");
        s.BootTimes = new[] { new BootRecord(Now, 30000, 20000, 1000) };
        Assert.DoesNotContain(StorageTriage.Ladder(s, StorageTriage.Evaluate(s)), r => r.ActionId == "diag.read_boot_times");
    }

    [Fact]
    public void StartupRung_NotOfferedWhenTheCheckCouldNotBeCompleted()
    {
        var s = Storage();
        s.Uptime = null; // fail closed: an incomplete check offers no step
        var f = StorageTriage.Evaluate(s);
        Assert.Contains(f, x => x.Code == FindingCode.ProbeIncomplete);
        Assert.Empty(StorageTriage.Ladder(s, f));
    }

    [Fact]
    public void CrashRung_OnlyWhenBlueScreensExistAndNoModuleIsKnown()
    {
        var none = Health(0);
        Assert.DoesNotContain(HealthTriage.Ladder(none, HealthTriage.Evaluate(none)), r => r.ActionId == "diag.read_crash_reports");

        var s = Health(2);
        var rung = Assert.Single(HealthTriage.Ladder(s, HealthTriage.Evaluate(s)), r => r.ActionId == "diag.read_crash_reports");
        Assert.Equal(1, rung.Tier);
        Assert.Null(rung.Arg);
        Assert.Equal(ReadRungs.CrashId, rung.Id);

        s.BlueScreenModules = new[] { new ModuleCount("nvlddmkm.sys", 2) };
        Assert.DoesNotContain(HealthTriage.Ladder(s, HealthTriage.Evaluate(s)), r => r.ActionId == "diag.read_crash_reports");

        var read = Health(2);
        read.CrashReportsRead = true; // read already, and Windows named nothing: not asked again
        Assert.DoesNotContain(HealthTriage.Ladder(read, HealthTriage.Evaluate(read)), r => r.ActionId == "diag.read_crash_reports");
    }

    [Fact]
    public void ReadRungs_AreNeverStartedAutomatically_AndSkipTheFixTest()
    {
        // The runner asks first (ConfirmAsync) and, after a read-only step, does not ask "is it fixed?".
        var host = new Host(confirm: false);
        Assert.Equal(Outcome.Abandoned, LadderRunner.RunAsync(new[] { ReadRungs.StartupTimes() }, host, new()).GetAwaiter().GetResult());
        Assert.Empty(host.Ran);

        var yes = new Host(confirm: true);
        var outcome = LadderRunner.RunAsync(new[] { ReadRungs.CrashReports(), ReadRungs.StartupTimes() }, yes, new()).GetAwaiter().GetResult();
        Assert.Equal(Outcome.NotFixed, outcome);
        Assert.Equal(2, yes.Ran.Count);
        Assert.Equal(0, yes.Tests);
    }

    private sealed class Host(bool confirm) : IFixHost
    {
        public List<string> Ran { get; } = new();
        public int Tests;
        public Task<bool> ConfirmAsync(Rung rung) => Task.FromResult(confirm);
        public Task<(bool, string)> RunAsync(Rung rung) { Ran.Add(rung.Id); return Task.FromResult((true, "")); }
        public Task<bool> TestAsync() { Tests++; return Task.FromResult(false); }
        public Task<bool> SettleAsync(Rung rung, bool keep) => Task.FromResult(true);
    }

    // ---- wording ----

    [Fact]
    public void Wording_FollowsTheWritingRules()
    {
        foreach (var r in new[] { ReadRungs.StartupTimes(), ReadRungs.CrashReports() })
        {
            Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |\bI\b|\bwe\b|\bWe\b|!|\bit's\b|\bthat's\b", r.Say);
            Assert.Contains("Windows will ask for your permission", r.Say);
            Assert.Contains("changes nothing", r.Say);
            Assert.Contains(r.ActionId, FixActions.ElevatedActions);
        }
        Assert.EndsWith("Windows will ask for your permission. AutoFix Scout only reads the start-up report and changes nothing.", ReadRungs.StartupTimes().Say);
        Assert.EndsWith("Windows will ask for your permission. AutoFix Scout only reads the crash reports and changes nothing.", ReadRungs.CrashReports().Say);
    }
}

[CollectionDefinition("ElevatedDiagnosticsCache", DisableParallelization = true)]
public class ElevatedDiagnosticsCacheCollection { }
