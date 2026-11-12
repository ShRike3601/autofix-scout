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
}
