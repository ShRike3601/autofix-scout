using AutoFixScout.Areas;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class PowerTests
{
    private static PowerState Healthy() => new()
    {
        HasBattery = true, DesignCapacity = 50000, FullChargeCapacity = 48000, ActiveSchemeGuid = PowerTriage.BalancedGuid,
        SleepAcSeconds = 0, SleepDcSeconds = 600, FastStartup = false,
    };

    [Fact]
    public void Parsers()
    {
        Assert.Equal(PowerTriage.BalancedGuid, PowerTriage.ParseActiveScheme("Power Scheme GUID: 381B4222-F694-41F0-9685-FF5BB260DF2E  (Balanced)"));
        Assert.Null(PowerTriage.ParseActiveScheme("nothing"));
        var q = "Minimum: 0x00000000\nMaximum: 0xffffffff\n Current AC Power Setting Index: 0x00000000\n Current DC Power Setting Index: 0x00000258\n";
        Assert.Equal((0L, 600L), PowerTriage.ParseAcDc(q));
        Assert.Null(PowerTriage.ParseAcDc("none"));
        Assert.Equal("Power Button", PowerTriage.ParseWakeSource("The system has resumed.\r\nWake Source: Power Button\r\n"));
        Assert.Null(PowerTriage.ParseWakeSource(""));
    }

    [Fact]
    public void NoBatteryDesktop_IsNotAFault()
    {
        var s = new PowerState { ActiveSchemeGuid = PowerTriage.HighPerformanceGuid, SleepDcSeconds = 0 };
        Assert.Equal(FindingCode.LooksNormal, PowerTriage.Evaluate(s)[0].Code);
    }

    [Fact]
    public void Wear_ThresholdsAndFindings()
    {
        var s = Healthy(); Assert.Equal(FindingCode.LooksNormal, PowerTriage.Evaluate(s)[0].Code);
        s.FullChargeCapacity = 35000; Assert.Equal(FindingCode.PowerBatteryWorn, PowerTriage.Evaluate(s)[0].Code);
        s.FullChargeCapacity = 20000; Assert.Equal(FindingCode.PowerBatteryReplace, PowerTriage.Evaluate(s)[0].Code);
    }

    [Fact]
    public void SleepNever_HighPlan_Wakes_FastStartup()
    {
        var s = Healthy(); s.SleepDcSeconds = 0; s.ActiveSchemeGuid = PowerTriage.UltimateGuid;
        s.WakeSources = Enumerable.Repeat("Power Button", 8).ToList(); s.FastStartup = true; s.ReportsShutdownProblems = true;
        var f = PowerTriage.Evaluate(s);
        foreach (var c in new[] { FindingCode.PowerSleepNeverOnBattery, FindingCode.PowerPlanHighOnBattery, FindingCode.PowerWakesOften, FindingCode.PowerFastStartupOn })
            Assert.Contains(f, x => x.Code == c);
        var l = PowerTriage.Ladder(s, f);
        Assert.Contains(l, r => r.ActionId == "power.balanced_plan");
        Assert.Contains(l, r => r.Id == "fast_startup_off");
        Assert.DoesNotContain("power.balanced_plan", FixActions.ElevatedActions);
    }

    [Fact]
    public void ProbeFailure_GivesIncomplete()
    {
        var s = new PowerState { ProbeErrors = new[] { "x" } };
        var f = PowerTriage.Evaluate(s);
        Assert.Equal(FindingCode.ProbeIncomplete, f[0].Code);
        Assert.Empty(PowerTriage.Ladder(s, f));
    }

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var s = Healthy(); s.FullChargeCapacity = 20000; s.SleepDcSeconds = 0; s.ActiveSchemeGuid = PowerTriage.UltimateGuid;
        s.WakeSources = Enumerable.Repeat("Power Button", 8).ToList(); s.FastStartup = true; s.ReportsShutdownProblems = true;
        var f = PowerTriage.Evaluate(s);
        var l = PowerTriage.Ladder(s, f);
        foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)).Append(PowerTriage.NotFixedHint(f) ?? "").Append(PowerTriage.NotFixedHint(Array.Empty<Finding>()) ?? ""))
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
        Assert.Contains("Windows will ask for your permission", l.First(r => r.Id == "fast_startup_off").Say);
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
    }
}

[Collection("Live machine")]
public class LivePowerTests
{
    [Fact]
    public async Task PowerProbe_ReadsWithoutErrors_AndBalancedPlanSwitchesBack()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        var s = PowerFlow.Read();
        Assert.Empty(s.ProbeErrors);
        Assert.NotNull(s.ActiveSchemeGuid);
        var before = s.ActiveSchemeGuid!;
        try
        {
            var (ok, detail) = await FixActions.RunAsync(new Rung("t", "power.balanced_plan", 0, ""));
            Assert.True(ok, detail);
            Assert.Equal(PowerTriage.BalancedGuid, PowerFlow.Read().ActiveSchemeGuid);
        }
        finally
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg.exe", "/setactive " + before) { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
        }
    }
}
