using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class StartupSlowMatchTests
{
    private const string Win = @"C:\Windows";
    private static StartupEntry U(string name, string cmd) => new(name, cmd, StartupScope.UserRun, true);
    private static SlowStartupItem Slow(string name, long ms, string? file = null, int id = 101) => new(name, ms, file, id);

    private static StorageState Base() => new()
    {
        FreeBytes = 120L << 30, TotalBytes = 256L << 30, ReclaimableTempBytes = 0, RecycleBinBytes = 0, StartupAppCount = 3,
        StartupEntries = Array.Empty<StartupEntry>(), TopProcesses = Array.Empty<ProcessUsage>(), WindowsDirectory = Win,
        Uptime = TimeSpan.FromDays(1), MemoryLoadPercent = 40, ProblemDevices = Array.Empty<StorageDeviceInfo>(),
        BootTimes = new[] { new BootRecord(DateTime.UtcNow, 95000, 90000, 0) },
    };

    private static readonly Regex BadWords = new(@"n't|'s\b|'ll|'re|'ve|'m\b|'d\b|!|\b(I|we|my|our|us)\b", RegexOptions.IgnoreCase);

    [Fact]
    public void Matches_ByExecutableName_IgnoringCase()
    {
        var e = U("Run value", @"""C:\Users\a\AppData\Roaming\Spotify\Spotify.exe"" /minimized");
        Assert.Single(StartupSlowMatch.Match(new[] { Slow("Anything", 9000, @"C:\x\SPOTIFY.EXE") }, new[] { e }));
        Assert.Single(StartupSlowMatch.Match(new[] { Slow("spotify.exe", 9000) }, new[] { e }));
    }

    [Fact]
    public void Matches_ByNormalisedDisplayName_AndShortcutTarget()
    {
        Assert.Single(StartupSlowMatch.Match(new[] { Slow("Spotify", 9000) }, new[] { U("SPOTIFY", @"C:\z\launcher.exe") }));
        Assert.Single(StartupSlowMatch.Match(new[] { Slow("Battle.net", 5000) }, new[] { U("Battle net", @"C:\z\launcher.exe") }));
        var shortcut = new StartupEntry("Helper Tool.lnk", @"""C:\Tools\helper.exe""", StartupScope.UserFolder, true);
        Assert.Single(StartupSlowMatch.Match(new[] { Slow("Other", 5000, "helper.exe") }, new[] { shortcut }));
        Assert.Single(StartupSlowMatch.Match(new[] { Slow("Helper Tool", 5000) }, new[] { shortcut }));
    }

    [Fact]
    public void SimilarNames_AreNotConfused()
    {
        var teams = U("Teams", @"C:\a\Teams.exe");
        var viewer = U("TeamViewer", @"C:\a\TeamViewer.exe");
        var m = StartupSlowMatch.Match(new[] { Slow("TeamViewer", 7000) }, new[] { teams, viewer });
        Assert.Equal("TeamViewer", Assert.Single(m).Entry.Name);
        Assert.Empty(StartupSlowMatch.Match(new[] { Slow("Teams Helper", 7000) }, new[] { teams }));
    }

    [Theory]
    [InlineData("Spotify", "spotify.sys", 101)]
    [InlineData("Spotify Driver", "Spotify.exe", 102)]
    [InlineData("Spotify Service", "Spotify.exe", 103)]
    [InlineData("spotify.sys", null, 0)]
    public void DriversAndServices_AreNeverMatched(string name, string? file, int id)
    {
        Assert.Empty(StartupSlowMatch.Match(new[] { Slow(name, 9000, file, id) }, new[] { U("Spotify", @"C:\a\Spotify.exe") }));
    }

    [Fact]
    public void UnmatchedItem_IsLeftOut()
    {
        Assert.Empty(StartupSlowMatch.Match(new[] { Slow("Contoso Installer", 30000, "setup.exe") }, new[] { U("Spotify", @"C:\a\Spotify.exe") }));
        Assert.Empty(StartupSlowMatch.Match(null, new[] { U("Spotify", @"C:\a\Spotify.exe") }));
    }

    [Fact]
    public void Offers_AreOrderedByDelay_ThenCurrentOrder_WithoutDuplicates()
    {
        var s = Base();
        s.StartupEntries = new[]
        {
            U("Steam", @"C:\a\steam.exe"), U("Spotify", @"C:\a\spotify.exe"), U("Zeta", @"C:\a\zeta.exe"),
            U("Discord", @"C:\a\discord.exe"), U("Alpha", @"C:\a\alpha.exe"),
        };
        s.SlowStartupItems = new[] { Slow("Zeta", 4000), Slow("Spotify", 9000), Slow("spotify.exe", 2000, "spotify.exe") };
        var f = StorageTriage.Evaluate(s);
        var l = StorageTriage.Ladder(s, f);
        var off = l.Where(r => r.ActionId == "startup.disable").ToList();
        Assert.Equal(new[] { "Spotify", "Zeta", "Steam", "Discord", "Alpha" }, off.Select(r => r.Arg));
        Assert.Equal(off.Count, off.Select(r => r.Arg).Distinct().Count());
        Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        Assert.Contains("about 9 seconds", off[0].Say);
        Assert.Contains("about 4 seconds", off[1].Say);
        Assert.DoesNotContain("Windows measured", off[2].Say);
        Assert.Contains("Settings > Apps > Startup", off[0].Say);
    }

    [Fact]
    public void SlowEntryBeyondFive_IsStillOffered()
    {
        var s = Base();
        s.StartupEntries = Enumerable.Range(0, 6).Select(i => U($"Tool {i}", $@"C:\a\t{i}.exe")).Append(U("Zzz", @"C:\a\zzz.exe")).ToList();
        s.SlowStartupItems = new[] { Slow("Zzz", 6000) };
        Assert.Equal("Zzz", StorageTriage.StartupOffers(s)[0].Name);
        Assert.Equal(StartupApps.MaxOffered, StorageTriage.StartupOffers(s).Count);
    }

    [Fact]
    public void ProtectedEntries_AreNeverOffered()
    {
        var s = Base();
        s.StartupEntries = new[]
        {
            U("OneDrive", @"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background"),
            U("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe"),
            U("Avast", @"C:\Avast\AvastUI.exe"), U("Steam", @"C:\a\steam.exe"),
            new StartupEntry("Machine", @"C:\a\machine.exe", StartupScope.MachineRun, true),
        };
        s.SlowStartupItems = new[] { Slow("OneDrive.exe", 40000), Slow("SecurityHealthSystray.exe", 9000), Slow("Avast", 8000), Slow("Machine", 7000) };
        var off = StorageTriage.Ladder(s, StorageTriage.Evaluate(s)).Where(r => r.ActionId == "startup.disable").ToList();
        Assert.Equal("Steam", Assert.Single(off).Arg);
        Assert.DoesNotContain("Windows measured", off[0].Say);
        Assert.Empty(StorageTriage.SlowMatches(s));
    }

    private static string SentenceFor(params (string Name, long Ms)[] apps)
    {
        var s = Base();
        s.StartupEntries = apps.Select(a => U(a.Name, $@"C:\a\{a.Name}.exe")).ToList();
        s.SlowStartupItems = apps.Select(a => Slow(a.Name, a.Ms)).ToList();
        return Assert.Single(StorageTriage.Evaluate(s), x => x.Code == FindingCode.StorageSlowStartup).PlainMessage;
    }

    [Fact]
    public void Sentence_OneTwoAndThreePlusApps()
    {
        Assert.Contains("Windows measured that \"Spotify\" delayed the start of Windows by about 9 seconds.", SentenceFor(("Spotify", 9000)));
        var two = SentenceFor(("Spotify", 9000), ("Steam", 4400));
        Assert.Contains("Windows measured that \"Spotify\" delayed the start of Windows by about 9 seconds, and \"Steam\" by about 4 seconds.", two);
        var four = SentenceFor(("Spotify", 9000), ("Steam", 4400), ("Discord", 1000), ("Dropbox", 500));
        Assert.Contains("\"Spotify\" by about 9 seconds, \"Steam\" by about 4 seconds, and \"Discord\" by about 1 second.", four);
        Assert.DoesNotContain("Dropbox", four);
    }

    [Fact]
    public void Wording_FollowsTheWritingGuide_AndRungIdsAreUnique()
    {
        foreach (var apps in new[] { new[] { ("Spotify", 9000L) }, new[] { ("Spotify", 9000L), ("Steam", 4000L) }, new[] { ("Spotify", 9000L), ("Steam", 4000L), ("Discord", 2000L), ("Dropbox", 1000L) } })
        {
            var s = Base();
            s.StartupEntries = apps.Select(a => U(a.Item1, $@"C:\a\{a.Item1}.exe")).ToList();
            s.SlowStartupItems = apps.Select(a => Slow(a.Item1, a.Item2)).ToList();
            var f = StorageTriage.Evaluate(s);
            var l = StorageTriage.Ladder(s, f);
            foreach (var t in f.Select(x => x.PlainMessage).Concat(l.Select(r => r.Say)))
                Assert.DoesNotMatch(BadWords, t);
            Assert.Equal(l.Count, l.Select(r => r.Id).Distinct().Count());
        }
    }

    [Fact]
    public void ParseSlowItem_KeepsFileNameAndEventId()
    {
        var xml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>101</EventID></System><EventData>" +
                  "<Data Name='Name'>Spotify.exe</Data><Data Name='FriendlyName'>Spotify</Data><Data Name='TotalTime'>9000</Data></EventData></Event>";
        var i = StartupTimings.ParseSlowItem(xml)!;
        Assert.Equal(("Spotify", 9000L, "Spotify.exe", 101), (i.Name, i.TotalMs, i.FileName, i.EventId));
    }
}
