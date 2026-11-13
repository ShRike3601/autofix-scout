using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;
using ExecutorProgram = AutoFixScout.Executor.Program;

namespace AutoFixScout.Tests;

public class AppsTests
{
    private static AppsState Healthy(AppsFocus focus = AppsFocus.All) => new()
    {
        Focus = focus,
        StorePackagesRead = true,
        StorePackages = new[]
        {
            new ShellPackage("Microsoft.WindowsStore", "Microsoft.WindowsStore_22410.1401.5.0_x64__8wekyb3d8bbwe", "Ok"),
            new ShellPackage("Microsoft.StorePurchaseApp", "Microsoft.StorePurchaseApp_22408.1401.0.0_x64__8wekyb3d8bbwe", "Ok"),
            new ShellPackage("Microsoft.DesktopAppInstaller", "Microsoft.DesktopAppInstaller_1.24.25200.0_x64__8wekyb3d8bbwe", "Ok"),
        },
        Services = AppsTriage.ServiceNames.Select(n => n == AppsTriage.OfficeService
            ? new UpdateServiceInfo(n, "Running", "Automatic") : new UpdateServiceInfo(n, "Stopped", "Manual")).ToList(),
        ClockOffsetSeconds = 1,
        Office = new OfficeInstall("x64", "16.0.18129.20158", "O365HomePremRetail"),
        OneDrive = new OneDriveInstall(true, true),
    };

    private static List<string> Actions(AppsState s) => AppsTriage.Ladder(s, AppsTriage.Evaluate(s)).Select(r => r.ActionId).ToList();

