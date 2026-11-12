using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Microsoft.Win32;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class SecurityTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private const int DefenderOnUpToDate = 0x61100;   // 397568, as read on the test bench
    private const int DefenderOff = 0x60100;
    private const int OutOfDate = 0x61110;

    private static SecurityState Healthy() => new()
    {
        CheckedUtc = Now,
        Antivirus = new[] { new SecurityProduct("Windows Defender", DefenderOnUpToDate, "windowsdefender://") },
        DefenderRealTime = Tri.Yes,
        DefenderSignaturesUtc = Now.AddHours(-5),
        FirewallProfiles = new Dictionary<string, Tri> { ["Domain"] = Tri.Yes, ["Private"] = Tri.Yes, ["Public"] = Tri.Yes },
        FirewallService = new UpdateServiceInfo("mpssvc", "Running", "Automatic"),
        UacEnabled = Tri.Yes,
        UacAdminPrompt = 5,
    };

    private static (IReadOnlyList<Finding> F, IReadOnlyList<Rung> L) Run(SecurityState s)
    {
        var f = SecurityTriage.Evaluate(s);
        return (f, SecurityTriage.Ladder(s, f));
    }

    [Theory]
    [InlineData(0x61100, true, true)]
    [InlineData(0x60100, false, true)]
    [InlineData(0x61110, true, false)]
    [InlineData(0x41000, true, true)]   // third-party, on, up to date
    [InlineData(0x40000, false, true)]
    [InlineData(0x62100, false, true)]  // snoozed
    public void ProductState_IsDecoded(int state, bool enabled, bool upToDate)
    {
        Assert.Equal(enabled, SecurityTriage.IsEnabled(state));
        Assert.Equal(upToDate, SecurityTriage.IsUpToDate(state));
    }

    [Fact]
    public void Healthy_LooksNormal_AndOffersAQuickScan()
    {
        var (f, l) = Run(Healthy());
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(f).Code);
        Assert.Equal("security.quick_scan", Assert.Single(l).ActionId);
    }

    [Fact]
    public void NothingRead_IsIncomplete_WithNoSteps()
    {
        var (f, l) = Run(new SecurityState { ProbeErrors = new[] { "antivirus: denied" } });
        Assert.Equal(FindingCode.ProbeIncomplete, f[0].Code);
        Assert.Empty(l);
    }

    [Fact]
    public void DefenderDetailsDenied_StaysUnknown_NotAProblem()
    {
        var s = Healthy(); s.DefenderRealTime = Tri.Unknown; s.DefenderSignaturesUtc = null; s.DefenderNote = "Access denied";
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(SecurityTriage.Evaluate(s)).Code);
    }

    [Fact]
    public void RealTimeOff_OpensWindowsSecurity()
    {
        var s = Healthy(); s.Antivirus = new[] { new SecurityProduct("Windows Defender", DefenderOff, "windowsdefender://") }; s.DefenderRealTime = Tri.No;
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.SecurityRealTimeOff);
        var r = l.First(x => x.ActionId == "ui.open_settings_page");
        Assert.Equal("windowsdefender", r.Arg);
        Assert.Contains("Real-time protection", r.Say);
    }

    [Fact]
    public void NoAntivirusAtAll_IsFound()
    {
        var s = Healthy(); s.Antivirus = Array.Empty<SecurityProduct>(); s.DefenderRealTime = Tri.Unknown;
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.SecurityNoAntivirus);
        Assert.DoesNotContain(l, r => r.ActionId == "security.quick_scan");
    }

    [Fact]
    public void ThirdPartyAntivirus_DefenderPassive_IsNormal_AndScanIsAHumanStep()
    {
        var s = Healthy();
        s.Antivirus = new[] { new SecurityProduct("Windows Defender", 0x60100, "windowsdefender://"), new SecurityProduct("Contoso AV", 0x41000) };
        s.DefenderRealTime = Tri.No;
        var (f, l) = Run(s);
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(f).Code);
        Assert.Contains(l, r => r.ActionId == "ui.human_step" && r.Say.Contains("Contoso AV"));
    }

    [Fact]
    public void OldSignatures_AreUpdated()
    {
        var s = Healthy(); s.DefenderSignaturesUtc = Now.AddDays(-8);
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.SecuritySignaturesOld);
        var r = l.First(x => x.ActionId == "security.update_signatures");
        Assert.DoesNotContain(r.ActionId, FixActions.ElevatedActions);
        s.DefenderSignaturesUtc = Now.AddDays(-6);
        Assert.DoesNotContain(SecurityTriage.Evaluate(s), x => x.Code == FindingCode.SecuritySignaturesOld);
    }

    [Fact]
    public void ThirdPartyOutOfDate_AsksThePersonToUpdateIt()
    {
        var s = Healthy(); s.Antivirus = new[] { new SecurityProduct("Contoso AV", 0x41010) };
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.SecuritySignaturesOld);
        Assert.Contains(l, r => r.Id == "update_other_antivirus");
        Assert.DoesNotContain(l, r => r.ActionId == "security.update_signatures");
    }

    [Fact]
    public void FirewallOffOnPublic_IsTurnedOnByTheExecutor()
    {
        var s = Healthy(); s.FirewallProfiles = new Dictionary<string, Tri> { ["Domain"] = Tri.Yes, ["Private"] = Tri.Yes, ["Public"] = Tri.No };
        var (f, l) = Run(s);
        Assert.Contains("public networks", f.First(x => x.Code == FindingCode.SecurityFirewallOff).PlainMessage);
        var r = l.First(x => x.ActionId == "security.firewall_on");
        Assert.Equal("system", r.Arg);
        Assert.Contains("security.firewall_on", FixActions.ElevatedActions);
    }

    [Fact]
    public void FirewallOff_WithAThirdPartyFirewall_IsNormal()
    {
        var s = Healthy(); s.FirewallProfiles = new Dictionary<string, Tri> { ["Public"] = Tri.No };
        s.Firewalls = new[] { new SecurityProduct("Contoso Firewall", 0x41000) };
        Assert.DoesNotContain(SecurityTriage.Evaluate(s), x => x.Code == FindingCode.SecurityFirewallOff);
    }

    [Fact]
    public void FirewallServiceDisabled_IsRestoredFirst()
    {
        var s = Healthy(); s.FirewallService = new UpdateServiceInfo("mpssvc", "Stopped", "Disabled");
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.SecurityFirewallOff);
        Assert.Equal("mpssvc", l.First(r => r.ActionId == "service.restore_default").Arg);
    }

    [Fact]
    public void UacOff_IsTurnedOn_ThenARestart_AtTheEnd()
    {
        var s = Healthy(); s.UacEnabled = Tri.No;
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.SecurityUacOff);
        var i = l.ToList().FindIndex(r => r.ActionId == "security.uac_on");
        Assert.Contains("restart", l[i].Say);
        Assert.Equal("ui.human_step", l[i + 1].ActionId);
        Assert.Equal(l.Count - 2, i);
        Assert.Contains("restart", SecurityTriage.NotFixedHint(f)!);
    }

    [Fact]
    public void UacNeverAsks_IsAlsoFound()
    {
        var s = Healthy(); s.UacAdminPrompt = 0;
        Assert.Contains(SecurityTriage.Evaluate(s), x => x.Code == FindingCode.SecurityUacOff);
    }

    [Theory]
    [InlineData("Off", null, true)]
    [InlineData("Warn", null, false)]
    [InlineData(null, null, false)]
    [InlineData(null, 0, true)]
    [InlineData("Warn", 1, false)]
    public void SmartScreen(string? setting, int? policy, bool off)
    {
        var s = Healthy(); s.SmartScreenSetting = setting; s.SmartScreenPolicy = policy;
        var (f, l) = Run(s);
        Assert.Equal(off, f.Any(x => x.Code == FindingCode.SecuritySmartScreenOff));
        Assert.Equal(off, l.Any(r => r.Arg == "windowsdefender_appbrowser"));
    }

    [Fact]
    public void HostsHijack_ReusesCleanHosts()
    {
        var s = Healthy(); s.Network = new HealthState { HostsHijacks = new[] { new HostsHijack("www.microsoft.com", "6.6.6.6") } };
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.HealthHostsHijacked);
        Assert.Contains(l, r => r.ActionId == "network.clean_hosts" && r.Arg == "system");
    }

    [Fact]
    public void DeadProxy_ReusesTheProxyReset()
    {
        var s = Healthy(); s.Network = new HealthState { UserProxyEnabled = true, UserProxyServer = "127.0.0.1:9", UserProxyReachable = Tri.No };
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.HealthDeadProxy);
        Assert.Contains(l, r => r.ActionId == "network.reset_user_proxy");
    }

    [Fact]
    public void SuspiciousStartup_IsAFindingOnly_SaysMayBe()
    {
        var s = Healthy(); s.SuspiciousStartups = new[] { new SuspiciousStartup(@"HKCU\Run", "xkqzvbtp", @"C:\Users\u\AppData\Local\Temp\xkqzvbtp.exe") };
        var (f, l) = Run(s);
        Assert.Contains("may be", f.First(x => x.Code == FindingCode.SecuritySuspiciousStartup).PlainMessage);
        Assert.Contains(l, r => r.Arg == "startupapps");
        Assert.DoesNotContain(l, r => r.ActionId == "startup.disable");
    }

    private const string Temp = @"C:\Users\u\AppData\Local\Temp\";
    private const string AppData = @"C:\Users\u\AppData\Roaming";
    private const string Public = @"C:\Users\Public";

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\xkqzvbtp.exe", true)]
    [InlineData(@"""C:\Users\u\AppData\Local\Temp\a8f3c91d0e.exe"" /run", true)]
    [InlineData(@"C:\Users\u\AppData\Roaming\r4nd0m7x.exe -silent", true)]
    [InlineData(@"C:\Users\Public\Libraries\qwrtzpl.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Roaming\Spotify\x8k2m9q4.exe", false)]   // a subfolder of roaming app data is normal
    [InlineData(@"C:\Users\u\AppData\Local\Temp\setup.exe", false)]           // name is not random
    [InlineData(@"C:\Users\u\AppData\Roaming\updater.exe", false)]
    [InlineData(@"C:\Program Files\Contoso\xkqzvbtp.exe", false)]             // not a temporary or shared folder
    [InlineData(@"C:\Users\u\AppData\Local\Temp\xkqzvbtp.dll", false)]
    [InlineData(@"C:\Windows\System32\OneDriveSetup.exe /thfirstsetup", false)]
    public void SuspiciousStartup_Heuristic(string command, bool suspicious) =>
        Assert.Equal(suspicious, SecurityTriage.IsSuspiciousStartup(command, Temp, AppData, Public));

    [Theory]
    [InlineData("xkqzvbtp", true)]
    [InlineData("a8f3c91d", true)]
    [InlineData("r4nd0m7x", true)]
    [InlineData("OneDriveSetup", false)]
    [InlineData("strength", false)]
    [InlineData("chrome", false)]
    [InlineData("setup64", false)]
    [InlineData("abc", false)]
    public void RandomLookingNames(string name, bool random) => Assert.Equal(random, SecurityTriage.IsRandomLookingName(name));

    [Theory]
    [InlineData("security.firewall_on", "other")]
    [InlineData("security.firewall_on", "System")]
    [InlineData("security.firewall_on", "system ")]
    [InlineData("security.firewall_on", "system & calc")]
    [InlineData("security.uac_on", "")]
    [InlineData("security.remove_browser_policies", "")]
    [InlineData("security.remove_browser_policies", "System")]
    [InlineData("security.remove_browser_policies", "HKCU")]
    [InlineData("security.remove_browser_policies", "system & calc")]
    [InlineData("security.remove_browser_policies_user", "system")]   // user action, not an executor action
    [InlineData("security.uac_on", "C:\\Windows")]
    [InlineData("security.update_signatures", "system")]   // not an executor action
    [InlineData("security.quick_scan", "system")]
    public void Executor_RejectsAnythingButSystem(string action, string arg) =>
        Assert.Equal(2, ExecutorProgram.Main(new[] { action, arg }));

    private static BrowserPolicies.Flag Flag(string hive, string browser, string name, string display) =>
        new(hive, browser, hive == "HKCU" ? @"Software\Policies\Google\Chrome" : @"Software\Policies\Google\Chrome", name, display);

    [Theory]
    [InlineData("Chrome", "", "HomepageLocation", true)]
    [InlineData("Edge", "", "ProxyPacUrl", true)]
    [InlineData("Chrome", "", "DefaultSearchProviderSearchURL", true)]
    [InlineData("Chrome", "ExtensionInstallForcelist", "1", true)]
    [InlineData("Chrome", "RestoreOnStartupURLs", "1", true)]
    [InlineData("Chrome", "", "PasswordManagerEnabled", false)]
    [InlineData("Chrome", "Other", "1", false)]
    [InlineData("Firefox", "Extensions\\Install", "1", true)]
    [InlineData("Firefox", "Homepage", "URL", true)]
    [InlineData("Firefox", "SearchEngines\\Add\\1", "Name", true)]
    [InlineData("Firefox", "", "HomepageLocation", false)]
    public void BrowserPolicies_Rule(string browser, string sub, string name, bool flagged) =>
        Assert.Equal(flagged, BrowserPolicies.IsHijackValue(browser, sub, name));

    [Fact]
    public void BrowserPolicies_ShowsExtensionIdsNotUrls()
    {
        Assert.Equal("abcdefghijklmnopabcdefghijklmnop", BrowserPolicies.DisplayFor("ExtensionInstallForcelist", "1", "abcdefghijklmnopabcdefghijklmnop;https://x.example/update.xml"));
        Assert.Equal("HomepageLocation", BrowserPolicies.DisplayFor("", "HomepageLocation", "https://x.example"));
    }

    [Fact]
    public void BrowserPolicy_HomeComputer_FlagsAndOffersOneAdminRemoval()
    {
        var s = Healthy();
        s.BrowserPolicyFlags = new[] { Flag("HKCU", "Chrome", "HomepageLocation", "HomepageLocation"), Flag("HKLM", "Edge", "ProxyServer", "ProxyServer") };
        var (f, l) = Run(s);
        var fnd = f.Single(x => x.Code == FindingCode.SecurityBrowserPolicy);
        Assert.Contains("Managed by your organization", fnd.PlainMessage);
        Assert.Contains("HomepageLocation", fnd.TechnicalDetail);
        Assert.DoesNotContain(l, r => r.ActionId == "security.remove_browser_policies_user"); // HKCU Policies is read-only unelevated
        var admin = Assert.Single(l, r => r.ActionId == "security.remove_browser_policies");
        Assert.Equal(1, admin.Tier); Assert.Equal("system", admin.Arg);
        Assert.Contains(l, r => r.ActionId == "ui.human_step" && r.Say.Contains("chrome://policy") && r.Say.Contains("edge://policy"));
    }

    [Fact]
    public void BrowserPolicy_OnlyUserValues_StillUsesTheAdminStep()
    {
        var s = Healthy(); s.BrowserPolicyFlags = new[] { Flag("HKCU", "Chrome", "HomepageLocation", "HomepageLocation") };
        Assert.Single(Run(s).L, r => r.ActionId == "security.remove_browser_policies");
    }

    [Fact]
    public void BrowserPolicy_DomainJoined_IsInformationOnly_NoRemoval()
    {
        var s = Healthy(); s.ManagedPc = true;
        s.BrowserPolicyFlags = new[] { Flag("HKLM", "Chrome", "HomepageLocation", "HomepageLocation"), Flag("HKCU", "Edge", "ProxyServer", "ProxyServer") };
        var (f, l) = Run(s);
        Assert.Contains(f, x => x.Code == FindingCode.SecurityBrowserPolicyManaged);
        Assert.DoesNotContain(f, x => x.Code == FindingCode.SecurityBrowserPolicy);
        Assert.DoesNotContain(l, r => r.ActionId.Contains("browser_policies"));
        Assert.DoesNotContain(l, r => r.Id == "check_browser_policy");
    }

    [Fact]
    public void BrowserPolicy_NoneFound_LooksNormal() =>
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(Run(Healthy()).F).Code);

    [Fact]
    public void BrowserPolicy_IsRechecked()
    {
        var before = new[] { new Finding(FindingCode.SecurityBrowserPolicy, "", "") };
        Assert.False(SecurityTriage.Resolved(before, before));
        Assert.True(SecurityTriage.Resolved(before, new[] { new Finding(FindingCode.LooksNormal, "", "") }));
    }

    [Fact]
    public void Executor_RejectsExtraArguments() =>
        Assert.Equal(2, ExecutorProgram.Main(new[] { "security.uac_on", "system", "extra" }));

    [Fact]
    public void Resolved_RecheckIgnoresStartupChoice()
    {
        var before = new[] { new Finding(FindingCode.SecurityFirewallOff, "", ""), new Finding(FindingCode.SecuritySuspiciousStartup, "", "") };
        Assert.True(SecurityTriage.Resolved(before, new[] { new Finding(FindingCode.SecuritySuspiciousStartup, "", "") }));
        Assert.False(SecurityTriage.Resolved(before, new[] { new Finding(FindingCode.SecurityFirewallOff, "", "") }));
    }

    [Fact]
    public void Flow_Constants()
    {
        Assert.Equal("security", SecurityFlow.Topic);
        Assert.EndsWith("?", SecurityFlow.TestQuestion);
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var all = Healthy();
        all.Antivirus = new[] { new SecurityProduct("Windows Defender", 0x60110, "windowsdefender://") }; all.DefenderRealTime = Tri.No;
        all.FirewallProfiles = new Dictionary<string, Tri> { ["Domain"] = Tri.No, ["Private"] = Tri.No, ["Public"] = Tri.No };
        all.FirewallService = new UpdateServiceInfo("mpssvc", "Stopped", "Disabled");
        all.UacEnabled = Tri.No; all.SmartScreenSetting = "Off"; all.DefenderSignaturesUtc = Now.AddDays(-30);
        all.Network = new HealthState { HostsHijacks = new[] { new HostsHijack("www.paypal.com", "1.2.3.4") }, UserProxyEnabled = true, UserProxyServer = "x:1", UserProxyReachable = Tri.No, WinHttpProxy = "y:2", WinHttpProxyReachable = Tri.No };
        all.SuspiciousStartups = new[] { new SuspiciousStartup(@"HKCU\Run", "xkqzvbtp", @"C:\Temp\xkqzvbtp.exe") };
        all.BrowserPolicyFlags = new[] { Flag("HKCU", "Chrome", "HomepageLocation", "HomepageLocation"), Flag("HKLM", "Edge", "ProxyServer", "ProxyServer") };
        var managed = Healthy(); managed.ManagedPc = true; managed.BrowserPolicyFlags = all.BrowserPolicyFlags;
        var third = Healthy(); third.Antivirus = new[] { new SecurityProduct("Contoso AV", 0x41010) };
        var none = Healthy(); none.Antivirus = Array.Empty<SecurityProduct>();
        foreach (var s in new[] { all, third, none, managed, Healthy() })
        {
            var (f, l) = Run(s);
            var texts = f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).Append(SecurityTriage.NotFixedHint(f) ?? "").Append(SecurityFlow.TestQuestion);
            foreach (var t in texts)
                Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
            foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
            {
                Assert.Contains("Windows will ask for your permission", r.Say);
                Assert.Equal(1, r.Tier);
            }
            Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
            Assert.All(l, r => Assert.True(r.Tier <= 1));
        }
        var codes = SecurityTriage.Evaluate(all).Select(x => x.Code).ToHashSet();
        foreach (var c in new[] { FindingCode.SecurityRealTimeOff, FindingCode.SecuritySignaturesOld, FindingCode.SecurityFirewallOff, FindingCode.SecurityUacOff,
                                  FindingCode.SecuritySmartScreenOff, FindingCode.HealthHostsHijacked, FindingCode.SecuritySuspiciousStartup, FindingCode.SecurityBrowserPolicy })
            Assert.Contains(c, codes);
    }
}

/// <summary>Reads the real machine only (AFS_LIVE set): the healthy test bench must show no protection switched off.</summary>
[Collection("Live machine")]
public class LiveSecurityTests
{
    [Fact]
    public void SecurityProbe_ReadsThisMachine_AndFindsItProtected()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = SecurityFlow.Read();
        var f = SecurityTriage.Evaluate(s);
        var lines = new List<string>
        {
            $"security probe took {sw.Elapsed.TotalSeconds:0.0} s", "errors: " + string.Join(" | ", s.ProbeErrors),
            "antivirus: " + string.Join("; ", (s.Antivirus ?? Array.Empty<SecurityProduct>()).Select(a => $"{a.Name} 0x{a.ProductState:x} on={a.Enabled} current={a.UpToDate}")),
            $"defender: realtime={s.DefenderRealTime} signatures={s.DefenderSignaturesUtc:u} quickscanage={s.DefenderQuickScanAgeDays} note={s.DefenderNote}",
            "firewall: " + string.Join(", ", s.FirewallProfiles.Select(p => $"{p.Key}={p.Value}")) + $"; mpssvc {s.FirewallService?.Status}/{s.FirewallService?.StartType}",
            $"uac: {s.UacEnabled} prompt={s.UacAdminPrompt}; smartscreen: {s.SmartScreenSetting ?? "(default)"} policy={s.SmartScreenPolicy}",
            $"suspicious startup: {s.SuspiciousStartups.Count}; hosts hijacks: {s.Network.HostsHijacks.Count}",
        };
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage}"));
        lines.AddRange(SecurityTriage.Ladder(s, f).Select(r => $"RUNG {r.Id} {r.ActionId}({r.Arg})"));
        File.AppendAllLines(output, lines);
        Assert.Empty(s.ProbeErrors);
        Assert.NotNull(s.Antivirus);
        Assert.Equal(Tri.Yes, s.UacEnabled);
        Assert.Equal(3, s.FirewallProfiles.Count);
        foreach (var bad in new[] { FindingCode.SecurityNoAntivirus, FindingCode.SecurityRealTimeOff, FindingCode.SecurityFirewallOff, FindingCode.SecurityUacOff, FindingCode.ProbeIncomplete })
            Assert.DoesNotContain(f, x => x.Code == bad);
    }

    /// <summary>
    /// Creates a dummy Chrome HomepageLocation policy for the current user, checks it is found and removed by the user
    /// action, and always deletes what it created. Windows lets only administrators write HKCU\Software\Policies, so a
    /// standard user cannot create the real value; the test then runs the same code on a scratch key of its own
    /// (HKCU\Software\AutoFixScoutLiveTest), which is deleted afterwards.
    /// </summary>
    [Fact]
    public void BrowserPolicy_DummyHomepage_IsFoundAndRemoved_AndAlwaysCleanedUp()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        const string chrome = @"Software\Policies\Google\Chrome";
        const string scratch = @"Software\AutoFixScoutLiveTest";
        var lines = new List<string>();
        void Note(string l) => lines.Add("browser policy live test: " + l);
        var before = SecurityFlow.Read();
        if (BrowserPolicies.IsManagedPc() || before.BrowserPolicyFlags.Any(x => x.Hive == "HKCU"))
        {
            File.AppendAllLines(output, new[] { "browser policy live test skipped: managed PC or HKCU browser policies already present" });
            return;
        }
        var paths = new[] { @"Software\Policies", @"Software\Policies\Google", chrome };
        var existed = paths.Select(p => { using var k = Registry.CurrentUser.OpenSubKey(p); return k is not null; }).ToArray();
        var scratchExisted = false;
        using (var k = Registry.CurrentUser.OpenSubKey(scratch)) scratchExisted = k is not null;
        var started = DateTime.Now.AddSeconds(-1);
        RegistryKey? root = null;
        try
        {
            var real = true;
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(chrome);
                k.SetValue("HomepageLocation", "https://dummy.invalid/afs-test");
                root = Registry.CurrentUser;
            }
            catch (UnauthorizedAccessException)
            {
                real = false;
                Note("HKCU Software/Policies is not writable for this standard user (expected); using scratch key");
                root = Registry.CurrentUser.CreateSubKey(scratch);
                using var k = root.CreateSubKey(chrome);
                k.SetValue("HomepageLocation", "https://dummy.invalid/afs-test");
            }

            var s = real ? SecurityFlow.Read() : new SecurityState { BrowserPolicyFlags = BrowserPolicies.Scan(root, "HKCU") };
            Assert.Contains(s.BrowserPolicyFlags, x => x.Browser == "Chrome" && x.ValueName == "HomepageLocation");
            var f = SecurityTriage.Evaluate(Healthy(s.BrowserPolicyFlags));
            Note("found: " + BrowserPolicies.Summary(s.BrowserPolicyFlags));
            Assert.Contains(f, x => x.Code == FindingCode.SecurityBrowserPolicy);
            Assert.DoesNotContain("dummy.invalid", f.First(x => x.Code == FindingCode.SecurityBrowserPolicy).TechnicalDetail);
            Assert.Contains(SecurityTriage.Ladder(Healthy(s.BrowserPolicyFlags), f), r => r.ActionId == "security.remove_browser_policies_user");

            var (ok, detail) = real ? UserRepairs.RemoveBrowserPoliciesUser() : UserRepairs.RemoveBrowserPoliciesUser(root);
            Note($"user action ok={ok}: {detail}");
            Assert.True(ok);
            using (var k = root.OpenSubKey(chrome)) Assert.Null(k?.GetValue("HomepageLocation"));
            Assert.Empty(BrowserPolicies.Scan(root, "HKCU"));
        }
        finally
        {
            try
            {
                if (!scratchExisted) Registry.CurrentUser.DeleteSubKeyTree(scratch, false);
                using (var k = Registry.CurrentUser.OpenSubKey(chrome, writable: true)) k?.DeleteValue("HomepageLocation", false);
                for (var i = 2; i >= 0; i--)
                {
                    if (existed[i]) continue;
                    using var k = Registry.CurrentUser.OpenSubKey(paths[i]);
                    if (k is not null && k.ValueCount == 0 && k.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(paths[i], false);
                }
                if (Directory.Exists(UserRepairs.BackupDir))
                    foreach (var b in Directory.GetFiles(UserRepairs.BackupDir, "browser-policies-user-*.txt").Where(b => File.GetLastWriteTime(b) >= started))
                        File.Delete(b);
            }
            catch (Exception ex) { Note("cleanup problem: " + ex.Message); }
            finally { root?.Dispose(); File.AppendAllLines(output, lines.Append("browser policy live test: cleaned up")); }
        }
    }

    private static SecurityState Healthy(IReadOnlyList<BrowserPolicies.Flag> flags) => new()
    {
        Antivirus = new[] { new SecurityProduct("Windows Defender", 0x61100, "windowsdefender://") },
        DefenderRealTime = Tri.Yes, DefenderSignaturesUtc = DateTime.UtcNow,
        FirewallProfiles = new Dictionary<string, Tri> { ["Domain"] = Tri.Yes, ["Private"] = Tri.Yes, ["Public"] = Tri.Yes },
        FirewallService = new UpdateServiceInfo("mpssvc", "Running", "Automatic"),
        UacEnabled = Tri.Yes, UacAdminPrompt = 5, BrowserPolicyFlags = flags,
    };
}
