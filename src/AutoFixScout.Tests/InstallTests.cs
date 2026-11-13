using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class InstallTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0);

    private static AppsState State(Action<InstallState>? tweak = null)
    {
        var i = new InstallState
        {
            Service = new UpdateServiceInfo("msiserver", "Stopped", "Manual"),
            SystemDriveFreeBytes = 50L * 1024 * 1024 * 1024,
        };
        tweak?.Invoke(i);
        return new AppsState { Focus = AppsFocus.Install, StorePackagesRead = true, Install = i, ClockOffsetSeconds = 900 };
    }

    private static List<string> Actions(AppsState s) => AppsTriage.Ladder(s, AppsTriage.Evaluate(s)).Select(r => r.ActionId).ToList();

    [Theory]
    [InlineData(1603, "fatal")]
    [InlineData(1618, "another installation")]
    [InlineData(1619, "could not be opened")]
    [InlineData(1625, "policy")]
    [InlineData(1638, "another version")]
    [InlineData(3010, "restart")]
    [InlineData(2503, "temporary folder")]
    [InlineData(9999, "9999")]
    public void Codes_HaveMeanings(int code, string part) => Assert.Contains(part, InstallerCodes.Meaning(code));

    [Fact]
    public void ServiceDefault_IsManual()
    {
        var d = ServiceDefaults.Find("msiserver")!;
        Assert.Equal(ServiceDefaults.Start.Manual, d.Start);
        Assert.False(d.ShouldRun);
        Assert.Equal("apps", d.Area);
    }

    [Fact]
    public void NoTrace_LooksNormal_AndIgnoresTheClock()
    {
        var f = AppsTriage.Evaluate(State());
        Assert.Equal(FindingCode.LooksNormal, Assert.Single(f).Code);
    }

    [Fact]
    public void DisabledService_IsRestoredWithAdmin()
    {
        var s = State(i => i.Service = new UpdateServiceInfo("msiserver", "Stopped", "Disabled"));
        Assert.Contains(AppsTriage.Evaluate(s), x => x.Code == FindingCode.InstallerServiceOff);
        var r = AppsTriage.Ladder(s, AppsTriage.Evaluate(s)).Single(x => x.ActionId == "service.restore_default");
        Assert.Equal("msiserver", r.Arg);
    }

    [Fact]
    public void Failure_NamesTheProductAndMeaning_UsingCodeOfMatchingEvent()
    {
        var s = State(i => i.Events = new[]
        {
            new InstallEvent(Now, 11708, "Contoso Editor", 0),
            new InstallEvent(Now.AddSeconds(-1), 1033, "Contoso Editor", 1603),
            new InstallEvent(Now.AddDays(-2), 1033, "Other", 0),
        });
        var f = AppsTriage.Evaluate(s).Single(x => x.Code == FindingCode.InstallFailures);
        Assert.Contains("Contoso Editor", f.PlainMessage);
        Assert.Contains("fatal error", f.PlainMessage);
    }

    [Fact]
    public void RestartCodes_AreNotFailures()
    {
        var s = State(i => i.Events = new[] { new InstallEvent(Now, 1033, "X", 3010) });
        Assert.DoesNotContain(AppsTriage.Evaluate(s), x => x.Code == FindingCode.InstallFailures);
    }

    [Fact]
    public void PendingRestart_OffersRestartGuide()
    {
        var s = State(i => i.RestartPending = true);
        Assert.Contains(AppsTriage.Evaluate(s), x => x.Code == FindingCode.InstallBlockedByRestart);
        Assert.Contains(AppsTriage.Ladder(s, AppsTriage.Evaluate(s)), r => r.Id == "restart_pc_installs" && r.ActionId == "ui.human_step");
    }

    [Theory]
    [InlineData(2503, "storage.clear_user_temp")]
    [InlineData(2502, "storage.clear_user_temp")]
    [InlineData(1638, "ui.open_settings_page")]
    [InlineData(1619, "ui.human_step")]
    [InlineData(1620, "ui.human_step")]
    public void FailureCode_SelectsRung(int code, string action)
    {
        var s = State(i => i.Events = new[] { new InstallEvent(Now, 1033, "Contoso Editor", code) });
        Assert.Contains(action, Actions(s));
    }

    [Fact]
    public void TestQuestion_IsSet() => Assert.Equal("Does the program install now?", AppsFlow.TestQuestionFor(AppsFocus.Install));

    public static IEnumerable<object[]> Styled()
    {
        yield return new object[] { State() };
        foreach (var c in new[] { 1603, 1618, 1619, 1620, 1625, 1638, 2502, 2503, 112 })
            yield return new object[] { State(i =>
            {
                i.Service = new UpdateServiceInfo("msiserver", "Stopped", "Disabled");
                i.RestartPending = true;
                i.SystemDriveFreeBytes = 1024;
                i.Events = new[] { new InstallEvent(Now, 1033, "Contoso Editor", c) };
            }) };
    }

    [Theory]
    [MemberData(nameof(Styled))]
    public void Sentences_FollowTheWritingGuide(AppsState s)
    {
        var f = AppsTriage.Evaluate(s);
        var l = AppsTriage.Ladder(s, f);
        var texts = f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say))
            .Append(AppsTriage.NotFixedHint(f, s.Focus) ?? "").Append(AppsFlow.TestQuestionFor(s.Focus));
        foreach (var t in texts)
            Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |\bI\b|\bI'm\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        foreach (var r in l.Where(r => FixActions.ElevatedActions.Contains(r.ActionId)))
            Assert.EndsWith("Windows will ask for your permission.", r.Say);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
    }
}

/// <summary>Reads the real machine only (AFS_LIVE set). Read-only.</summary>
[Collection("Live machine")]
public class LiveInstallTests
{
    [Fact]
    public void InstallFocus_ReadsThisMachine()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = AppsFlow.Read(AppsFocus.Install);
        Assert.Empty(s.ProbeErrors);
        Assert.NotNull(s.Install);
        var i = s.Install!;
        var f = AppsTriage.Evaluate(s);
        var lines = new List<string>
        {
            $"msiserver: {i.Service?.Status}/{i.Service?.StartType}; events: {i.Events.Count}; restart pending: {i.RestartPending}; free: {i.SystemDriveFreeBytes}",
        };
        lines.AddRange(i.Events.Take(5).Select(e => $"EVENT {e.EventId} {e.Product} {e.Code}"));
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage}"));
        File.AppendAllLines(output, lines);
        Assert.Equal("Manual", i.Service!.StartType);
        Assert.NotNull(i.SystemDriveFreeBytes);
        Assert.NotEmpty(f);
    }
}
