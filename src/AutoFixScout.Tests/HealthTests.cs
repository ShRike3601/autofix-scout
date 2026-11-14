using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class HealthTests
{
    private static HealthState Healthy() => new()
    {
        ClockOffsetSeconds = 2,
        Services = ServiceDefaults.All.Select(d => new UpdateServiceInfo(d.Name, d.ShouldRun ? "Running" : "Stopped", d.Start.ToString())).ToList(),
        RebootPending = Tri.No,
    };

    private static (IReadOnlyList<Finding> F, IReadOnlyList<Rung> L) Run(HealthState s)
    {
        var f = HealthTriage.Evaluate(s);
        return (f, HealthTriage.Ladder(s, f));
    }

    [Fact]
    public void Healthy_LooksNormal_OffersOnlySystemFileRepair()
    {
        var (f, l) = Run(Healthy());
        Assert.Equal(FindingCode.LooksNormal, f[0].Code);
        Assert.Equal(new[] { "repair.dism", "repair.sfc" }, l.Select(r => r.ActionId));
    }

    [Fact]
    public void UnreadablePart_WithNothingFound_FailsClosed()
    {
        var s = Healthy();
        s.ProbeErrors = new[] { "hosts file: access denied" };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.ProbeIncomplete, f[0].Code);
        Assert.Empty(l);
    }

    [Fact]
    public void UnreadablePart_DoesNotHideWhatOtherPartsFound()
    {
        var s = Healthy();
        s.ProbeErrors = new[] { "Store apps: timeout" };
        s.ClockOffsetSeconds = -3600;
        Assert.Equal(FindingCode.HealthClockWrong, Run(s).F[0].Code);
    }

    [Fact]
    public void WrongClock_Resyncs()
    {
        var s = Healthy();
        s.ClockOffsetSeconds = 600;
        Assert.Equal("time.resync", Run(s).L[0].ActionId);
    }

    [Fact]
    public void DeadProxy_SwitchesItOffWithoutAdmin()
    {
        var s = Healthy();
        s.UserProxyEnabled = true; s.UserProxyServer = "127.0.0.1:9"; s.UserProxyReachable = Tri.No;
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthDeadProxy, f[0].Code);
        Assert.Equal("network.reset_user_proxy", l[0].ActionId);
        Assert.DoesNotContain(l[0].ActionId, FixActions.ElevatedActions);
    }

    [Fact]
    public void WorkingProxy_IsLeftAlone()
    {
        var s = Healthy();
        s.UserProxyEnabled = true; s.UserProxyServer = "proxy.corp:8080"; s.UserProxyReachable = Tri.Yes;
        Assert.Equal(FindingCode.LooksNormal, Run(s).F[0].Code);
    }

    [Fact]
    public void DisabledService_RestoresItsDefault()
    {
        var s = Healthy();
        s.Services = s.Services.Select(x => x.Name == "Dnscache" ? x with { Status = "Stopped", StartType = "Disabled" } : x).ToList();
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthServiceOff, f[0].Code);
        Assert.Contains(l, r => r.ActionId == "service.restore_default" && r.Arg == "Dnscache");
    }

    [Fact]
    public void ManualServiceStopped_IsNormal()
    {
        Assert.False(ServiceDefaults.IsFault("wuauserv", "Stopped", "Manual"));
        Assert.False(ServiceDefaults.IsFault("WlanSvc", "NotFound", "NotFound"));
        Assert.True(ServiceDefaults.IsFault("AudioSrv", "Stopped", "Automatic"));
        Assert.True(ServiceDefaults.IsFault("wuauserv", "Stopped", "Disabled"));
        Assert.False(ServiceDefaults.IsFault("SomeOtherService", "Stopped", "Disabled"));
    }

    [Fact]
    public void ProgramCrash_InRuntime_OffersVisualCppReinstall()
    {
        var s = Healthy();
        s.Crashes = new[] { new CrashInfo("Game.exe", "VCRUNTIME140.dll", "c0000005", 4, DateTime.UtcNow) };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthProgramCrashing, f[0].Code);
        Assert.Contains("Visual C++", f[0].PlainMessage);
        Assert.Contains(l, r => r.ActionId == "runtime.install_vcredist");
        Assert.Contains(l, r => r.Arg == "apps");
    }

    [Fact]
    public void ChosenProgram_IsTargetedFirst_EvenAfterOneCrash()
    {
        var s = Healthy();
        s.Crashes = new[]
        {
            new CrashInfo("Other.exe", "nvwgf2umx.dll", "c0000005", 5, DateTime.UtcNow),
            new CrashInfo("Word.exe", "VCRUNTIME140.dll", "c0000005", 1, DateTime.UtcNow),
        };
        Assert.Equal("Other.exe", HealthTriage.ProgramCrashes(s)[0].App);
        Assert.Equal(new[] { "Other.exe", "Word.exe" }, HealthTriage.CrashCandidates(s).Select(c => c.App));
        s.ChosenApp = "word.exe";
        var (f, l) = Run(s);
        Assert.Contains("\"Word.exe\"", f.First(x => x.Code == FindingCode.HealthProgramCrashing).PlainMessage);
        Assert.Contains(l, r => r.ActionId == "runtime.install_vcredist");
    }

    [Fact]
    public void CrashCandidates_SkipWindowsShell() 
    {
        var s = Healthy();
        s.Crashes = new[] { new CrashInfo("explorer.exe", "ntdll.dll", "c0000374", 9, DateTime.UtcNow) };
        Assert.Empty(HealthTriage.CrashCandidates(s));
    }

    [Fact]
    public void InternetLooksFine_ButProxyDead_ProxyComesFirst()
    {
        var s = Healthy();
        s.UserProxyEnabled = true; s.UserProxyServer = "127.0.0.1:9"; s.UserProxyReachable = Tri.No;
        s.Services = s.Services.Select(x => x.Name is "Themes" or "Dnscache" ? x with { StartType = "Disabled" } : x).ToList();
        s.Crashes = new[] { new CrashInfo("Game.exe", "game.dll", "c0000005", 5, DateTime.UtcNow) };
        var net = new Diagnosis(new object(), new[] { new Finding(FindingCode.LooksNormal, "ok", "") },
            new[] { new Rung("flush_dns", "network.flush_dns", 0, "") }, false);
        var d = HealthTriage.CombineNetwork(net, s);
        Assert.Equal(FindingCode.HealthDeadProxy, d.Findings[0].Code);
        Assert.DoesNotContain(d.Findings, f => f.Code is FindingCode.LooksNormal or FindingCode.HealthProgramCrashing);
        Assert.Contains("DNS Client", d.Findings.First(f => f.Code == FindingCode.HealthServiceOff).PlainMessage);
        Assert.DoesNotContain("Themes", d.Findings.First(f => f.Code == FindingCode.HealthServiceOff).PlainMessage);
        Assert.Equal(new[] { "network.reset_user_proxy", "service.restore_default", "network.flush_dns" }, d.Ladder.Select(r => r.ActionId));
        Assert.Equal("Dnscache", d.Ladder[1].Arg);
    }

    [Fact]
    public void HealthCauses_ComeBeforeTheInternetFinding_AndHealthyExtrasChangeNothing()
    {
        var net = new Diagnosis(new object(), new[] { new Finding(FindingCode.ProbeIncomplete, "x", "") }, Array.Empty<Rung>(), false);
        Assert.Same(net, HealthTriage.CombineNetwork(net, Healthy()));
        var s = Healthy(); s.ClockOffsetSeconds = 4000;
        var d = HealthTriage.CombineNetwork(net, s);
        Assert.Equal(FindingCode.HealthClockWrong, d.Findings[0].Code);
        Assert.Equal(FindingCode.ProbeIncomplete, d.Findings[1].Code);
    }

    [Fact]
    public void FailingDrive_ComesFirst_BackupFirst_PointsAtHardware()
    {
        var s = Healthy();
        s.ClockOffsetSeconds = 900;
        s.Drives = new[] { new PhysicalDriveInfo("SSD 1", "SSD", "Healthy", new[] { 2, 5 }, false), new PhysicalDriveInfo("HDD", "HDD", "Healthy", new[] { 2 }, false) };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthDriveFailing, f[0].Code);
        Assert.Contains("\"SSD 1\"", f[0].PlainMessage);
        Assert.Equal("back_up_files", l[0].Id);
        Assert.True(HealthTriage.ProbablyHardware(s));
        Assert.Contains("replace the drive", HealthTriage.NotFixedHint(f));
    }

    [Theory]
    [InlineData("Healthy", new[] { 2 }, false, false, false)]
    [InlineData("Warning", new[] { 2 }, false, false, true)]
    [InlineData("Healthy", new[] { 3 }, false, false, true)]
    [InlineData("Unhealthy", new[] { 2 }, false, true, false)]
    [InlineData("Healthy", new[] { 2 }, true, true, false)]
    [InlineData("Unknown", new int[0], false, false, false)]
    public void DriveVerdicts(string health, int[] status, bool predict, bool failing, bool warning)
    {
        var d = new PhysicalDriveInfo("d", "SSD", health, status, predict);
        Assert.Equal((failing, warning), (d.Failing, d.Warning));
    }

    [Fact]
    public void DriveWarning_IsNotHardwareYet_ButOffersBackup()
    {
        var s = Healthy();
        s.Drives = new[] { new PhysicalDriveInfo("HDD", "HDD", "Warning", new[] { 2 }, false) };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthDriveWarning, f[0].Code);
        Assert.Equal("backup", l[0].Arg);
        Assert.False(HealthTriage.ProbablyHardware(s));
    }

    [Fact]
    public void WrongClock_WithAutomaticTimeOff_GuidesToSwitchItOnFirst()
    {
        var s = Healthy(); s.ClockOffsetSeconds = -7200; s.AutoTimeOff = true;
        var (f, l) = Run(s);
        Assert.Contains("Set time automatically", f[0].PlainMessage);
        Assert.Equal(new[] { "auto_time_on", "sync_clock" }, l.Take(2).Select(r => r.Id));
        s.AutoTimeOff = false;
        Assert.Equal("sync_clock", Run(s).L[0].Id);
    }

    [Fact]
    public void SingleCrash_IsNotAFault()
    {
        var s = Healthy();
        s.Crashes = new[] { new CrashInfo("Game.exe", "game.dll", "c0000005", 1, DateTime.UtcNow) };
        Assert.Equal(FindingCode.LooksNormal, Run(s).F[0].Code);
    }

    [Fact]
    public void ShellCrash_RestartsExplorerAndRepairsSystemFiles()
    {
        var s = Healthy();
        s.Crashes = new[] { new CrashInfo("StartMenuExperienceHost.exe", "twinapi.appcore.dll", "c000027b", 3, DateTime.UtcNow) };
        var l = Run(s).L.Select(r => r.ActionId).ToList();
        Assert.True(l.IndexOf("shell.restart_explorer") < l.IndexOf("repair.dism"));
        Assert.True(l.IndexOf("repair.dism") < l.IndexOf("repair.sfc"));
    }

    [Theory]
    [InlineData("VCRUNTIME140.dll", "c0000005", CrashCause.Runtime)]
    [InlineData("ucrtbase.dll", "c0000409", CrashCause.Runtime)]
    [InlineData("KERNELBASE.dll", "e0434352", CrashCause.DotNet)]
    [InlineData("coreclr.dll", "c0000005", CrashCause.DotNet)]
    [InlineData("unknown", "80131623", CrashCause.DotNet)]
    [InlineData("nvwgf2umx.dll", "c0000005", CrashCause.Graphics)]
    [InlineData("ntdll.dll", "c0000374", CrashCause.WindowsFiles)]
    [InlineData("unknown", "c0000135", CrashCause.MissingFile)]
    [InlineData("Game.exe", "c0000005", CrashCause.Program)]
    [InlineData("libcef.dll", "80000003", CrashCause.Program)]
    [InlineData(null, null, CrashCause.Unknown)]
    public void CrashCause_FromModuleAndCode(string? module, string? code, CrashCause expected) =>
        Assert.Equal(expected, CrashCauses.Classify(module, code, "Game.exe"));

    [Fact]
    public void Hosts_FlagsRedirectsAndSecurityBlocks_NotDeliberateBlocks()
    {
        var lines = new[]
        {
            "# comment 1.2.3.4 www.paypal.com",
            "127.0.0.1 localhost",
            "1.2.3.4 www.paypal.com",
            "0.0.0.0 www.malwarebytes.com",
            "0.0.0.0 vortex.data.microsoft.com",
            "0.0.0.0 ads.example.net",
            "192.168.1.10 nas.home",
        };
        var bad = HostsFile.Suspicious(lines).Select(e => e.Host).ToList();
        Assert.Equal(new[] { "www.paypal.com", "www.malwarebytes.com" }, bad);
        var cleaned = HostsFile.Clean(lines, new DateTime(2026, 11, 12));
        Assert.Equal(lines.Length, cleaned.Length);
        Assert.StartsWith(HostsFile.Marker, cleaned[2]);
        Assert.Equal(lines[6], cleaned[6]);
        Assert.Empty(HostsFile.Suspicious(cleaned));
    }

    [Fact]
    public void SystemPath_AddsOnlyMissingWindowsFolders_KeepsTheRest()
    {
        const string root = @"C:\WINDOWS";
        var healthy = @"C:\WINDOWS\system32;C:\WINDOWS;C:\WINDOWS\System32\Wbem;C:\WINDOWS\System32\WindowsPowerShell\v1.0\;C:\Program Files\Git\cmd";
        Assert.Empty(SystemPath.Missing(healthy, root));
        Assert.Empty(SystemPath.Missing(@"%SystemRoot%\system32;%SystemRoot%;%SYSTEMROOT%\System32\Wbem;%SYSTEMROOT%\System32\WindowsPowerShell\v1.0\", root));
        var damaged = @"C:\Program Files\Git\cmd;C:\Tools";
        Assert.Equal(4, SystemPath.Missing(damaged, root).Count);
        var repaired = SystemPath.Repair(damaged, root);
        Assert.Empty(SystemPath.Missing(repaired, root));
        Assert.EndsWith(damaged, repaired);
    }

    [Fact]
    public void UpdateErrorCodes_ReadAsHex()
    {
        Assert.Equal("0x80073d02", RealHealthProbe.HexCode("-2147009278"));
        Assert.Equal("0x80240016", RealHealthProbe.HexCode("0x80240016"));
    }

    [Fact]
    public void ProxyList_Parses()
    {
        Assert.Equal(new[] { ("127.0.0.1", 9) }, RealHealthProbe.ParseProxyList("127.0.0.1:9"));
        Assert.Equal(new[] { ("a", 1), ("b", 2) }, RealHealthProbe.ParseProxyList("http=a:1;https=b:2"));
        Assert.Equal(new[] { ("proxy", 80) }, RealHealthProbe.ParseProxyList("http://proxy/"));
    }

    [Fact]
    public void StoreApps_ParseSingleAndArray()
    {
        Assert.Single(RealHealthProbe.ParseStoreApps("{\"PackageFullName\":\"A_1_x64__x\",\"Name\":\"A\",\"Status\":\"Modified\"}"));
        Assert.Equal(2, RealHealthProbe.ParseStoreApps("[{\"PackageFullName\":\"A_1\",\"Name\":\"A\",\"Status\":\"Tampered\"},{\"PackageFullName\":\"B_1\",\"Name\":\"B\",\"Status\":\"NeedsRemediation\"}]").Count);
        Assert.Empty(RealHealthProbe.ParseStoreApps(""));
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("A; Remove-Item C:\\", false)]
    [InlineData("A$(calc)", false)]
    [InlineData("", false)]
    public void PackageNames_AreValidated(string name, bool ok) => Assert.Equal(ok, UserRepairs.IsValidPackageFullName(name));

    [Theory]
    [InlineData("repair.sfc", "C:\\Windows")]
    [InlineData("repair.dism", "online & calc")]
    [InlineData("service.restore_default", "NotAListedService")]
    [InlineData("runtime.install_vcredist", "x64 /evil")]
    [InlineData("disk.scan", "D")]
    [InlineData("disk.scan", "C: /f")]
    [InlineData("network.clean_hosts", "other")]
    public void Executor_RejectsAnythingButTheFixedArguments(string action, string arg) =>
        Assert.Equal(2, ExecutorProgram.Main(new[] { action, arg }));

    [Fact]
    public void Resolved_IgnoresHistory_ButNotLiveProblems()
    {
        var before = new[] { new Finding(FindingCode.HealthDeadProxy, "", ""), new Finding(FindingCode.HealthBlueScreens, "", "") };
        Assert.True(HealthTriage.Resolved(before, new[] { new Finding(FindingCode.HealthBlueScreens, "", "") }));
        Assert.False(HealthTriage.Resolved(before, new[] { new Finding(FindingCode.HealthDeadProxy, "", "") }));
        Assert.False(HealthTriage.Resolved(before, new[] { new Finding(FindingCode.ProbeIncomplete, "", "") }));
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var s = Healthy();
        s.HardwareErrors = 1; s.DiskErrors = 2; s.ClockOffsetSeconds = 999; s.UserProxyEnabled = true; s.UserProxyServer = "x:1";
        s.UserProxyReachable = Tri.No; s.WinHttpProxy = "y:2"; s.WinHttpProxyReachable = Tri.No;
        s.HostsHijacks = new[] { new HostsHijack("www.paypal.com", "1.2.3.4") };
        s.MissingPathEntries = new[] { @"%SystemRoot%" }; s.TempFolderMissing = true; s.ThirdPartyWinsock = new[] { "x.dll" };
        s.BrokenStoreApps = new[] { new StoreAppInfo("Calc_1", "Calculator", "Modified") };
        s.Crashes = new[] { new CrashInfo("explorer.exe", "ntdll.dll", "c0000374", 3, DateTime.UtcNow), new CrashInfo("App.exe", "nvwgf2umx.dll", "c0000005", 2, DateTime.UtcNow) };
        s.Drives = new[] { new PhysicalDriveInfo("Disk", "SSD", "Unhealthy", new[] { 6 }, false) };
        s.BlueScreens = 2; s.RuntimeErrors = new[] { "app.exe" }; s.FailingServices = new[] { "Foo" }; s.UpdateFailures = 3; s.RebootPending = Tri.Yes;
        s.Services = s.Services.Select(x => x.Name == "Themes" ? x with { StartType = "Disabled" } : x).ToList();
        var (f, l) = Run(s);
        var texts = f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).Append(HealthTriage.NotFixedHint(f) ?? "").ToList();
        foreach (var t in texts)
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
            Assert.Contains("Windows will ask for your permission", r.Say);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        Assert.All(l, r => Assert.True(r.Tier <= 1));
        Assert.True(HealthTriage.ProbablyHardware(s));
    }

    private static RecentChange Change(RecentChangeKind k, string name, int daysAgo) => new(k, name, DateTime.UtcNow.AddDays(-daysAgo));

    [Fact]
    public void RecentChanges_AloneAreNotAFinding()
    {
        var s = Healthy();
        s.RecentChanges = new[] { Change(RecentChangeKind.Program, "Sample Tool", 1) };
        Assert.Equal(FindingCode.LooksNormal, Run(s).F[0].Code);
        s.RebootPending = Tri.Yes; // a pending restart is the result of a change, not a second problem
        Assert.DoesNotContain(Run(s).F, x => x.Code == FindingCode.HealthRecentChanges);
    }

    [Fact]
    public void RecentChanges_AreListedWhenAnotherProblemExists()
    {
        var s = Healthy();
        s.BlueScreens = 1;
        s.RecentChanges = new[] { Change(RecentChangeKind.Program, "Sample Tool", 1), Change(RecentChangeKind.Update, "Sample Update KB1", 2) };
        s.RestorePointsKnown = Tri.Unknown;
        var (f, l) = Run(s);
        var c = f.Single(x => x.Code == FindingCode.HealthRecentChanges);
        Assert.Contains("programs or updates were installed shortly before the problems started", c.PlainMessage);
        Assert.Contains("Sample Tool", c.TechnicalDetail);
        Assert.Contains(l, r => r.Id == "uninstall_recent_program" && r.Arg == "apps");
        Assert.Contains(l, r => r.Id == "uninstall_recent_update" && r.Arg == "updatehistory");
        Assert.Contains(l, r => r.Id == "system_restore" && r.ActionId == "ui.human_step" && r.Say.Contains("rstrui.exe"));
    }

    [Fact]
    public void RestorePoint_OnlyOfferedWhenUnknownOrOlderThanTheFirstChange()
    {
        var s = Healthy();
        s.BlueScreens = 1;
        s.RecentChanges = new[] { Change(RecentChangeKind.Driver, "SampleDriver", 3) };
        s.RestorePointsKnown = Tri.Yes;
        s.RestorePoints = new[] { DateTime.UtcNow.AddDays(-1) };
        Assert.DoesNotContain(Run(s).L, r => r.Id == "system_restore");
        s.RestorePoints = new[] { DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-10) };
        Assert.Contains(Run(s).L, r => r.Id == "system_restore");
        Assert.DoesNotContain(Run(s).L, r => r.Id is "uninstall_recent_program" or "uninstall_recent_update");
    }

    [Fact]
    public void GraphicsResets_GiveDriverStepsAndAreHardwareOnlyWhenVeryFrequent()
    {
        var s = Healthy();
        s.GraphicsResets = 3;
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthGraphicsResets, f[0].Code);
        Assert.Contains("3 times", f[0].PlainMessage);
        Assert.Contains(l, r => r.Id == "graphics_reset_updates" && r.Arg == "windowsupdate");
        Assert.Contains(l, r => r.Id == "graphics_reset_maker" && r.ActionId == "ui.human_step" && r.Say.Contains("NVIDIA"));
        Assert.False(HealthTriage.ProbablyHardware(s));
        s.GraphicsResets = 20;
        Assert.True(HealthTriage.ProbablyHardware(s));
    }

    [Theory]
    [InlineData("20261109", true)]
    [InlineData("2026-11-09", false)]
    [InlineData("", false)]
    [InlineData("20261399", false)]
    public void InstallDates_AreParsed(string v, bool ok) => Assert.Equal(ok, RealHealthProbe.ParseInstallDate(v).HasValue);

    [Fact]
    public void RecentChangeAndGraphicsText_FollowTheWritingGuide()
    {
        var s = Healthy();
        s.BlueScreens = 1; s.GraphicsResets = 25;
        s.RecentChanges = new[] { Change(RecentChangeKind.Program, "Sample Tool", 1), Change(RecentChangeKind.Update, "Sample Update", 2), Change(RecentChangeKind.Driver, "SampleDriver", 2) };
        var (f, l) = Run(s);
        foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)))
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        Assert.All(l, r => Assert.True(r.Tier <= 1));
    }

    // ---- blue-screen causes and virtual memory ----

    [Theory]
    [InlineData("The bugcheck was: 0x0000001a (0x0000000000041790, 0xfffffa8000c3d6f0, 0x000000000000ffff, 0x0000000000000000). A dump was saved in: C:\\Windows\\MEMORY.DMP.", 0x1Au)]
    [InlineData("0x00000124 (0x0000000000000000, 0xffffd30a1b2c3d40, 0x00000000bf800000, 0x0000000000000124)", 0x124u)]
    [InlineData("0x000000d1", 0xD1u)]
    [InlineData("0x1a", 0x1Au)]
    public void StopCodes_AreParsedFromTextAndParameters(string text, uint expected) => Assert.Equal(expected, StopCodes.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The computer has rebooted from a bugcheck.")]
    [InlineData("0xZZ")]
    public void StopCodes_NoCodeGivesNull(string? text) => Assert.Null(StopCodes.Parse(text));

    [Fact]
    public void StopCodes_AreCountedMostFrequentFirst()
    {
        var c = StopCodes.Count(new uint[] { 0x1A, 0xD1, 0xD1, 0x1A, 0xD1 });
        Assert.Equal(0xD1u, c[0].Code);
        Assert.Equal(3, c[0].Count);
        Assert.Equal(2, c[1].Count);
    }

    [Theory]
    [InlineData(0x1Au, StopCategory.Memory)]
    [InlineData(0x50u, StopCategory.Memory)]
    [InlineData(0x19u, StopCategory.Memory)]
    [InlineData(0x3Bu, StopCategory.Driver)]
    [InlineData(0x7Eu, StopCategory.Driver)]
    [InlineData(0x0Au, StopCategory.Driver)]
    [InlineData(0xD1u, StopCategory.Driver)]
    [InlineData(0xC2u, StopCategory.Driver)]
    [InlineData(0x133u, StopCategory.Driver)]
    [InlineData(0x139u, StopCategory.Driver)]
    [InlineData(0x9Fu, StopCategory.Driver)]
    [InlineData(0x116u, StopCategory.Graphics)]
    [InlineData(0x117u, StopCategory.Graphics)]
    [InlineData(0x7Au, StopCategory.Storage)]
    [InlineData(0x154u, StopCategory.Storage)]
    [InlineData(0xF4u, StopCategory.Storage)]
    [InlineData(0x124u, StopCategory.Other)]
    [InlineData(0xEFu, StopCategory.Other)]
    public void StopCodes_CommonCodesHaveAMeaning(uint code, StopCategory category)
    {
        var m = StopCodes.Meaning(code);
        Assert.NotNull(m);
        Assert.Equal(category, m!.Category);
        Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", m.Explanation);
    }

    [Fact]
    public void StopCodes_UnknownCodeHasNoMeaning_AndOnly124IsHardware()
    {
        Assert.Null(StopCodes.Meaning(0xDEADu));
        Assert.True(StopCodes.IsHardware(0x124));
        Assert.False(StopCodes.IsHardware(0x1A));
    }

    [Fact]
    public void BlueScreen_NamesTheMostFrequentCode()
    {
        var s = Healthy();
        s.BlueScreens = 3;
        s.StopCodes = new[] { new StopCodeCount(0x1A, 2), new StopCodeCount(0xD1, 1) };
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.HealthBlueScreens, f[0].Code);
        Assert.Contains("0x1A", f[0].PlainMessage);
        Assert.Contains("memory", f[0].PlainMessage);
        Assert.Contains("0xD1", f[0].TechnicalDetail);
        var diag = Assert.Single(l, r => r.Id == "memory_diagnostic");
        Assert.Equal("ui.human_step", diag.ActionId);
        Assert.Contains("mdsched.exe", diag.Say);
        Assert.Contains(l, r => r.Id == "bluescreen_driver_updates" && r.Arg == "windowsupdate");
        Assert.Contains(l, r => r.Id == "bluescreen_driver_maker");
        Assert.DoesNotContain(l, r => r.ActionId == "disk.scan");
    }

    [Fact]
    public void BlueScreen_WithoutCodes_KeepsTheShortText_AndStorageCodesGiveDiskScan()
    {
        var s = Healthy();
        s.BlueScreens = 1;
        var (f, l) = Run(s);
        Assert.DoesNotContain("stop code", f[0].PlainMessage);
        Assert.DoesNotContain(l, r => r.Id == "memory_diagnostic");
        s.StopCodes = new[] { new StopCodeCount(0x7A, 1) };
        Assert.Contains(Run(s).L, r => r.ActionId == "disk.scan" && r.Arg == "C");
        s.StopCodes = new[] { new StopCodeCount(0x116, 1) };
        Assert.Contains(Run(s).L, r => r.Id == "bluescreen_driver_updates" && r.Say.Contains("display driver"));
    }

    [Fact]
    public void BlueScreen_Code124_IsProbablyHardware()
    {
        var s = Healthy();
        s.BlueScreens = 1;
        s.StopCodes = new[] { new StopCodeCount(0x124, 1) };
        Assert.True(HealthTriage.ProbablyHardware(s));
        s.StopCodes = new[] { new StopCodeCount(0x1A, 1) };
        Assert.False(HealthTriage.ProbablyHardware(s));
    }

    private static PageFileState Pf(bool? auto, params long[] allocatedMb) =>
        new(auto, 16384, Array.Empty<PageFileSetting>(), allocatedMb.Select((a, i) => new PageFileUse($"C:\\pagefile{i}.sys", a, 10)).ToList());

    [Theory]
    [InlineData(false, new long[0], true)]
    [InlineData(false, new long[] { 512 }, true)]
    [InlineData(false, new long[] { 512, 256 }, true)]
    [InlineData(false, new long[] { 1024 }, false)]
    [InlineData(false, new long[] { 4096 }, false)]
    [InlineData(true, new long[0], false)]
    [InlineData(true, new long[] { 256 }, false)]
    public void PageFile_FindingOnlyWhenManualAndTooSmall(bool auto, long[] sizes, bool expected)
    {
        var s = Healthy();
        s.PageFile = Pf(auto, sizes);
        Assert.Equal(expected, Run(s).F.Any(x => x.Code == FindingCode.HealthPageFileTooSmall));
    }

    [Fact]
    public void PageFile_Unreadable_GivesNoFinding()
    {
        var s = Healthy();
        Assert.DoesNotContain(Run(s).F, x => x.Code == FindingCode.HealthPageFileTooSmall);
        s.PageFile = Pf(null);
        Assert.DoesNotContain(Run(s).F, x => x.Code == FindingCode.HealthPageFileTooSmall);
    }

    [Fact]
    public void PageFile_Finding_UsesLowMemoryEventsAsEvidence_AndGivesRungs()
    {
        var s = Healthy();
        s.PageFile = Pf(false);
        var (f, l) = Run(s);
        Assert.Contains("switched off", f[0].PlainMessage);
        Assert.DoesNotContain("low virtual memory", f[0].PlainMessage);
        s.LowMemoryEvents = 2;
        s.PageFile = Pf(false, 300);
        (f, l) = Run(s);
        Assert.Contains("300 MB", f[0].PlainMessage);
        Assert.Contains("low virtual memory twice", f[0].PlainMessage);
        var fix = Assert.Single(l, r => r.ActionId == "system.pagefile_auto");
        Assert.Equal("system", fix.Arg);
        Assert.Equal(1, fix.Tier);
        Assert.Contains("restarts", fix.Say);
        Assert.Contains("Windows will ask for your permission.", fix.Say);
        Assert.Contains("system.pagefile_auto", FixActions.ElevatedActions);
        Assert.Contains(l.SkipWhile(r => r.Id != fix.Id), r => r.Id == "restart_after_pagefile" && r.ActionId == "ui.human_step");
        Assert.Contains(HealthTriage.Rechecked, c => c == FindingCode.HealthPageFileTooSmall);
    }

    [Theory]
    [InlineData("system.pagefile_auto", "C:\\")]
    [InlineData("system.pagefile_auto", "System")]
    [InlineData("system.pagefile_auto", "other")]
    [InlineData("system.pagefile_auto", "")]
    public void Executor_PageFileAction_RejectsAnythingButSystem(string action, string arg) =>
        Assert.Equal(2, ExecutorProgram.Main(new[] { action, arg }));

    [Fact]
    public void BlueScreenAndPageFileText_FollowTheWritingGuide()
    {
        var s = Healthy();
        s.BlueScreens = 6; s.LowMemoryEvents = 1;
        s.StopCodes = new[] { new StopCodeCount(0x1A, 2), new StopCodeCount(0xD1, 2), new StopCodeCount(0x116, 1), new StopCodeCount(0x7A, 1), new StopCodeCount(0x124, 1) };
        s.PageFile = Pf(false, 200);
        var (f, l) = Run(s);
        foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)))
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        Assert.All(l.Where(r => r.Tier == 1 && !Probes.DiagResults.IsDiagAction(r.ActionId)), r => Assert.EndsWith("Windows will ask for your permission.", r.Say));
    }
}
