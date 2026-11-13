using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Triage;
using AutoFixScout.Watching;
using Microsoft.Win32;
using Xunit;

namespace AutoFixScout.Tests;

public class WifiWatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double seconds) => T0.AddSeconds(seconds);
    private static bool Auto(string _) => true;
    private static bool Manual(string _) => false;

    private static WifiWatchLogic Connected(out List<WifiWatchRecord> done, string profile = "SecretNet")
    {
        var logic = new WifiWatchLogic();
        var list = new List<WifiWatchRecord>();
        logic.Completed += list.Add;
        done = list;
        logic.OnConnected(At(-100), profile);
        return logic;
    }

    // ---- decision logic ----

    [Fact]
    public void DriverDrop_NotReconnectedByWindows_ReconnectsAfterGrace()
    {
        var logic = Connected(out _);
        logic.OnDisconnected(At(0), 0, "The network is disconnected by the driver.", "SecretNet");
        Assert.IsType<WatchDecision.Nothing>(logic.Tick(At(5), false, Auto));
        Assert.IsType<WatchDecision.Nothing>(logic.Tick(At(19), false, Auto));
        Assert.Equal(new WatchDecision.Reconnect("SecretNet"), logic.Tick(At(21), false, Auto));
    }

    [Fact]
    public void DropByPerson_IsIgnored()
    {
        var logic = Connected(out var done);
        logic.OnDisconnected(At(0), 2, "The network is disconnected by the user.", "SecretNet");
        Assert.IsType<WatchDecision.Nothing>(logic.Tick(At(60), false, Auto));
        Assert.Empty(done);
    }

    [Fact]
    public void WindowsReconnectsInTime_NoAttempt_RecordedAsNotByWatcher()
    {
        var logic = Connected(out var done);
        logic.OnDisconnected(At(0), 0, null, "SecretNet");
        logic.OnConnected(At(8), "SecretNet");
        Assert.IsType<WatchDecision.Nothing>(logic.Tick(At(30), true, Auto));
        var r = Assert.Single(done);
        Assert.False(r.ReconnectedByWatcher);
        Assert.Equal(8, r.SecondsOffline);
        Assert.Equal("Driver", r.Reason);
    }

    [Fact]
    public void ProfileNotSetToConnectAutomatically_IsNeverReconnected()
    {
        var logic = Connected(out _);
        logic.OnDisconnected(At(0), 0, null, "SecretNet");
        Assert.IsType<WatchDecision.Nothing>(logic.Tick(At(25), false, Manual));
        Assert.Equal(0, logic.Attempts);
    }

    [Fact]
    public void UnknownProfile_IsNeverReconnected()
    {
        var logic = new WifiWatchLogic();
        logic.OnDisconnected(At(0), 0, null, null);
        Assert.IsType<WatchDecision.Nothing>(logic.Tick(At(25), false, Auto));
    }

    [Fact]
    public void ProfileFromEarlierConnect_IsUsedWhenTheDropEventHasNone()
    {
        var logic = Connected(out _, "Campus");
        logic.OnDisconnected(At(0), 99, "The signal was lost.", null);
        Assert.Equal(new WatchDecision.Reconnect("Campus"), logic.Tick(At(25), false, Auto));
    }

    [Fact]
    public void Backoff_OneAttemptPer30Seconds_FiveAttemptsThenStops()
    {
        var logic = Connected(out var done);
        logic.OnDisconnected(At(0), 0, null, "SecretNet");
        var attempts = new List<double>();
        for (double s = 21; s < 600; s += 1)
            if (logic.Tick(At(s), false, Auto) is WatchDecision.Reconnect) attempts.Add(s);
        Assert.Equal(WifiWatchLogic.MaxAttempts, attempts.Count);
        for (var i = 1; i < attempts.Count; i++) Assert.True(attempts[i] - attempts[i - 1] >= 30);
        var gaveUp = Assert.Single(done);
        Assert.False(gaveUp.ReconnectedByWatcher);
    }

    [Fact]
    public void SuccessfulConnection_ResetsTheAttemptCount_AndCreditsTheWatcher()
    {
        var logic = Connected(out var done);
        logic.OnDisconnected(At(0), 0, null, "SecretNet");
        Assert.IsType<WatchDecision.Reconnect>(logic.Tick(At(21), false, Auto));
        logic.OnConnected(At(27), "SecretNet");
        Assert.Equal(0, logic.Attempts);
        var r = Assert.Single(done);
        Assert.True(r.ReconnectedByWatcher);
        Assert.Equal(27, r.SecondsOffline);
        // A later drop starts fresh.
        logic.Tick(At(990), true, Auto);
        logic.OnDisconnected(At(1000), 0, null, "SecretNet");
        Assert.IsType<WatchDecision.Reconnect>(logic.Tick(At(1021), false, Auto));
    }

    [Fact]
    public void ConnectedAccordingToWindows_WithoutAnEvent_EndsTheOutage()
    {
        var logic = Connected(out var done);
        logic.OnDisconnected(At(0), 0, null, "SecretNet");
        Assert.IsType<WatchDecision.Nothing>(logic.Tick(At(25), true, Auto));
        Assert.Single(done);
        Assert.False(logic.Pending);
    }

    [Fact]
    public void Sleep_BetweenTicks_IsNotCountedAsADrop()
    {
        var logic = Connected(out var done);
        logic.Tick(At(0), true, Auto);
        logic.OnDisconnected(At(1), 0, null, "SecretNet");
        Assert.IsType<WatchDecision.Nothing>(logic.Tick(At(8 * 3600), false, Auto));
        Assert.Empty(done);
        Assert.False(logic.Pending);
    }

    [Fact]
    public void VeryLongOutage_IsNotRecorded()
    {
        var logic = Connected(out var done);
        logic.OnDisconnected(At(0), 0, null, "SecretNet");
        logic.OnConnected(At(3 * 3600), "SecretNet");
        Assert.Empty(done);
    }

    // ---- netsh helpers ----

    [Fact]
    public void ConnectArguments_QuotesTheProfile_AndRefusesUnsafeNames()
    {
        Assert.Equal("wlan connect name=\"Campus WiFi\"", WifiNetsh.ConnectArguments("Campus WiFi"));
        Assert.Null(WifiNetsh.ConnectArguments("a\" & calc"));
        Assert.Null(WifiNetsh.ConnectArguments(""));
        Assert.Null(WifiNetsh.ConnectArguments("x\ny"));
        Assert.Null(WifiNetsh.ShowProfileArguments("a\"b"));
    }

    [Theory]
    [InlineData("    Connection mode        : Connect automatically", true)]
    [InlineData("    Connection mode        : Connect manually", false)]
    [InlineData("    Autoswitch             : Do not switch to other networks", false)]
    public void IsAutoConnect_ReadsConnectionMode(string line, bool expected) =>
        Assert.Equal(expected, WifiNetsh.IsAutoConnect("Profile information\n-------\n    Version : 1\n" + line + "\n"));

    [Fact]
    public void ParseInterfaces_ReadsStateAndProfile()
    {
        var up = "There is 1 interface on the system:\n    Name                   : Wi-Fi\n    State                  : connected\n    SSID                   : X\n    Profile                : Campus\n";
        Assert.Equal((true, "Campus"), WifiNetsh.ParseInterfaces(up));
        var down = "    Name                   : Wi-Fi\n    State                  : disconnected\n";
        Assert.Equal((false, (string?)null), WifiNetsh.ParseInterfaces(down));
    }

    [Fact]
    public void ParseProfileName_ReadsTheEventData()
    {
        var xml = "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>8003</EventID></System><EventData><Data Name=\"ProfileName\">Campus</Data></EventData></Event>";
        Assert.Equal("Campus", WifiNetsh.ParseProfileName(xml));
        Assert.Null(WifiNetsh.ParseProfileName(xml.Replace("Campus", "")));
    }

    // ---- the file ----

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "afs-watch-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Store_NeverWritesAProfileName()
    {
        var path = TempFile();
        try
        {
            var logic = Connected(out _, "SecretNet-Profile");
            var store = new WifiWatchStore(path);
            logic.Completed += r => store.Add(r, T0.AddMinutes(5));
            logic.OnDisconnected(At(0), 0, "The network is disconnected by the driver.", "SecretNet-Profile");
            Assert.IsType<WatchDecision.Reconnect>(logic.Tick(At(21), false, Auto));
            logic.OnConnected(At(25), "SecretNet-Profile");
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("SecretNet", text);
            Assert.DoesNotContain("Profile", text);
            Assert.Contains("\"ReconnectedByWatcher\": true", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_KeepsThirtyDays_AndSummarizes()
    {
        var path = TempFile();
        try
        {
            var store = new WifiWatchStore(path);
            var now = T0.AddDays(40);
            store.Add(new WifiWatchRecord(T0, "Driver", true, 20), T0);
            store.Add(new WifiWatchRecord(now.AddDays(-2), "Driver", true, 22), now);
            store.Add(new WifiWatchRecord(now.AddDays(-1), "SignalLost", false, 70), now);
            var loaded = store.Load(now);
            Assert.Equal(2, loaded.Count); // the 40-day-old one was dropped when pruning
            Assert.Equal(new WifiWatchSummary(2, 1, 30), store.Summarize(now));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_MissingOrCorruptFile_IsEmptyNotAnError()
    {
        var path = TempFile();
        var store = new WifiWatchStore(path);
        Assert.Null(store.Summarize(T0));
        File.WriteAllText(path, "{ not json");
        try { Assert.Empty(store.Load(T0)); }
        finally { File.Delete(path); }
    }

    // ---- Run key ----

    [Fact]
    public void RunCommand_IsTheQuotedPathOnly()
    {
        Assert.Equal("\"C:\\Program Files\\AutoFix Scout\\AutoFixScout.Watch.exe\"",
            WifiWatcherSetup.RunCommand(@"C:\Program Files\AutoFix Scout\AutoFixScout.Watch.exe"));
        Assert.Equal("\"C:\\a.exe\"", WifiWatcherSetup.RunCommand("\"C:\\a.exe\""));
        Assert.Equal("AutoFixScout.Watch.exe", Path.GetFileName(WifiWatcherSetup.ExePath(@"C:\x")));
    }

    [Fact]
    public void TurnOnAndOff_WriteAndRemoveTheRunValue_OnAScratchKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afs-watch-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, WifiWatcherSetup.ExeName);
        var scratch = @"Software\AutoFixScoutTests\" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(scratch);
        try
        {
            Assert.Equal(Tri.Unknown, WifiWatcherSetup.ReadState(root, exe));
            Assert.False(WifiWatcherSetup.TurnOn(root, exe, start: false).Ok);
            File.WriteAllText(exe, "x");
            Assert.Equal(Tri.No, WifiWatcherSetup.ReadState(root, exe));
            Assert.True(WifiWatcherSetup.TurnOn(root, exe, start: false).Ok);
            using (var run = root.OpenSubKey(WifiWatcherSetup.RunKey))
                Assert.Equal("\"" + exe + "\"", run!.GetValue(WifiWatcherSetup.ValueName));
            Assert.Equal(Tri.Yes, WifiWatcherSetup.ReadState(root, exe));
            Assert.True(WifiWatcherSetup.TurnOff(root, stop: false).Ok);
            Assert.Equal(Tri.No, WifiWatcherSetup.ReadState(root, exe));
            Assert.True(WifiWatcherSetup.TurnOff(root, stop: false).Ok); // twice is fine
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Actions_AreUserLevel_NeverElevated()
    {
        Assert.DoesNotContain("network.wifi_watch_on", FixActions.ElevatedActions);
        Assert.DoesNotContain("network.wifi_watch_off", FixActions.ElevatedActions);
    }

    // ---- ladder ----

    private static NetworkState Drops(Tri watcher)
    {
        var s = WifiDropsTests.Working(unintentional: 9);
        s.WifiWatcher = watcher;
        return s;
    }

    private static List<Rung> Ladder(NetworkState s) => NetworkTriage.Ladder(s, NetworkTriage.Evaluate(s)).ToList();

    [Fact]
    public void Rung_IsOfferedWhenDropsAreRaisedAndTheWatcherIsOff()
    {
        var rung = Assert.Single(Ladder(Drops(Tri.No)), r => r.Id == "wifi_watch");
        Assert.Equal("network.wifi_watch_on", rung.ActionId);
        Assert.Equal(1, rung.Tier);
    }

    [Theory]
    [InlineData(Tri.Yes)]
    [InlineData(Tri.Unknown)]
    public void Rung_IsAbsentWhenTheWatcherIsOnOrNotInstalled(Tri watcher) =>
        Assert.DoesNotContain(Ladder(Drops(watcher)), r => r.Id == "wifi_watch");

    [Fact]
    public void Rung_IsAbsentWithoutTheDropsFinding()
    {
        var s = WifiDropsTests.Working(unintentional: 2);
        s.WifiWatcher = Tri.No;
        Assert.DoesNotContain(Ladder(s), r => r.Id == "wifi_watch");
    }

    [Fact]
    public void RungText_FollowsTheWritingGuide_AndExplainsTheWatcher()
    {
        var say = Ladder(Drops(Tri.No)).Single(r => r.Id == "wifi_watch").Say;
        Assert.DoesNotMatch(new Regex(@"n't|'s |'re |'ll |'ve |'d |(^|[\s""(])I\s|\bwe\b|!", RegexOptions.IgnoreCase), say);
        Assert.Contains("starts with Windows", say);
        Assert.Contains("reconnects", say);
        Assert.Contains("sends nothing anywhere", say);
        Assert.Contains("switch it off", say);
        Assert.Contains("Startup", say);
        Assert.All(Regex.Split(say, @"(?<=\.)\s"), sentence => Assert.True(sentence.Split(' ').Length <= 30, sentence));
    }

    [Fact]
    public void FindingDetail_MentionsTheWatcherReconnects()
    {
        var s = Drops(Tri.Yes);
        s.WifiWatch = new WifiWatchSummary(7, 5, 30);
        var f = NetworkTriage.Evaluate(s).Single(x => x.Code == FindingCode.NetWifiDrops);
        Assert.Contains("the watcher reconnected 5 times", f.TechnicalDetail);
    }

    // ---- live, read only ----

    [Fact]
    public void Live_ReadsTheWatcherState_WithoutChangingAnything()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        var state = WifiWatcherSetup.ReadState();
        Assert.True(Enum.IsDefined(state));
        _ = new WifiWatchStore().Summarize(DateTime.UtcNow);
        // netsh show interfaces only reads.
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "wlan show interfaces")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = WifiNetsh.ParseInterfaces(text);
    }
}
