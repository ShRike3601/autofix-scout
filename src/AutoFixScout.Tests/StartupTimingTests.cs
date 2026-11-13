using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class StartupTimingTests
{
    private static StorageState Base() => new()
    {
        FreeBytes = 120L << 30, TotalBytes = 256L << 30, ReclaimableTempBytes = 0, RecycleBinBytes = 0, StartupAppCount = 0,
        StartupEntries = Array.Empty<StartupEntry>(), TopProcesses = Array.Empty<ProcessUsage>(),
        Uptime = TimeSpan.FromDays(1), MemoryLoadPercent = 40, ProblemDevices = Array.Empty<StorageDeviceInfo>(),
    };

    private static BootRecord B(long main, long post = 0) => new(DateTime.UtcNow, main + 5000, main, post);

    [Fact]
    public void Median_OddEvenEmpty()
    {
        Assert.Equal(3, StorageTriage.Median(new long[] { 5, 1, 3 }));
        Assert.Equal(25, StorageTriage.Median(new long[] { 10, 40, 20, 30 }));
        Assert.Equal(0, StorageTriage.Median(Array.Empty<long>()));
    }

    [Fact]
    public void Threshold_MedianNotSingleBoot()
    {
        var s = Base();
        s.BootTimes = new[] { B(300000), B(30000), B(30000) };
        Assert.False(StorageTriage.SlowStartup(s));
        s.BootTimes = new[] { B(61000), B(70000), B(20000) };
        Assert.True(StorageTriage.SlowStartup(s));
        s.BootTimes = new[] { B(60000) };
        Assert.False(StorageTriage.SlowStartup(s));
        s.BootTimes = new[] { B(10000, 121000) };
        Assert.True(StorageTriage.SlowStartup(s));
    }

    [Fact]
    public void UnknownOrEmpty_NoFinding()
    {
        var s = Base();
        Assert.DoesNotContain(StorageTriage.Evaluate(s), x => x.Code == FindingCode.StorageSlowStartup);
        s.BootTimes = Array.Empty<BootRecord>();
        Assert.DoesNotContain(StorageTriage.Evaluate(s), x => x.Code == FindingCode.StorageSlowStartup);
    }

    [Fact]
    public void Finding_NamesUpToThreeItems_AndOffersRungs()
    {
        var s = Base();
        s.BootTimes = new[] { B(90000) };
        s.FastStartup = false;
        s.SlowStartupItems = new[] { new SlowStartupItem("A App", 4), new SlowStartupItem("B App", 3), new SlowStartupItem("C App", 2), new SlowStartupItem("D App", 1) };
        var f = StorageTriage.Evaluate(s);
        var m = Assert.Single(f, x => x.Code == FindingCode.StorageSlowStartup).PlainMessage;
        Assert.Contains("\"C App\"", m);
        Assert.DoesNotContain("D App", m);
        var l = StorageTriage.Ladder(s, f);
        Assert.Contains(l, r => r.Id == "keep_windows_updated");
        Assert.Contains(l, r => r.Id == "fast_startup_info");
        Assert.Contains(l, r => r.Id == "startup_apps");
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        s.FastStartup = true;
        Assert.DoesNotContain(StorageTriage.Ladder(s, f), r => r.Id == "fast_startup_info");
    }

    private static string Xml(int id, string data) =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>{id}</EventID>" +
        "<TimeCreated SystemTime='2026-11-12T08:00:00.0000000Z'/></System><EventData>" + data + "</EventData></Event>";

    [Fact]
    public void ParseBoot_ReadsTimings()
    {
        var b = StartupTimings.ParseBoot(Xml(100, "<Data Name='BootTime'>95000</Data><Data Name='MainPathBootTime'>70000</Data><Data Name='BootPostBootTime'>25000</Data>"));
        Assert.NotNull(b);
        Assert.Equal(95000, b!.BootMs); Assert.Equal(70000, b.MainPathMs); Assert.Equal(25000, b.PostBootMs);
        Assert.Equal(new DateTime(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc), b.TimeUtc);
        Assert.Null(StartupTimings.ParseBoot(Xml(101, "<Data Name='BootTime'>1</Data>")));
        Assert.Null(StartupTimings.ParseBoot(Xml(100, "<Data Name='BootTime'>x</Data>")));
        Assert.Null(StartupTimings.ParseBoot("not xml"));
    }

    [Fact]
    public void ParseSlowItem_FriendlyNameOnly()
    {
        var i = StartupTimings.ParseSlowItem(Xml(101, "<Data Name='Name'>c:/x/sync.exe</Data><Data Name='FriendlyName'> Contoso Sync </Data><Data Name='TotalTime'>12000</Data>"));
        Assert.Equal("Contoso Sync", i!.Name);
        Assert.Equal(12000, i.TotalMs);
        Assert.Null(StartupTimings.ParseSlowItem(Xml(101, "<Data Name='Name'>c:/x/sync.exe</Data>")));
        Assert.Null(StartupTimings.ParseSlowItem(Xml(100, "<Data Name='FriendlyName'>A</Data>")));
        Assert.Null(StartupTimings.ParseSlowItem(Xml(111, "<Data Name='FriendlyName'>A</Data>")));
    }
}

[Collection("Live machine")]
public class LiveStartupTimingTests
{
    [Fact]
    public void ReadsBootTimes_ReadOnly()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = new RealStorageProbe().Read();
        var lines = new List<string>
        {
            "probe errors: " + string.Join("; ", s.ProbeErrors),
            "boot times: " + (s.BootTimes is null ? "Unknown (log not readable)" : s.BootTimes.Count + " boots"),
            "slow items: " + (s.SlowStartupItems is null ? "Unknown" : string.Join(", ", s.SlowStartupItems.Take(3).Select(x => x.Name))),
            "fast startup: " + (s.FastStartup?.ToString() ?? "Unknown"),
            "startup apps: " + s.StartupAppCount,
        };
        if (s.BootTimes is { Count: > 0 } b) lines.AddRange(b.Select(x => $"BOOT {x.TimeUtc:u} boot={x.BootMs} main={x.MainPathMs} post={x.PostBootMs}"));
        var f = StorageTriage.Evaluate(s);
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage}"));
        File.WriteAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
    }
}