    [Fact]
    public void Store_NoTrace_StillOffersStepsMildestFirst()
    {
        var s = Healthy(AppsFocus.Store);
        var f = AppsTriage.Evaluate(s);
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(f).Code);
        var l = Actions(s);
        Assert.Equal("apps.reset_store_cache", l[0]);
        Assert.Equal(3, l.Count(a => a == "apps.reregister"));
        Assert.Equal("ui.open_settings_page", l[^1]);
        Assert.DoesNotContain("office.quick_repair", l);
        Assert.DoesNotContain("apps.reset_onedrive", l);
        Assert.DoesNotContain("service.restore_default", l);
    }

    [Fact]
    public void DisabledStoreService_IsFoundAndRestoredBeforeTheTroubleshooter()
    {
        var s = Healthy(AppsFocus.Store);
        s.Services = s.Services.Select(x => x.Name == "ClipSVC" ? x with { StartType = "Disabled" } : x).ToList();
        var f = AppsTriage.Evaluate(s);
        Assert.Contains(f, x => x.Code == FindingCode.HealthServiceOff && x.TechnicalDetail.StartsWith("ClipSVC"));
        var l = AppsTriage.Ladder(s, f);
        var restore = l.Single(r => r.ActionId == "service.restore_default");
        Assert.Equal("ClipSVC", restore.Arg);
        Assert.True(l.ToList().IndexOf(restore) > l.ToList().FindIndex(r => r.ActionId == "apps.reregister"));
        Assert.Equal("troubleshoot", l[^1].Arg);
    }

    [Fact]
    public void StoppedManualServices_AreNotFaults()
    {
        var f = AppsTriage.Evaluate(Healthy(AppsFocus.Store));
        Assert.DoesNotContain(f, x => x.Code == FindingCode.HealthServiceOff);
    }

    [Fact]
    public void DamagedStorePackage_IsReregisteredFirst()
    {
        var s = Healthy(AppsFocus.Store);
        s.StorePackages = new[] { s.StorePackages[0], s.StorePackages[1], s.StorePackages[2] with { Status = "Modified" } };
        var f = AppsTriage.Evaluate(s);
        Assert.Equal(FindingCode.StorePackageDamaged, f[0].Code);
        Assert.Equal(s.StorePackages[2].FullName, AppsTriage.Ladder(s, f).First(r => r.ActionId == "apps.reregister").Arg);
    }

    [Fact]
    public void MissingStore_IsReportedButNoCacheReset()
    {
        var s = Healthy(AppsFocus.Store);
        s.StorePackages = s.StorePackages.Skip(1).ToList();
        Assert.Contains(AppsTriage.Evaluate(s), x => x.Code == FindingCode.StoreMissing);
        Assert.DoesNotContain("apps.reset_store_cache", Actions(s));
        // An unread package list never counts as "missing".
        s.StorePackages = Array.Empty<ShellPackage>(); s.StorePackagesRead = false; s.ProbeErrors = new[] { "store packages: x" };
        Assert.DoesNotContain(AppsTriage.Evaluate(s), x => x.Code == FindingCode.StoreMissing);
    }

    [Fact]
    public void WrongClock_IsSetFirst()
    {
        var s = Healthy(AppsFocus.Store); s.ClockOffsetSeconds = -3600;
        Assert.Contains(AppsTriage.Evaluate(s), x => x.Code == FindingCode.HealthClockWrong);
        Assert.Equal("time.resync", Actions(s)[0]);
    }

    [Fact]
    public void Office_QuickRepairOnlyForClickToRun()
    {
        var s = Healthy(AppsFocus.Office);
        var l = AppsTriage.Ladder(s, AppsTriage.Evaluate(s));
        Assert.Equal("office.quick_repair", l[0].ActionId);
        Assert.Equal("system", l[0].Arg);
        Assert.Equal("apps", l[1].Arg);
        Assert.DoesNotContain(l, r => r.ActionId.StartsWith("apps.") || r.Arg == "troubleshoot");

        s.Office = null;
        var f = AppsTriage.Evaluate(s);
        Assert.Equal(FindingCode.OfficeNotFound, f[0].Code);
        var only = Assert.Single(AppsTriage.Ladder(s, f));
        Assert.Equal("ui.open_settings_page", only.ActionId);
    }

    [Fact]
    public void Office_DisabledClickToRunService_IsRestored()
    {
        var s = Healthy(AppsFocus.Office);
        s.Services = s.Services.Select(x => x.Name == AppsTriage.OfficeService ? x with { Status = "Stopped" } : x).ToList();
        Assert.Contains(AppsTriage.Evaluate(s), x => x.Code == FindingCode.HealthServiceOff);
        Assert.Contains(AppsTriage.Ladder(s, AppsTriage.Evaluate(s)), r => r.ActionId == "service.restore_default" && r.Arg == "ClickToRunSvc");
    }

    [Fact]
    public void OneDrive_NotRunning_IsStartedThenReset()
    {
        var s = Healthy(AppsFocus.OneDrive); s.OneDrive = new OneDriveInstall(true, false);
        Assert.Equal(FindingCode.OneDriveNotRunning, AppsTriage.Evaluate(s)[0].Code);
        Assert.Equal(new[] { "apps.start_onedrive", "apps.reset_onedrive" }, Actions(s));
        var reset = AppsTriage.Ladder(s, AppsTriage.Evaluate(s)).Single(r => r.ActionId == "apps.reset_onedrive");
        Assert.Null(reset.Arg); // never a path from the app
        Assert.Contains("files are kept", reset.Say);
        Assert.Contains("cloud", reset.Say);

        s.OneDrive = null;
        Assert.Equal(FindingCode.OneDriveNotFound, AppsTriage.Evaluate(s)[0].Code);
        Assert.Empty(Actions(s));
    }

    [Fact]
    public void OneDriveRunning_LooksNormal_OffersReset()
    {
        var s = Healthy(AppsFocus.OneDrive);
        Assert.Equal(FindingCode.LooksNormal, AppsTriage.Evaluate(s)[0].Code);
        Assert.Equal(new[] { "apps.reset_onedrive" }, Actions(s));
    }

    [Fact]
    public void NothingRead_IsIncomplete()
    {
        var s = new AppsState { ProbeErrors = new[] { "store packages: x", "services: y" } };
        var f = AppsTriage.Evaluate(s);
        Assert.Equal(FindingCode.ProbeIncomplete, f[0].Code);
        Assert.Empty(AppsTriage.Ladder(s, f));
    }

    [Fact]
    public void NewActions_AreRoutedCorrectly()
    {
        Assert.Contains("office.quick_repair", FixActions.ElevatedActions);
        foreach (var a in new[] { "apps.reset_store_cache", "apps.start_onedrive", "apps.reset_onedrive" })
            Assert.DoesNotContain(a, FixActions.ElevatedActions);
        foreach (var r in AppsTriage.Ladder(Healthy(), AppsTriage.Evaluate(Healthy())).Where(r => r.ActionId == "service.restore_default"))
            Assert.NotNull(ServiceDefaults.Find(r.Arg!));
        Assert.All(AppsTriage.ServiceNames, n => Assert.NotNull(ServiceDefaults.Find(n)));
    }

    [Fact]
    public void Executor_OfficeRepair_AcceptsOnlySystem()
    {
        Assert.Equal(2, ExecutorProgram.Main(new[] { "office.quick_repair" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "office.quick_repair", "System" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "office.quick_repair", "system ", }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "office.quick_repair", "platform=x64" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "office.quick_repair", @"C:\evil.exe" }));
        Assert.Equal(2, ExecutorProgram.Main(new[] { "office.quick_repair", "system", "x" }));
    }

    [Theory]
    [InlineData("x64", "en-us", true)]
    [InlineData("x86", "de-DE", true)]
    [InlineData("x64", "sr-latn-rs", true)]
    [InlineData("arm64", "en-us", false)]
    [InlineData("x64 ", "en-us", false)]
    [InlineData("x64", "en-us DisplayLevel=True", false)]
    [InlineData("x64", "en-us\n", false)]
    [InlineData("x64", "", false)]
    [InlineData(null, "en-us", false)]
    [InlineData("x64", null, false)]
    public void OfficeRepairArgs_AreValidated(string? platform, string? culture, bool ok)
    {
        var args = OfficeClickToRun.QuickRepairArgs(platform, culture);
        Assert.Equal(ok, args is not null);
        if (args is null) return;
        Assert.Equal(new[] { "scenario=Repair", $"platform={platform}", $"culture={culture!.ToLowerInvariant()}", "forceappshutdown=True", "RepairType=QuickRepair", "DisplayLevel=False" }, args);
    }

    [Fact]
    public void TestQuestions_AreSet()
    {
        Assert.Equal("apps", AppsFlow.Topic);
        foreach (var focus in Enum.GetValues<AppsFocus>())
            Assert.EndsWith("?", AppsFlow.TestQuestionFor(focus));
    }

    public static IEnumerable<object[]> States()
    {
        foreach (var focus in Enum.GetValues<AppsFocus>())
        {
            yield return new object[] { Healthy(focus) };
            var bad = Healthy(focus);
            bad.ClockOffsetSeconds = 900;
            bad.StorePackages = new[] { bad.StorePackages[1] with { Status = "NeedsRemediation" } };
            bad.Services = bad.Services.Select(x => x with { StartType = "Disabled" }).ToList();
            bad.OneDrive = new OneDriveInstall(false, false);
            yield return new object[] { bad };
            var none = Healthy(focus); none.Office = null; none.OneDrive = null;
            yield return new object[] { none };
        }
        yield return new object[] { new AppsState { ProbeErrors = new[] { "x" } } };
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Sentences_FollowTheWritingGuide(AppsState s)
    {
        var f = AppsTriage.Evaluate(s);
        var l = AppsTriage.Ladder(s, f);
        var texts = f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).Append(AppsTriage.NotFixedHint(f, s.Focus) ?? "")
            .Append(AppsFlow.TestQuestionFor(s.Focus)).Append(AppsFlow.What);
        foreach (var t in texts)
            Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |\bI\b|\bI'm\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
            Assert.EndsWith("Windows will ask for your permission.", r.Say);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
    }
}

/// <summary>Reads the real machine only (AFS_LIVE set). Never runs WSReset, the OneDrive reset or an Office repair.</summary>
[Collection("Live machine")]
public class LiveAppsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void AppsFlow_ReadsThisMachine()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        foreach (var focus in Enum.GetValues<AppsFocus>())
        {
            var s = AppsFlow.Read(focus);
            Assert.Empty(s.ProbeErrors);
            Assert.True(s.StorePackagesRead);
            Assert.Equal(AppsTriage.ServiceNames.Count(), s.Services.Count);
            var f = AppsTriage.Evaluate(s);
            Assert.NotEmpty(f);
            Assert.DoesNotContain(f, x => x.Code == FindingCode.ProbeIncomplete);
            output.WriteLine($"{focus}: " + string.Join(" | ", f.Select(x => $"{x.Code} [{x.TechnicalDetail}]")) + " => " + string.Join(", ", AppsTriage.Ladder(s, f).Select(r => r.ActionId)));
        }
    }
}
