using System.Text.RegularExpressions;
using Xunit;

namespace AutoFixScout.Tests;

public class AppLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afs-applog-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    private AppLogFile Log(IEnumerable<string>? hide = null) => new(_dir, "9.9.9", hide == null ? null : () => hide);

    private static Exception Thrown(Action a)
    {
        try { a(); } catch (Exception ex) { return ex; }
        throw new InvalidOperationException("nothing thrown");
    }

    private static void Deep(int n) { if (n == 0) throw new InvalidOperationException("deep"); Deep(n - 1); }

    [Fact]
    public void WritesOneLinePerEntryWithVersionAndArea()
    {
        var log = Log();
        log.Info("start", "AutoFix Scout 9.9.9 started");
        log.Warn("audio probe", new IOException("part failed"));
        var lines = log.Recent(10);
        Assert.Equal(2, lines.Count);
        Assert.Contains("| INFO | 9.9.9 | start |", lines[0]);
        Assert.Contains("| WARN | 9.9.9 | audio probe | System.IO.IOException: part failed", lines[1]);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d \| ", lines[0]);
        Assert.True(Directory.GetFiles(_dir, $"app-{DateTime.Now:yyyy-MM}.log").Length == 1);
    }

    [Fact]
    public void UsesTheActivityAreaWhenNoneIsGiven()
    {
        var log = Log();
        using (Activity.Scope("graphics", announce: false)) log.Write("WARN", "", "something");
        Assert.Contains("| graphics |", log.Recent(1)[0]);
    }

    [Fact]
    public void RecentReturnsTheNewestEntriesOldestFirst()
    {
        var log = Log();
        for (var i = 1; i <= 30; i++) log.Info("t", $"entry {i}");
        var last = log.Recent(20);
        Assert.Equal(20, last.Count);
        Assert.EndsWith("entry 11", last[0]);
        Assert.EndsWith("entry 30", last[^1]);
    }

    [Fact]
    public void FilesStayUnderOneMegabyteAndOnlyThreeAreKept()
    {
        var log = Log();
        var text = new string('x', 3000);
        for (var i = 0; i < 1800; i++) log.Info("fill", text); // about 5.4 MB in total
        var files = Directory.GetFiles(_dir, "app-*.log");
        Assert.Equal(AppLogFile.FilesKept, files.Length);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= AppLogFile.MaxFileBytes, f));
        Assert.NotEmpty(log.Recent(20));
    }

    [Fact]
    public void RotationKeepsTheNewestEntries()
    {
        var log = Log();
        var text = new string('y', 3000);
        for (var i = 1; i <= 1500; i++) log.Info("fill", $"{i} {text}");
        Assert.StartsWith("1500 ", log.Recent(1)[0].Split(" | ")[4]);
    }

    [Fact]
    public void ConcurrentWritesAreAllKeptWhole()
    {
        var log = Log();
        Parallel.For(0, 200, i => log.Info("par", $"line number {i:D3} end"));
        var lines = log.Recent(500);
        Assert.Equal(200, lines.Count);
        Assert.All(lines, l => Assert.Matches(@" \| par \| line number \d{3} end$", l));
        Assert.Equal(200, lines.Select(l => Regex.Match(l, @"\d{3} end$").Value).Distinct().Count());
    }

    [Fact]
    public void RedactsUserMachinePathNetworkNameAndIpAddress()
    {
        var log = Log(new[] { "Zebra-Fake-User", "FAKE-LAPTOP-9" });
        log.Info("net", @"user Zebra-Fake-User on FAKE-LAPTOP-9 opened C:\Users\Jane Doe\Documents\a.txt, ssid: CafeWifi, gateway 192.168.1.77, mac 00:1A:2B:3C:4D:5E, user " + Environment.UserName);
        var line = log.Recent(1)[0];
        Assert.DoesNotContain("Zebra-Fake-User", line);
        Assert.DoesNotContain("FAKE-LAPTOP-9", line);
        Assert.DoesNotContain("Jane", line);
        Assert.DoesNotContain("CafeWifi", line);
        Assert.DoesNotContain("192.168.1.77", line);
        Assert.DoesNotContain("00:1A:2B", line);
        Assert.Contains("%USERPROFILE%", line);
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(Environment.UserName + ",", line);
        // the file itself holds the redacted text too
        Assert.DoesNotContain("CafeWifi", File.ReadAllText(Directory.GetFiles(_dir)[0]));
    }

    [Fact]
    public void ErrorsKeepTypeMessageAndAtMostEightFramesWithoutPaths()
    {
        var ex = Thrown(() => Deep(20));
        var frames = AppLogFile.Frames(ex);
        Assert.Equal(AppLogFile.MaxFrames, frames.Count);
        Assert.All(frames, f => { Assert.DoesNotContain(@"\", f); Assert.DoesNotContain(" in ", f); Assert.DoesNotContain("(", f); });
        var log = Log();
        log.Error("screen", ex);
        var line = log.Recent(1)[0];
        Assert.Contains("| ERROR | 9.9.9 | screen | System.InvalidOperationException: deep | at AppLogTests.Deep", line);
        Assert.Equal(AppLogFile.MaxFrames - 1, Regex.Matches(line, "; at ").Count);
        Assert.DoesNotMatch(@"[A-Za-z]:\\", line);
        Assert.DoesNotContain(".cs:line", line);
    }

    [Fact]
    public void NeverThrowsWhenTheFolderCannotBeUsed()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "file-not-folder");
        File.WriteAllText(blocker, "x");
        var inside = new AppLogFile(Path.Combine(blocker, "logs"), "1.0.0"); // a folder below a file cannot exist
        var bad = new AppLogFile("Z:\\no\\such\\drive\\<>|", "1.0.0");
        var readOnly = new AppLogFile(_dir, "1.0.0");
        var path = Path.Combine(_dir, $"app-{DateTime.Now:yyyy-MM}.log");
        File.WriteAllText(path, "old\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            foreach (var l in new[] { inside, bad, readOnly })
            {
                var e = Record.Exception(() => { l.Info("a", "b"); l.Warn("a", new Exception("x")); l.Error("a", new Exception("y")); _ = l.Recent(5); });
                Assert.Null(e);
            }
            Assert.Empty(inside.Recent(5));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.Null(Record.Exception(() => AppLog.Warn("test", new Exception("static call"))));
        Assert.Null(Record.Exception(() => AppLog.Warn("test", (Exception)null!)));
    }

    [Fact]
    public void AHidingFunctionThatThrowsDoesNotStopTheLog()
    {
        var log = new AppLogFile(_dir, "1.0.0", () => throw new InvalidOperationException("no"));
        log.Info("a", "still written");
        Assert.Single(log.Recent(5));
    }

    [Fact]
    public void TheHelperReportShowsTheLastProblemsRedactedAndEncoded()
    {
        var lines = Enumerable.Range(1, 3).Select(i => $"2026-11-14 10:0{i}:00 | WARN | 1.2.3 | audio | <b>problem {i}</b> at {Environment.UserName}").ToList();
        var html = HelperReport.BuildHtml("audio", Array.Empty<Triage.Finding>(), Array.Empty<string>(), Outcome.Fixed, new DateTime(2026, 11, 14),
            Array.Empty<SummaryLine>(), Array.Empty<ChangeEntry>(), Array.Empty<ActivityItem>(), Array.Empty<SessionLog.SessionSummary>(), "1.2.3",
            null, lines);
        Assert.Contains("<h2>Problems in AutoFix Scout itself</h2>", html);
        Assert.Contains("&lt;b&gt;problem 2&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>problem", html);
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(" at " + Environment.UserName, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHelperReportSaysSoWhenNothingWasRecorded()
    {
        var html = HelperReport.BuildHtml("audio", Array.Empty<Triage.Finding>(), Array.Empty<string>(), Outcome.Fixed, new DateTime(2026, 11, 14),
            Array.Empty<SummaryLine>(), Array.Empty<ChangeEntry>(), Array.Empty<ActivityItem>(), Array.Empty<SessionLog.SessionSummary>(), "1.2.3");
        Assert.Contains("Problems in AutoFix Scout itself", html);
        Assert.Contains("No problems in AutoFix Scout itself have been recorded.", html);
    }
}
