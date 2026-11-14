using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class BatteryHealthTests
{
    private const string Ns = "http://schemas.microsoft.com/battery/2012";

    private static string Report(string batteries, string history = "") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        $"<BatteryReport xmlns=\"{Ns}\">\n" +
        "  <ReportInformation><ReportVersion>1.1</ReportVersion><ReportDuration>7</ReportDuration></ReportInformation>\n" +
        "  <SystemInformation><ComputerName>EXAMPLE-PC</ComputerName><PlatformRole>2</PlatformRole></SystemInformation>\n" +
        $"  <Batteries>{batteries}</Batteries>\n  <History>{history}</History>\n</BatteryReport>";

    private static string Battery(int? design, int? full, int? cycles, string chem = "LION") =>
        "<Battery><Id>EXAMPLE-ID</Id><SerialNumber>0000</SerialNumber>" + $"<Chemistry>{chem}</Chemistry>" +
        (design is null ? "" : $"<DesignCapacity>{design}</DesignCapacity>") +
        (full is null ? "" : $"<FullChargeCapacity>{full}</FullChargeCapacity>") +
        (cycles is null ? "" : $"<CycleCount>{cycles}</CycleCount>") + "</Battery>";

    private static PowerState State(int design, int full, int? cycles = null) => new()
    {
        HasBattery = true, ActiveSchemeGuid = PowerTriage.BalancedGuid, SleepDcSeconds = 600,
        BatteryReport = BatteryReportParser.Parse(Report(Battery(design, full, cycles))),
    };

    [Fact]
    public void Parser_NormalReport()
    {
        var hist = "<HistoryEntry StartDate=\"2025-01-06\" LocalStartDate=\"2025-01-06\" DesignCapacity=\"50000\" FullChargeCapacity=\"48000\" CycleCount=\"100\" />" +
                   "<HistoryEntry StartDate=\"2025-06-02\" LocalStartDate=\"2025-06-02\" DesignCapacity=\"50000\" FullChargeCapacity=\"40000\" CycleCount=\"310\" />";
        var r = BatteryReportParser.Parse(Report(Battery(50000, 40000, 310), hist))!;
        var b = Assert.Single(r.Batteries);
        Assert.Equal(50000, b.DesignCapacity);
        Assert.Equal(40000, b.FullChargeCapacity);
        Assert.Equal(310, b.CycleCount);
        Assert.Equal("LION", b.Chemistry);
        Assert.Equal(20.0, r.WearPercent!.Value, 3);
        Assert.Equal(310, r.MaxCycles);
        Assert.Equal(2, r.History.Count);
        Assert.Equal(48000, r.History[0].FullChargeCapacity);
    }

    [Fact]
    public void Parser_MissingFields()
    {
        var r = BatteryReportParser.Parse(Report(Battery(50000, null, null)))!;
        Assert.Null(r.Batteries[0].FullChargeCapacity);
        Assert.Null(r.WearPercent);
        Assert.Null(r.MaxCycles);
        var r2 = BatteryReportParser.Parse(Report(Battery(null, null, null)))!;
        Assert.Null(r2.WearPercent);
        var r3 = BatteryReportParser.Parse(Report(""));
        Assert.Empty(r3!.Batteries);
    }

    [Fact]
    public void Parser_CycleCountZeroMeansUnknown() =>
        Assert.Null(BatteryReportParser.Parse(Report(Battery(50000, 40000, 0)))!.MaxCycles);

    [Fact]
    public void Parser_TwoBatteries_AreSummed()
    {
        var r = BatteryReportParser.Parse(Report(Battery(30000, 24000, 200) + Battery(20000, 16000, 450)))!;
        Assert.Equal(2, r.Batteries.Count);
        Assert.Equal(50000, r.DesignTotal);
        Assert.Equal(40000, r.FullTotal);
        Assert.Equal(20.0, r.WearPercent!.Value, 3);
        Assert.Equal(450, r.MaxCycles);
    }

    [Fact]
    public void Parser_ZeroDesignCapacity_GivesNoWear()
    {
        var r = BatteryReportParser.Parse(Report(Battery(0, 40000, 10)))!;
        Assert.Null(r.Batteries[0].WearPercent);
        Assert.Null(r.WearPercent);
        Assert.Empty(r.Usable);
        var r2 = BatteryReportParser.Parse(Report(Battery(0, 0, null) + Battery(50000, 45000, null)))!;
        Assert.Equal(10.0, r2.WearPercent!.Value, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Other><Batteries/></Other>")]
    public void Parser_RejectsNonReports(string? text) => Assert.Null(BatteryReportParser.Parse(text));

    [Fact]
    public void Parser_DoesNotKeepNameOrSerial()
    {
        var r = BatteryReportParser.Parse(Report(Battery(50000, 40000, 1)))!;
        Assert.DoesNotContain("EXAMPLE", System.Text.Json.JsonSerializer.Serialize(r.Batteries));
    }

    [Theory]
    [InlineData(50000, 50000, FindingCode.LooksNormal)]
    [InlineData(50000, 41000, FindingCode.LooksNormal)]
    [InlineData(50000, 40001, FindingCode.LooksNormal)]
    [InlineData(50000, 40000, FindingCode.PowerBatteryAgeing)]
    [InlineData(50000, 30001, FindingCode.PowerBatteryAgeing)]
    [InlineData(50000, 30000, FindingCode.PowerBatteryWornOut)]
    [InlineData(51368, 21000, FindingCode.PowerBatteryWornOut)]
    public void Thresholds(int design, int full, FindingCode expected) =>
        Assert.Equal(expected, PowerTriage.Evaluate(State(design, full))[0].Code);

    [Fact]
    public void Cycles_Above800()
    {
        Assert.DoesNotContain(PowerTriage.Evaluate(State(50000, 50000, 800)), x => x.Code == FindingCode.PowerBatteryManyCycles);
        Assert.Contains(PowerTriage.Evaluate(State(50000, 50000, 801)), x => x.Code == FindingCode.PowerBatteryManyCycles);
    }

    [Fact]
    public void ReportReplacesTheWmiWearFindings()
    {
        var s = State(50000, 45000);
        s.DesignCapacity = 50000; s.FullChargeCapacity = 10000;
        Assert.Equal(FindingCode.LooksNormal, PowerTriage.Evaluate(s)[0].Code);
    }

    [Fact]
    public void Desktop_SaysNothing()
    {
        var s = new PowerState { ActiveSchemeGuid = PowerTriage.BalancedGuid, BatteryReportFailed = true };
        var f = PowerTriage.Evaluate(s);
        Assert.Single(f);
        Assert.Equal(FindingCode.LooksNormal, f[0].Code);
        var ran = false;
        PowerFlow.ReadBatteryReport(s, _ => { ran = true; return ""; });
        Assert.False(ran);
    }

    [Fact]
    public void UnreadableReport_OnALaptop()
    {
        var s = new PowerState { HasBattery = true, ActiveSchemeGuid = PowerTriage.BalancedGuid, BatteryReportFailed = true };
        var f = PowerTriage.Evaluate(s);
        Assert.Contains(f, x => x.Code == FindingCode.PowerBatteryReportUnreadable);
        Assert.DoesNotContain(f, x => x.Code == FindingCode.ProbeIncomplete);
    }

    [Fact]
    public void ReadBatteryReport_ParsesAndDeletesTheTemporaryFile()
    {
        var s = new PowerState { HasBattery = true };
        string? used = null;
        PowerFlow.ReadBatteryReport(s, p => { used = p; File.WriteAllText(p, Report(Battery(50000, 40000, 5))); return ""; });
        Assert.False(s.BatteryReportFailed);
        Assert.Equal(20.0, s.BatteryReport!.WearPercent!.Value, 3);
        Assert.False(File.Exists(used));
    }

    [Fact]
    public void ReadBatteryReport_FailureIsNotAnException_AndFileIsGone()
    {
        var s = new PowerState { HasBattery = true };
        string? used = null;
        PowerFlow.ReadBatteryReport(s, p => { used = p; File.WriteAllText(p, "garbage"); throw new InvalidOperationException("exit 1"); });
        Assert.True(s.BatteryReportFailed);
        Assert.Null(s.BatteryReport);
        Assert.False(File.Exists(used));
    }

    [Fact]
    public void ReadBatteryReport_ReportsActivityLines()
    {
        var area = "battery-test-" + Guid.NewGuid().ToString("N");
        var seen = new System.Collections.Concurrent.ConcurrentBag<ActivityItem>();
        void Handler(ActivityItem i) { if (i.Area == area) seen.Add(i); }
        Activity.Reported += Handler;
        try
        {
            using (Activity.Scope(area, announce: false))
                PowerFlow.ReadBatteryReport(new PowerState { HasBattery = true }); // real powercfg; failing on a desktop is fine here
        }
        finally { Activity.Reported -= Handler; }
        var mine = seen.ToList();
        var cmd = Assert.Single(mine, i => i.Kind == ActivityKind.Command);
        Assert.Contains("powercfg", cmd.Text);
        Assert.Contains("batteryreport", cmd.Text);
        Assert.DoesNotContain("afs-battery", cmd.Text);
        Assert.DoesNotContain("\\", cmd.Text);
    }

    private static PowerState Worn() => State(50000, 20000, 900);

    [Fact]
    public void Sentences_FollowTheWritingGuide()
    {
        var all = new List<string>();
        foreach (var s in new[] { Worn(), State(50000, 35000, 100), State(50000, 50000, 1000), new PowerState { HasBattery = true, BatteryReportFailed = true } })
        {
            var f = PowerTriage.Evaluate(s);
            all.AddRange(f.Select(x => x.PlainMessage));
            all.AddRange(PowerTriage.Ladder(s, f).Select(r => r.Say));
            all.Add(PowerTriage.NotFixedHint(f) ?? "");
        }
        Assert.Contains(all, t => t.Contains("The battery can now hold 40 percent of the charge it held when it was new."));
        foreach (var t in all)
        {
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!|\bit's\b|\bthat's\b", t);
            Assert.DoesNotMatch(@"online|buy any", t);
        }
    }

    [Fact]
    public void Ladder_Steps_AreUniqueAndSensible()
    {
        var s = Worn();
        var l = PowerTriage.Ladder(s, PowerTriage.Evaluate(s));
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        Assert.Contains(l, r => r.Id == "reduce_battery_drain" && r.Say.Contains("brightness") && r.Say.Contains("Battery Saver"));
        Assert.Contains(l, r => r.Id == "replace_battery" && r.Say.Contains("computer maker or a repair shop"));
        Assert.Contains(l, r => r.ActionId == "ui.open_settings_page" && r.Arg == "batterysaver");
        Assert.Contains(l, r => r.ActionId == "ui.open_settings_page" && r.Arg == "powersleep");
        var ageing = State(50000, 35000);
        var la = PowerTriage.Ladder(ageing, PowerTriage.Evaluate(ageing));
        Assert.DoesNotContain(la, r => r.Id == "replace_battery");
        Assert.Equal(la.Count, la.Select(r => r.Id).Distinct().Count());
        Assert.Contains("replacement", PowerTriage.NotFixedHint(PowerTriage.Evaluate(s)));
    }

    [Theory]
    [InlineData("my battery health is bad")]
    [InlineData("battery worn out")]
    [InlineData("battery drains fast")]
    public void Router_FindsThePowerArea(string text) => Assert.Equal("power", SymptomRouter.Best(text).Area);
}

[Collection("Live machine")]
public class LiveBatteryTests
{
    [Fact]
    public void BatteryReport_ReadsThisLaptop_NumbersOnly()
    {
        var output = Environment.GetEnvironmentVariable("AFS_LIVE");
        if (string.IsNullOrEmpty(output)) return;
        var s = new PowerState { HasBattery = true };
        PowerFlow.ReadBatteryReport(s);
        var f = PowerTriage.Evaluate(s);
        var r = s.BatteryReport;
        var lines = new List<string>
        {
            $"design {r?.DesignTotal} mWh; full {r?.FullTotal} mWh; wear {r?.WearPercent?.ToString("0.0")} %; cycles {(r?.MaxCycles?.ToString() ?? "not reported")}; batteries {r?.Batteries.Count}",
            $"history entries {r?.History.Count}; report failed {s.BatteryReportFailed}",
        };
        lines.AddRange(f.Select(x => $"FINDING {x.Code}: {x.PlainMessage} [{x.TechnicalDetail}]"));
        lines.AddRange(PowerTriage.Ladder(s, f).Select(x => $"RUNG {x.Id} {x.ActionId}({x.Arg})"));
        File.WriteAllLines(output, lines);
        Assert.NotNull(r);
        Assert.False(s.BatteryReportFailed);
        Assert.True(r!.DesignTotal > 0);
    }
}
