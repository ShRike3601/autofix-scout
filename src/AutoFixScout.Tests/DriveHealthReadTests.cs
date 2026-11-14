using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>The read-only elevated action diag.read_drive_health. None of these tests runs the helper.</summary>
[Collection("ElevatedDiagnosticsCache")]
public class DriveHealthReadTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private const string A = "diag.read_drive_health";

    // ---- executor and action list ----

    [Theory]
    [InlineData("x")]
    [InlineData("0")]
    [InlineData("C:\\")]
    [InlineData("")]
    public void Executor_OneArgument_IsRejected(string arg) => Assert.Equal(2, AutoFixScout.Executor.Program.Main(new[] { A, arg }));

    [Fact]
    public void Executor_ManyArguments_AreRejected()
    {
        Assert.Equal(2, AutoFixScout.Executor.Program.Main(new[] { A, "a", "b" }));
        Assert.Equal(2, AutoFixScout.Executor.Program.Main(new[] { A, "a", "b", "c" }));
    }

    [Fact]
    public void Action_IsInElevatedActions_AndUsesAFixedFile()
    {
        Assert.Contains(A, FixActions.ElevatedActions);
        Assert.True(DiagResults.IsDiagAction(A));
        Assert.Equal("diag-drive-health.json", Path.GetFileName(DiagResults.FileFor(A)));
    }

    // ---- the parser ----

    private static DriveHealthRecord Rec(string name = "Samsung SSD", string media = "SSD", int? id = 1, int? wear = 5, int? temp = 40, int? max = 50,
        long? re = 0, long? we = 0, long? hours = 1000) =>
        new(id, name, media, "NVMe", "Healthy", 512_000_000_000, wear, temp, max, 0, re, 0, we, hours, 100);

    [Fact]
    public void Drives_RoundTrip()
    {
        var r = DiagResults.ParseDrives(DiagResults.Serialize(DiagResults.CleanDrives(new[] { Rec(), Rec("Seagate", "HDD", 0, null, 35, 41) }, Now)), Now);
        Assert.NotNull(r);
        Assert.Equal(2, r!.Drives.Count);
        Assert.Equal(5, r.Drives[0].WearPercent);
        Assert.Null(r.Drives[1].WearPercent);
        Assert.Equal(1000, r.Drives[0].PowerOnHours);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not json")]
    [InlineData("{\"Drives\": [")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Action\":\"diag.read_drive_health\"}")]
    [InlineData("{\"Version\":1,\"Action\":\"diag.read_boot_times\"}")]
    [InlineData("{\"Version\":1,\"Action\":\"diag.read_drive_health\",\"Drives\":[{\"WearPercent\":\"many\"}]}")]
    public void Drives_MalformedOrMissing_IsNull(string? json) => Assert.Null(DiagResults.ParseDrives(json, Now));

    [Fact]
    public void Drives_TooBig_IsRefused() =>
        Assert.Null(DiagResults.ParseDrives(new string(' ', DiagResults.MaxChars + 1) + DiagResults.Serialize(DiagResults.CleanDrives(new[] { Rec() }, Now)), Now));

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(101, null)]
    [InlineData(255, null)]
    public void Wear_OutOfRange_BecomesUnknown(int wear, int? expected) =>
        Assert.Equal(expected, DiagResults.CleanDrives(new[] { Rec(wear: wear) }, Now).Drives.Single().WearPercent);

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, 0)]
    [InlineData(120, 120)]
    [InlineData(121, null)]
    [InlineData(5000, null)]
    public void Temperature_OutOfRange_BecomesUnknown(int t, int? expected)
    {
        var d = DiagResults.CleanDrives(new[] { Rec(temp: t, max: t) }, Now).Drives.Single();
        Assert.Equal(expected, d.TemperatureC);
        Assert.Equal(expected, d.TemperatureMaxC);
    }

    [Fact]
    public void ErrorsAndHours_OutOfRange_BecomeUnknown()
    {
        var d = DiagResults.CleanDrives(new[] { Rec(re: -3, we: long.MaxValue, hours: 99_999_999) }, Now).Drives.Single();
        Assert.Null(d.ReadErrorsUncorrected);
        Assert.Null(d.WriteErrorsUncorrected);
        Assert.Null(d.PowerOnHours);
    }

    [Fact]
    public void Drives_CountIsCapped()
    {
        var many = Enumerable.Range(0, 100).Select(i => Rec("Drive " + i, id: i));
        Assert.Equal(DiagResults.MaxDrives, DiagResults.CleanDrives(many, Now).Drives.Count);
    }

    // ---- privacy ----

    [Fact]
    public void PrivacyFilter_DropsPathsAndPrivateNames_AndTheRecordHasNoSerialField()
    {
        var r = DiagResults.CleanDrives(new[]
        {
            Rec(@"C:\Users\Jane\drive"),             // names a user folder: dropped
            Rec("Disk\u0007X"),                      // control character: dropped
            Rec(@"\\?\scsi#disk&ven_acme#1234"),     // a path: cut to its last part
            Rec("Samsung SSD 980"),
        }, Now);
        Assert.DoesNotContain(r.Drives, d => d.Name.Contains("Jane") || d.Name.Contains('\\'));
        Assert.Contains(r.Drives, d => d.Name == "Samsung SSD 980");
        Assert.Equal(2, r.Drives.Count);
        Assert.DoesNotContain(typeof(DriveHealthRecord).GetProperties(), p => p.Name.Contains("Serial", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Serial", DiagResults.Serialize(r), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WordFields_AreRestrictedToPlainWords()
    {
        var d = DiagResults.CleanDrives(new[] { new DriveHealthRecord(1, "X", @"C:\Users\Jane", "NVMe", "Healthy; drop table", null, null, null, null, null, null, null, null, null, null) }, Now).Drives.Single();
        Assert.Equal("Unknown", d.MediaType);
        Assert.Equal("NVMe", d.BusType);
        Assert.Equal("Unknown", d.Health);
    }

    // ---- Finish: reading, deleting ----

    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "afs-drv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Finish_ReadsValidatesAndDeletesTheFile()
    {
        var dir = Temp();
        try
        {
            ElevatedDiagnostics.Reset();
            var file = Path.Combine(dir, DiagResults.DriveFile);
            File.WriteAllText(file, DiagResults.Serialize(DiagResults.CleanDrives(new[] { Rec() }, DateTime.UtcNow)));
            var (ok, _) = ElevatedDiagnostics.Finish(A, 0, dir);
            Assert.True(ok);
            Assert.False(File.Exists(file));
            Assert.Single(ElevatedDiagnostics.DriveHealth!.Drives);

            File.WriteAllText(file, "garbage");
            ElevatedDiagnostics.Reset();
            Assert.False(ElevatedDiagnostics.Finish(A, 0, dir).Ok);
            Assert.False(File.Exists(file));
            Assert.Null(ElevatedDiagnostics.DriveHealth);

            File.WriteAllText(file, "stale");
            Assert.False(ElevatedDiagnostics.Finish(A, 1, dir).Ok); // failed run: nothing used, stale file removed
            Assert.False(File.Exists(file));

            Assert.True(ElevatedDiagnostics.Finish(A, 3, dir).Ok); // nothing found: a known empty result
            Assert.Empty(ElevatedDiagnostics.DriveHealth!.Drives);
            Assert.True(ElevatedDiagnostics.DriveHealthWasRead);
        }
        finally { ElevatedDiagnostics.Reset(); Directory.Delete(dir, true); }
    }

    // ---- merging ----

    private static PhysicalDriveInfo Drive(string name = "Samsung SSD", string media = "SSD", int? id = 1) =>
        new(name, media, "Healthy", new[] { 2 }, false) { DeviceId = id };

    private static HealthState Health(params PhysicalDriveInfo[] drives) => new()
    {
        ClockOffsetSeconds = 2, RebootPending = Tri.No, Drives = drives,
        Services = ServiceDefaults.All.Select(d => new UpdateServiceInfo(d.Name, d.ShouldRun ? "Running" : "Stopped", d.Start.ToString())).ToList(),
    };

    [Fact]
    public void Merge_FillsOnlyUnknowns()
    {
        var s = Health(Drive() with { WearPercent = 12 });
        ElevatedDiagnostics.Merge(s, DiagResults.CleanDrives(new[] { Rec(wear: 91, temp: 44, max: 71, re: 2, we: 0, hours: 5000) }, Now));
        var d = s.Drives.Single();
        Assert.Equal(12, d.WearPercent);        // the app knew it already
        Assert.Equal(44, d.TemperatureC);
        Assert.Equal(71, d.TemperatureMaxC);
        Assert.Equal(2, d.ReadErrorsUncorrected);
        Assert.Equal(0, d.WriteErrorsUncorrected);
        Assert.Equal(5000, d.PowerOnHours);
        Assert.True(s.DriveHealthRead);
    }

    [Fact]
    public void Merge_MatchesByDriveNumber_ThenByUniqueName_AndLeavesOthersAlone()
    {
        var s = Health(Drive("Same", "SSD", 0), Drive("Same", "SSD", 1), Drive("Other", "HDD", 7));
        ElevatedDiagnostics.Merge(s, DiagResults.CleanDrives(new[] { Rec("Same", id: 1, wear: 33), Rec("Nothing", id: 9, wear: 44) }, Now));
        Assert.Null(s.Drives[0].WearPercent);
        Assert.Equal(33, s.Drives[1].WearPercent);
        Assert.Null(s.Drives[2].WearPercent);

        var byName = Health(Drive("Solo", "SSD", null));
        ElevatedDiagnostics.Merge(byName, DiagResults.CleanDrives(new[] { Rec("Solo", id: null, wear: 20) }, Now));
        Assert.Equal(20, byName.Drives[0].WearPercent);

        var twins = Health(Drive("Twin", "SSD", null), Drive("Twin", "SSD", null));
        ElevatedDiagnostics.Merge(twins, DiagResults.CleanDrives(new[] { Rec("Twin", id: null, wear: 20) }, Now));
        Assert.All(twins.Drives, d => Assert.Null(d.WearPercent)); // ambiguous: never guessed
    }

    [Fact]
    public void Probe_AppliesWhatTheHelperReadEarlier()
    {
        try
        {
            ElevatedDiagnostics.Reset();
            var s = Health(Drive());
            ElevatedDiagnostics.ApplyTo(s);
            Assert.Null(s.Drives[0].WearPercent);
            Assert.False(s.DriveHealthRead);
            var dir = Temp();
            try
            {
                File.WriteAllText(Path.Combine(dir, DiagResults.DriveFile), DiagResults.Serialize(DiagResults.CleanDrives(new[] { Rec(wear: 80) }, DateTime.UtcNow)));
                ElevatedDiagnostics.Finish(A, 0, dir);
            }
            finally { Directory.Delete(dir, true); }
            ElevatedDiagnostics.ApplyTo(s);
            Assert.Equal(80, s.Drives[0].WearPercent);
        }
        finally { ElevatedDiagnostics.Reset(); }
    }

    // ---- findings: each threshold on both sides ----

    private static IReadOnlyList<FindingCode> Codes(PhysicalDriveInfo d) => HealthTriage.Evaluate(Health(d)).Select(f => f.Code).ToList();

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(0, false, false)]
    [InlineData(69, false, false)]
    [InlineData(70, true, false)]
    [InlineData(89, true, false)]
    [InlineData(90, false, true)]
    [InlineData(100, false, true)]
    public void SsdWear_Thresholds(int? wear, bool wearing, bool wornOut)
    {
        var c = Codes(Drive() with { WearPercent = wear });
        Assert.Equal(wearing, c.Contains(FindingCode.HealthDriveWearing));
        Assert.Equal(wornOut, c.Contains(FindingCode.HealthDriveWornOut));
    }

    [Fact]
    public void Wear_OfAHardDrive_IsNotAnSsdFinding() =>
        Assert.DoesNotContain(Codes(Drive("Seagate", "HDD") with { WearPercent = 95 }), c => c is FindingCode.HealthDriveWornOut or FindingCode.HealthDriveWearing);

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(0L, 0L, false)]
    [InlineData(1L, 0L, true)]
    [InlineData(0L, 1L, true)]
    [InlineData(5L, 7L, true)]
    public void UncorrectedErrors_Thresholds(long? read, long? write, bool expected) =>
        Assert.Equal(expected, Codes(Drive() with { ReadErrorsUncorrected = read, WriteErrorsUncorrected = write }).Contains(FindingCode.HealthDriveErrors));

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(76, false)] // the bench SSD (2026-11-14): a lifetime peak in the 70s is normal under load
    [InlineData(84, false)]
    [InlineData(85, true)]
    [InlineData(95, true)]
    public void MaxTemperature_Thresholds(int? max, bool expected) =>
        Assert.Equal(expected, Codes(Drive() with { TemperatureMaxC = max }).Contains(FindingCode.HealthDriveHot));

    [Theory]
    [InlineData(53, false)] // the bench SSD now
    [InlineData(64, false)]
    [InlineData(65, true)]
    public void CurrentTemperature_Thresholds(int now, bool expected) =>
        Assert.Equal(expected, Codes(Drive() with { TemperatureC = now, TemperatureMaxC = 76 }).Contains(FindingCode.HealthDriveHot));

    [Fact]
    public void WornOut_WinsOverWearing_AndBothGetTheBackupRung()
    {
        var s = Health(Drive("A", "SSD", 0) with { WearPercent = 75 }, Drive("B", "SSD", 1) with { WearPercent = 92 });
        var f = HealthTriage.Evaluate(s);
        Assert.Contains(f, x => x.Code == FindingCode.HealthDriveWornOut && x.PlainMessage.Contains("\"B\"") && x.PlainMessage.Contains("92 percent"));
        Assert.DoesNotContain(f, x => x.Code == FindingCode.HealthDriveWearing);
        Assert.Contains(HealthTriage.Ladder(s, f), r => r.Id == "back_up_files");
    }

    [Fact]
    public void FindingMessages_FollowTheWritingRules()
    {
        var s = Health(Drive() with { WearPercent = 92, ReadErrorsUncorrected = 3, TemperatureMaxC = 88 });
        var f = HealthTriage.Evaluate(s);
        var worn = f.Single(x => x.Code == FindingCode.HealthDriveWornOut);
        Assert.Equal("The SSD \"Samsung SSD\" has used about 92 percent of its expected life. Please copy important files to another drive or to cloud storage now, and plan to replace the SSD.", worn.PlainMessage);
        var soft = HealthTriage.Evaluate(Health(Drive() with { WearPercent = 75 })).Single(x => x.Code == FindingCode.HealthDriveWearing);
        foreach (var m in new[] { worn, soft, f.Single(x => x.Code == FindingCode.HealthDriveErrors), f.Single(x => x.Code == FindingCode.HealthDriveHot) })
        {
            Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |\bI\b|\bwe\b|\bWe\b|!|\bit's\b|\bthat's\b", m.PlainMessage);
            Assert.DoesNotContain("serial", m.TechnicalDetail, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(HealthTriage.Ladder(s, f), r => r.Id == "drive_cooling");
    }

    [Fact]
    public void NewCodes_AreAtTheEndOfTheEnum()
    {
        var all = Enum.GetValues<FindingCode>().ToList();
        Assert.Equal(new[] { FindingCode.HealthDriveWornOut, FindingCode.HealthDriveWearing, FindingCode.HealthDriveErrors, FindingCode.HealthDriveHot }, all.SkipLast(2).TakeLast(4)); // the DNS codes were appended after these
    }

    // ---- the rung ----

    private static StorageState Storage(int drives, bool read) => new()
    {
        FreeBytes = 120L << 30, TotalBytes = 256L << 30, ReclaimableTempBytes = 0, RecycleBinBytes = 0, StartupAppCount = 0,
        StartupEntries = Array.Empty<StartupEntry>(), TopProcesses = Array.Empty<ProcessUsage>(), BootTimes = Array.Empty<BootRecord>(),
        Uptime = TimeSpan.FromDays(1), MemoryLoadPercent = 40, ProblemDevices = Array.Empty<StorageDeviceInfo>(),
        PhysicalDriveCount = drives, DriveHealthRead = read,
    };

    private static PerfState Perf(int drives, bool read) => new()
    {
        PerformanceLimitPercent = 100, ProcessorPerformancePercent = 40, CpuName = "AMD Ryzen 5 5600X 6-Core Processor",
        BiosDateUtc = Now.AddDays(-200), ActiveSchemeGuid = PowerTriage.BalancedGuid, OverlayGuid = Guid.Empty.ToString(),
        InstabilityRead = true, RamRead = true, PhysicalDriveCount = drives, DriveHealthRead = read,
    };

    [Fact]
    public void Rung_Properties_AndWording()
    {
        var r = ReadRungs.DriveHealth();
        Assert.Equal(A, r.ActionId);
        Assert.Equal(1, r.Tier);
        Assert.Null(r.Arg);
        Assert.Equal(ReadRungs.DriveId, r.Id);
        Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |\bI\b|\bwe\b|\bWe\b|!|\bit's\b|\bthat's\b", r.Say);
        Assert.EndsWith("Windows will ask for your permission. AutoFix Scout only reads the drive health and changes nothing.", r.Say);
        Assert.Contains("never serial numbers", r.Say);
        Assert.Contains(r.ActionId, FixActions.ElevatedActions);
    }

    [Fact]
    public void Rung_InTheStorageLadder_OnlyWhenDrivesExistAndNotRead()
    {
        bool Has(StorageState s) => StorageTriage.Ladder(s, StorageTriage.Evaluate(s)).Any(r => r.ActionId == A);
        Assert.True(Has(Storage(2, false)));
        Assert.False(Has(Storage(0, false)));
        Assert.False(Has(Storage(2, true)));
    }

    [Fact]
    public void Rung_InThePerfLadder_OnlyWhenDrivesExistAndNotRead()
    {
        bool Has(PerfState s) => PerfTriage.Ladder(s, PerfTriage.Evaluate(s, Now)).Any(r => r.ActionId == A);
        Assert.True(Has(Perf(2, false)));
        Assert.False(Has(Perf(0, false)));
        Assert.False(Has(Perf(2, true)));
    }

    [Fact]
    public void Rung_InTheHealthLadder_OnlyWhileWearIsUnknownAndNotRead()
    {
        bool Has(HealthState s) => HealthTriage.Ladder(s, HealthTriage.Evaluate(s)).Any(r => r.ActionId == A);
        Assert.True(Has(Health(Drive())));
        Assert.False(Has(Health()));                                              // no drives
        Assert.False(Has(Health(Drive() with { WearPercent = 10 })));             // already known
        var read = Health(Drive()); read.DriveHealthRead = true;
        Assert.False(Has(read));                                                  // read already
    }

    [Fact]
    public void Rung_IsNeverStartedAutomatically_AndSkipsTheFixTest()
    {
        var no = new Host(false);
        Assert.Equal(Outcome.Abandoned, LadderRunner.RunAsync(new[] { ReadRungs.DriveHealth() }, no, new()).GetAwaiter().GetResult());
        Assert.Empty(no.Ran);
        var yes = new Host(true);
        Assert.Equal(Outcome.NotFixed, LadderRunner.RunAsync(new[] { ReadRungs.DriveHealth() }, yes, new()).GetAwaiter().GetResult());
        Assert.Single(yes.Ran);
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
}
