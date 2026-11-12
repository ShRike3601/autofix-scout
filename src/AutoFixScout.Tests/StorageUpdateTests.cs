using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class StorageUpdateTests
{
    private const long GB = 1L << 30;

    private static StorageState Disk(long freeGb, long totalGb = 256) => new()
    {
        FreeBytes = freeGb * GB, TotalBytes = totalGb * GB, ReclaimableTempBytes = 3 * GB, RecycleBinBytes = 1 * GB,
        StartupAppCount = 4, Uptime = TimeSpan.FromDays(1), MemoryLoadPercent = 40, ProblemDevices = Array.Empty<StorageDeviceInfo>(),
    };

    [Fact]
    public void NearlyFullDisk_OffersCleanupBeforeSettings_AndNeverTouchesHardware()
    {
        var s = Disk(1);
        var f = StorageTriage.Evaluate(s);
        Assert.NotEqual(FindingCode.LooksNormal, f[0].Code);
        var ladder = StorageTriage.Ladder(s, f);
        Assert.Contains(ladder, r => r.ActionId == "storage.clear_user_temp");
        Assert.All(ladder, r => Assert.True(r.Tier <= 1));
        Assert.DoesNotContain(ladder, r => r.ActionId.StartsWith("device.") || r.ActionId.StartsWith("driver."));
        Assert.False(StorageTriage.ProbablyHardware(s));
    }

    [Fact]
    public void PlentyOfSpace_LittleTemp_ShortUptime_LooksNormal()
    {
        var s = Disk(120);
        s.ReclaimableTempBytes = 50L << 20;
        s.RecycleBinBytes = 0;
        Assert.Equal(FindingCode.LooksNormal, StorageTriage.Evaluate(s)[0].Code);
    }

    [Fact]
    public void ProbeError_FailsClosed_NoFixes()
    {
        var s = new StorageState { ProbeErrors = new[] { "WMI" } };
        var f = StorageTriage.Evaluate(s);
        Assert.Equal(FindingCode.ProbeIncomplete, f[0].Code);
        Assert.Empty(StorageTriage.Ladder(s, f));
    }

    [Fact]
    public void ClearTemp_RemovesOnlyOldFilesInsideTheFolder()
    {
        var dir = Directory.CreateTempSubdirectory("afs-temp-test-").FullName;
        try
        {
            var old = Path.Combine(dir, "old.tmp");
            var fresh = Path.Combine(dir, "fresh.tmp");
            File.WriteAllText(old, "x");
            File.WriteAllText(fresh, "y");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
            var (_, deleted, _) = StorageCleanup.ClearTemp(dir, DateTime.UtcNow.AddHours(-24));
            Assert.Equal(1, deleted);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UpdateServiceDisabled_SetsManualThenNothingRisky()
    {
        var s = new UpdateState
        {
            Services = new[] { new UpdateServiceInfo("wuauserv", "Stopped", "Disabled"), new UpdateServiceInfo("BITS", "Running", "Manual"),
                               new UpdateServiceInfo("CryptSvc", "Running", "Automatic") },
            RebootPending = Tri.No, LastInstallUtc = DateTime.UtcNow.AddDays(-3), FreeBytes = 100 * GB, TotalBytes = 256 * GB,
        };
        var ladder = UpdateTriage.Ladder(s, UpdateTriage.Evaluate(s));
        Assert.Equal(("service.set_manual", "wuauserv"), (ladder[0].ActionId, ladder[0].Arg));
        Assert.All(ladder, r => Assert.True(r.Tier <= 1));
        Assert.False(UpdateTriage.Healthy(s));
    }

    [Fact]
    public void Executor_AllowsOnlyUpdateServicesForSetManual()
    {
        Assert.Equal(2, ExecutorProgram.Main(new[] { "service.set_manual", "AudioSrv" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "service.set_manual", "wuauserv & calc" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "service.restart", "Spooler2" }));
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var sentences = new List<string>();
        foreach (var s in new[] { Disk(1), Disk(20), Disk(120) })
        {
            var f = StorageTriage.Evaluate(s);
            sentences.AddRange(f.Select(x => x.PlainMessage));
            sentences.AddRange(StorageTriage.Ladder(s, f).Select(r => r.Say));
        }
        var u = new UpdateState { Services = new[] { new UpdateServiceInfo("wuauserv", "Stopped", "Disabled") }, RebootPending = Tri.Yes };
        var uf = UpdateTriage.Evaluate(u);
        sentences.AddRange(uf.Select(x => x.PlainMessage));
        sentences.AddRange(UpdateTriage.Ladder(u, uf).Select(r => r.Say));
        foreach (var t in sentences)
        {
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|!|\bit's\b|\bthat's\b", t);
        }
    }
}
