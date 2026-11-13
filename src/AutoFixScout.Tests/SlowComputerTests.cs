using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Microsoft.Win32;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

/// <summary>"Slow or full computer": startup apps, busy programs and the component store clean-up.</summary>
public class SlowComputerTests
{
    private const long GB = 1L << 30;
    private const string Win = @"C:\Windows";

    private static StorageState Healthy() => new()
    {
        FreeBytes = 120 * GB, TotalBytes = 256 * GB, ReclaimableTempBytes = 0, RecycleBinBytes = 0, StartupAppCount = 0,
        StartupEntries = Array.Empty<StartupEntry>(), TopProcesses = Array.Empty<ProcessUsage>(),
        Uptime = TimeSpan.FromDays(1), MemoryLoadPercent = 40, ProblemDevices = Array.Empty<StorageDeviceInfo>(),
    };

    private static StartupEntry User(string name, string cmd, bool enabled = true) => new(name, cmd, StartupScope.UserRun, enabled);

    private static (IReadOnlyList<Finding> F, IReadOnlyList<Rung> L) Run(StorageState s)
    {
        var f = StorageTriage.Evaluate(s);
        return (f, StorageTriage.Ladder(s, f));
    }

    // ---- StartupApproved bytes ----

    [Fact]
    public void Approved_MissingOrEvenFirstByte_IsEnabled()
    {
        Assert.True(StartupApps.ParseApproved(null).Enabled);
        Assert.True(StartupApps.ParseApproved(Array.Empty<byte>()).Enabled);
        Assert.True(StartupApps.ParseApproved(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }).Enabled);
        Assert.True(StartupApps.ParseApproved(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }).Enabled);
    }

    [Fact]
    public void Approved_DisabledValue_IsWhatTaskManagerWrites_AndReadsBack()
    {
        var when = new DateTime(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);
        var b = StartupApps.DisabledValue(when);
        Assert.Equal(12, b.Length);
        Assert.Equal(new byte[] { 0x03, 0, 0, 0 }, b[..4]);
        Assert.Equal(when.ToFileTimeUtc(), BitConverter.ToInt64(b, 4));
        var (enabled, at) = StartupApps.ParseApproved(b);
        Assert.False(enabled);
        Assert.Equal(when, at);
    }

    [Fact]
    public void Approved_ShortDisabledValue_IsDisabledWithoutTime()
    {
        var (enabled, at) = StartupApps.ParseApproved(new byte[] { 0x03 });
        Assert.False(enabled);
        Assert.Null(at);
    }

    // ---- what may be offered ----

    [Fact]
    public void Offerable_NeverWindowsSecurityOrOneDrive_NorMachineWide()
    {
        var entries = new[]
        {
            User("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe"),
            User("Windows thing", @"C:\Windows\System32\thing.exe"),
            User("ctfmon", "ctfmon.exe"),
            User("OneDrive", @"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background"),
            User("Avast", @"""C:\Program Files\Avast Software\Avast\AvastUI.exe"" /nogui"),
            User("Malwarebytes", @"C:\Program Files\Malwarebytes\Anti-Malware\mbamtray.exe"),
            User("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", enabled: false),
            new StartupEntry("Discord", @"C:\Discord\Update.exe", StartupScope.MachineRun, true),
        };
        Assert.Empty(StartupApps.Offerable(entries, Win));
        Assert.Equal(new[] { "Discord" }, StartupApps.MachineWide(entries, Win).Select(e => e.Name));
    }

    [Fact]
    public void Offerable_KnownLaunchersFirst_AtMostFive()
    {
        var entries = new[]
        {
            User("Zeta Tool", @"C:\Tools\zeta.exe"),
            User("Alpha Tool", @"C:\Tools\alpha.exe"),
            User("Spotify", @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe /minimized"),
            User("Discord", @"C:\Users\x\AppData\Local\Discord\Update.exe --processStart Discord.exe"),
            User("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent"),
            User("EpicGamesLauncher", @"C:\Epic\EpicGamesLauncher.exe"),
            new StartupEntry("Helper.lnk", @"""C:\Tools\helper.exe""", StartupScope.UserFolder, true),
        };
        var offer = StartupApps.Offerable(entries, Win);
        Assert.Equal(5, offer.Count);
        Assert.Equal(new[] { "Steam", "Discord", "Spotify", "EpicGamesLauncher" }, offer.Take(4).Select(e => e.Name));
        Assert.DoesNotContain(offer, e => e.Name == "Zeta Tool");
    }

    [Fact]
    public void ExecutablePath_HandlesQuotesArgumentsAndVariables()
    {
        Assert.Equal(@"C:\A B\x.exe", StartupApps.ExecutablePath(@"""C:\A B\x.exe"" -q"));
        Assert.Equal(@"C:\A B\x.exe", StartupApps.ExecutablePath(@"C:\A B\x.exe -q"));
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StartupApps.ExecutablePath(@"%windir%\x.exe"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupApps_BecomeSeparateDisableRungs_MachineWideOnlyNamed()
    {
        var s = Healthy();
        s.StartupEntries = new[]
        {
            User("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent"),
            new StartupEntry("Helper.lnk", @"""C:\Tools\helper.exe""", StartupScope.UserFolder, true),
            new StartupEntry("Adobe ARM", @"""C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\AdobeARM.exe""", StartupScope.MachineRun32, true),
        };
        s.StartupAppCount = 3;
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.StartupAppsOptional, f[0].Code);
        Assert.Contains("Adobe ARM", f[0].TechnicalDetail);
        var off = l.Where(r => r.ActionId == "startup.disable").ToList();
        Assert.Equal(new[] { "Steam", "Helper.lnk" }, off.Select(r => r.Arg));
        Assert.Contains("\"Helper\"", off[1].Say);
        Assert.All(off, r => Assert.Equal(1, r.Tier));
        Assert.DoesNotContain(l, r => r.Arg == "Adobe ARM");
        Assert.DoesNotContain("startup.disable", FixActions.ElevatedActions);
    }

    [Fact]
    public void ManyStartupApps_StillGetsDisableRungs()
    {
        var s = Healthy();
        s.StartupEntries = Enumerable.Range(0, 14).Select(i => User($"Tool {i}", $@"C:\Tools\t{i}.exe")).ToList();
        s.StartupAppCount = 14;
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.ManyStartupApps);
        Assert.DoesNotContain(f, x => x.Code == FindingCode.StartupAppsOptional);
        Assert.Equal(StartupApps.MaxOffered, l.Count(r => r.ActionId == "startup.disable"));
    }

    // ---- busy programs ----

    [Fact]
    public void BusyProgram_IsNamed_AndOnlyThePersonClosesIt()
    {
        var s = Healthy();
        s.TopProcesses = new[] { new ProcessUsage("Video Converter", @"C:\Apps\conv.exe", 72.5, 1 * GB, 6) };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HeavyProcessCpu, f[0].Code);
        Assert.Contains("\"Video Converter\"", f[0].PlainMessage);
        Assert.Equal("ui.human_step", l[0].ActionId);
        Assert.Contains("\"Video Converter\"", l[0].Say);
        Assert.Contains("save your work", l[0].Say);
        Assert.DoesNotContain(l, r => r.ActionId.Contains("kill") || r.ActionId.Contains("process"));
    }

    [Fact]
    public void LargeProgram_MemoryFinding_ThresholdsAreStrict()
    {
        var s = Healthy();
        s.TopProcesses = new[]
        {
            new ProcessUsage("Browser", @"C:\Apps\browser.exe", 5, 5 * GB, 31),
            new ProcessUsage("Exactly", @"C:\Apps\exact.exe", 40, 4 * GB, 25),
        };
        var (f, l) = Run(s);
        Assert.Equal(new[] { FindingCode.HeavyProcessMemory }, f.Select(x => x.Code));
        Assert.Contains("\"Browser\"", f[0].PlainMessage);
        Assert.Equal(new[] { "close_large_program" }, l.Where(r => r.ActionId == "ui.human_step").Select(r => r.Id));
    }

    [Fact]
    public void ProgramsUnreadable_WithNothingFound_FailsClosed()
    {
        var s = Healthy();
        s.TopProcesses = null;
        Assert.Equal(FindingCode.ProbeIncomplete, StorageTriage.Evaluate(s)[0].Code);
    }

    [Fact]
    public void Sampler_LeavesOutWindows_AndAddsUpOneProgram()
    {
        var samples = new[]
        {
            new ProcessSampler.ProcSample(10, "chrome", @"C:\Apps\Chrome\chrome.exe", TimeSpan.FromMilliseconds(2000), 1 * GB),
            new ProcessSampler.ProcSample(11, "chrome", @"C:\Apps\Chrome\chrome.exe", TimeSpan.FromMilliseconds(2000), 1 * GB),
            new ProcessSampler.ProcSample(12, "svchost", @"C:\Windows\System32\svchost.exe", TimeSpan.FromMilliseconds(8000), 1 * GB),
            new ProcessSampler.ProcSample(13, "MsMpEng", null, TimeSpan.FromMilliseconds(8000), 1 * GB),
            new ProcessSampler.ProcSample(14, "tool", @"D:\Tools\Windows\tool.exe", TimeSpan.Zero, 1 * GB),
        };
        var r = ProcessSampler.Summarise(samples, TimeSpan.FromSeconds(2), 4, 16 * GB, Win);
        Assert.Equal(2, r.Count);
        var chrome = r.Single(p => p.Name == "chrome");
        Assert.Equal(50, chrome.CpuPercent);
        Assert.Equal(2 * GB, chrome.WorkingSetBytes);
        Assert.Equal(12.5, chrome.MemoryPercent);
        Assert.True(ProcessSampler.IsWindowsProcess("explorer", @"C:\Windows\explorer.exe", Win));
    }

    // ---- component store ----

    [Fact]
    public void LowDrive_OffersComponentCleanup_Elevated_WithSystemArgument()
    {
        var s = Healthy();
        s.FreeBytes = 1 * GB;
        var (_, l) = Run(s);
        var r = l.Single(x => x.ActionId == "storage.cleanup_components");
        Assert.Equal("system", r.Arg);
        Assert.Contains("Windows will ask for your permission", r.Say);
        Assert.Contains("storage.cleanup_components", FixActions.ElevatedActions);
        Assert.DoesNotContain(Run(Healthy()).L, x => x.ActionId == "storage.cleanup_components");
    }

    [Theory]
    [InlineData("System")]
    [InlineData("")]
    [InlineData("system & calc")]
    [InlineData("C:")]
    [InlineData("/ResetBase")]
    public void Executor_ComponentCleanup_AcceptsOnlySystem(string arg) =>
        Assert.Equal(2, ExecutorProgram.Main(new[] { "storage.cleanup_components", arg }));

    [Fact]
    public void Executor_ComponentCleanup_RejectsExtraArguments() =>
        Assert.Equal(2, ExecutorProgram.Main(new[] { "storage.cleanup_components", "system", "/ResetBase" }));

    // ---- startup.disable validation ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"..\..\Run\x")]
    [InlineData("a/b")]
    [InlineData("bad\nname")]
    public void Disable_RejectsMalformedNames(string name) => Assert.False(StartupApps.Disable(name).Ok);

    [Fact]
    public async Task Disable_RejectsNamesThatAreNotThere_AndWritesNothing()
    {
        var name = "AFS-No-Such-Entry-" + Guid.NewGuid().ToString("N");
        var (ok, detail) = await FixActions.RunAsync(new Rung("t", "startup.disable", 1, "", name));
        Assert.False(ok, detail);
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApps.ApprovedKey + "Run");
        Assert.Null(approved?.GetValue(name));
    }

    // ---- big space users ----

    private static StorageState Full()
    {
        var s = Healthy();
        s.FreeBytes = 5 * GB;
        s.WindowsOld = new SpaceItem(12 * GB, true);
        s.HiberFile = new SpaceItem(6 * GB, false);
        s.Downloads = new SpaceItem(3 * GB, false);
        s.DownloadsOldCount = 40;
        s.DeliveryOptimization = new SpaceItem(2 * GB, false);
        s.BigProfileFolders = new[] { new ProfileFolder("Videos", 20 * GB, false) };
        return s;
    }

    [Fact]
    public void Size_Formatting()
    {
        Assert.Equal("1.5 GB", StorageTriage.Size(3 * GB / 2));
        Assert.Equal("1 MB", StorageTriage.Size(10));
        Assert.Equal("at least 12.0 GB", StorageTriage.SizeOf(new SpaceItem(12 * GB, true)));
        Assert.Equal("6.0 GB", StorageTriage.SizeOf(new SpaceItem(6 * GB, false)));
    }

    [Fact]
    public void BigUsers_OnlyWhenDriveIsLowOrCritical()
    {
        var s = Full();
        s.FreeBytes = 120 * GB;
        Assert.DoesNotContain(StorageTriage.Evaluate(s), x => x.Code == FindingCode.StorageBigUsers);
        var (f, l) = Run(Full());
        var big = Assert.Single(f, x => x.Code == FindingCode.StorageBigUsers);
        Assert.Contains("Windows.old", big.PlainMessage);
        Assert.Contains("at least 12.0 GB", big.PlainMessage);
        Assert.Contains(f, x => x.Code == FindingCode.SystemDriveCritical);
        // Existing order kept: the space finding stays first, the new one comes after the existing ones.
        Assert.True(f.ToList().FindIndex(x => x.Code == FindingCode.SystemDriveCritical) < f.ToList().FindIndex(x => x.Code == FindingCode.StorageBigUsers));
        Assert.Contains(l, r => r.Id == "cleanup_components");
        var settings = Assert.Single(l, r => r.Id == "storage_settings");
        Assert.Equal("storagesense", settings.Arg);
        Assert.Contains("previous Windows installation", settings.Say);
        Assert.Contains("hibernation", Assert.Single(l, r => r.Id == "hibernation_info").Say);
        Assert.Equal("ui.human_step", Assert.Single(l, r => r.Id == "review_downloads").ActionId);
        Assert.DoesNotContain(l, r => r.ActionId.Contains("hibern") || r.ActionId.Contains("download"));
    }

    [Fact]
    public void BigUsers_UnknownPartsAreSkipped_AndSmallOnesIgnored()
    {
        var s = Full();
        s.WindowsOld = null; s.DeliveryOptimization = null; s.Downloads = new SpaceItem(10, false);
        var (f, l) = Run(s);
        Assert.Contains("hibernation file", Assert.Single(f, x => x.Code == FindingCode.StorageBigUsers).PlainMessage);
        Assert.DoesNotContain(l, r => r.Id == "review_downloads");
        s.HiberFile = null;
        Assert.DoesNotContain(StorageTriage.Evaluate(s), x => x.Code == FindingCode.StorageBigUsers);
    }

    [Fact]
    public void Rungs_HaveUniqueIds()
    {
        foreach (var st in StyleStates().Append(Full()))
        {
            var (_, l) = Run(st);
            Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        }
    }

    /// <summary>States that produce every slow-computer finding and rung (also scanned by WritingStyleTests).</summary>
    public static IEnumerable<StorageState> StyleStates()
    {
        var s = Healthy();
        s.FreeBytes = 1 * GB;
        s.ReclaimableTempBytes = 3 * GB;
        s.StartupEntries = new[] { User("Steam", @"C:\Steam\steam.exe"), User("Discord", @"C:\Discord\Update.exe") };
        s.StartupAppCount = 2;
        s.TopProcesses = new[]
        {
            new ProcessUsage("Video Converter", @"C:\Apps\conv.exe", 72.5, 1 * GB, 6),
            new ProcessUsage("Browser", @"C:\Apps\browser.exe", 5, 5 * GB, 31),
        };
        var one = Healthy();
        one.StartupEntries = new[] { User("Steam", @"C:\Steam\steam.exe") };
        one.StartupAppCount = 1;
        var slow = Healthy();
        slow.FastStartup = false;
        slow.BootTimes = new[] { new BootRecord(DateTime.UtcNow, 90000, 80000, 10000) };
        slow.SlowStartupItems = new[] { new SlowStartupItem("Contoso Sync", 20000) };
        return new[] { s, one, Full(), slow };
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var texts = new List<string>();
        foreach (var st in StyleStates())
        {
            var (f, l) = Run(st);
            texts.AddRange(f.Select(x => x.PlainMessage));
            texts.AddRange(l.Select(r => r.Say));
            texts.Add(StorageTriage.NotFixedHint(f) ?? "None.");
        }
        Assert.True(texts.Count >= 10);
        foreach (var t in texts)
            Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |\bI\b|!|\bit's\b|\bthat's\b|\bwe\b", t);
    }
}
