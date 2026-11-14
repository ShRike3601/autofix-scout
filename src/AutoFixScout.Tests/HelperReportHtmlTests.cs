using System.Text.RegularExpressions;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class HelperReportHtmlTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 9, 30, 0);

    private static string Build(IReadOnlyList<Finding>? findings = null, IEnumerable<string>? tried = null, Outcome outcome = Outcome.Fixed,
        IReadOnlyList<SummaryLine>? computer = null, IReadOnlyList<ChangeEntry>? changes = null, IReadOnlyList<ActivityItem>? steps = null,
        IReadOnlyList<SessionLog.SessionSummary>? recent = null, IEnumerable<string>? hide = null) =>
        HelperReport.BuildHtml("audio", findings ?? Array.Empty<Finding>(), tried ?? Array.Empty<string>(), outcome, Now,
            computer ?? Array.Empty<SummaryLine>(), changes ?? Array.Empty<ChangeEntry>(), steps ?? Array.Empty<ActivityItem>(),
            recent ?? Array.Empty<SessionLog.SessionSummary>(), "1.2.3", hide);

    private static Finding F(string msg, string detail) => new(FindingCode.ProbeIncomplete, msg, detail);

    [Fact]
    public void Sections_AreAllPresent_InOrder()
    {
        var html = Build(new[] { F("The sound output is muted.", "muted=true") }, new[] { "Turn on sound (x): Succeeded" },
            computer: new[] { new SummaryLine("Windows", "Windows 11 Home", "x") },
            changes: new[] { new ChangeEntry(1, DateTime.UtcNow, "Restarted the audio service", true, false, null) },
            steps: new[] { new ActivityItem(Now, "audio", ActivityKind.Wmi, "Querying WMI: Win32_SoundDevice", TimeSpan.FromSeconds(1)) },
            recent: new[] { new SessionLog.SessionSummary(DateTime.UtcNow, "audio", "fixed") });
        var order = new[] { ">Summary<", ">This computer<", ">What was found<", ">What was tried<", ">Changes AutoFix Scout made<",
            ">Steps AutoFix Scout took<", ">Recent checks<", ">App version<" };
        var last = -1;
        foreach (var h in order)
        {
            var i = html.IndexOf(h, StringComparison.Ordinal);
            Assert.True(i > last, $"{h} missing or out of order");
            last = i;
        }
        Assert.Contains("<details>", html);
        Assert.Contains("version 1.2.3", html);
    }

    [Fact]
    public void Head_IsWellFormed_AndSelfContained()
    {
        var html = Build();
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\">", html);
        Assert.Contains("prefers-color-scheme:dark", html);
        Assert.Contains("@media print", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Values_AreHtmlEncoded()
    {
        var html = Build(new[] { F("A <b>\"bold\"</b> & more", "x<y>&\"z\"") }, new[] { "<i>step</i> & \"done\"" });
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<i>", html);
        Assert.Contains("&lt;b&gt;&quot;bold&quot;&lt;/b&gt; &amp; more", html);
        Assert.Contains("&lt;i&gt;step&lt;/i&gt; &amp; &quot;done&quot;", html);
    }

    [Fact]
    public void Privacy_NothingPersonalAppears()
    {
        const string user = "jdoe_secret", pc = "DESKTOP-SECRETPC", ssid = "SecretNet";
        var path = $@"C:\Users\{user}\AppData\Local\Temp\x.log";
        var html = Build(
            new[] { F($"Connected to {ssid} as {user}", $"Ssid: {ssid}; ip 192.168.1.77; fe80::1ff:fe23:4567:890a; mac AA:BB:CC:11:22:33; file {path}; Serial Number: SN123456789; host {pc}") },
            new[] { $"Checked {path} on {pc}" },
            computer: new[] { new SummaryLine("Disk", $@"C:\Users\{user}\Documents", "x") },
            changes: new[] { new ChangeEntry(1, DateTime.UtcNow, $"Changed {path}", false, false, $"Ask {user}") },
            steps: new[] { new ActivityItem(Now, "audio", ActivityKind.Files, $"Reading {path} for {ssid}", null) },
            hide: new[] { user, pc, ssid });
        foreach (var secret in new[] { user, pc, ssid, "192.168.1.77", "fe80::1ff", "AA:BB:CC:11:22:33", "SN123456789", @"C:\Users\" })
            Assert.DoesNotContain(secret, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", html);
    }

    [Fact]
    public void Privacy_NetworkNameAndSerialPatterns_AreRemovedWithoutHints()
    {
        var html = Build(new[] { F("Wi-Fi", "SSID: SecretNet, serial: ABC123XYZ") });
        Assert.DoesNotContain("SecretNet", html);
        Assert.DoesNotContain("ABC123XYZ", html);
    }

    [Fact]
    public void Privacy_CurrentUserAndComputerNames_AreRemoved()
    {
        var html = Build(new[] { F($"{Environment.MachineName} {Environment.UserName}", $@"C:\Users\{Environment.UserName}\x") });
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(Environment.UserName, html.Replace("%USERPROFILE%", ""), StringComparison.OrdinalIgnoreCase);
        if (Environment.MachineName.Length >= 3) Assert.DoesNotContain(Environment.MachineName, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyData_ShowsPlainSentences()
    {
        var html = Build(outcome: Outcome.NoFaultFound);
        Assert.Contains("No findings were recorded.", html);
        Assert.Contains("No steps were tried.", html);
        Assert.Contains("AutoFix Scout has not made any changes to this computer.", html);
        Assert.Contains("No steps were recorded for this check.", html);
        Assert.Contains("No checks have been carried out on this computer yet.", html);
        Assert.Contains("No problem was found, so no changes were made.", html);
        Assert.DoesNotContain("<details>", html);
    }

    [Fact]
    public void RecentChecks_AreLimitedToTen()
    {
        var recent = Enumerable.Range(0, 15).Select(i => new SessionLog.SessionSummary(DateTime.UtcNow.AddDays(-i), "camera", "fixed")).ToList();
        var html = Build(recent: recent);
        Assert.Equal(10, Regex.Matches(html, "<td>Camera</td>").Count);
    }

    [Fact]
    public void Save_UsesTheDatedName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afs-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = HelperReport.SaveHtml("<html></html>", new DateTime(2026, 11, 14, 9, 5, 0), dir);
            Assert.Equal("AutoFix-Scout-report-2026-11-14-0905.html", Path.GetFileName(path));
            Assert.True(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void FixedSentences_FollowTheWritingGuide()
    {
        var html = Build();
        var text = Regex.Replace(Regex.Replace(html, @"(?s)<style>.*?</style>", ""), "<[^>]+>", " ");
        var sentences = Enum.GetValues<Outcome>().Select(HelperReport.OutcomeSentence).Append(text).ToList();
        foreach (var s in sentences)
        {
            Assert.DoesNotMatch(@"n't|'d |'ll |'re |'ve |it's|that's|I'm", s);
            Assert.DoesNotMatch(@"\bI\b|\bwe\b|\bWe\b|!", s);
        }
    }

    [Fact]
    public void WriteSample_WhenAsked()
    {
        var dir = Environment.GetEnvironmentVariable("AFS_SAMPLE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var html = HelperReport.BuildHtml("audio",
            new[] { F("The sound output is muted.", "DefaultMuted=Yes; volume 2 percent"), F("The audio service is stopped.", "AudioSrv: Stopped") },
            new[] { "Turn the volume back on (unmute): Succeeded", "Restart the Windows Audio service (restart-audio): Succeeded -- fixed it: yes" },
            Outcome.Fixed, new DateTime(2026, 11, 14, 9, 30, 0),
            new[] { new SummaryLine("Windows", "Windows 11 Home 24H2 (build 26100)", "x"), new SummaryLine("Processor", "Intel Core i7-10750H", "x"), new SummaryLine("Memory", "16 GB", "x"), new SummaryLine("Disk C:", "420 GB free of 476 GB", "x") },
            new[] { new ChangeEntry(1, new DateTime(2026, 11, 14, 4, 0, 0, DateTimeKind.Utc), "Restarted the Windows Audio service", false, false, "This change cannot be undone automatically."), new ChangeEntry(2, new DateTime(2026, 11, 13, 4, 0, 0, DateTimeKind.Utc), "Updated the sound driver", true, false, null) },
            new[] { new ActivityItem(new DateTime(2026, 11, 14, 9, 29, 1), "audio", ActivityKind.Wmi, @"Querying WMI: Win32_SoundDevice (C:\Users\Fake\x)", TimeSpan.FromSeconds(0.4)), new ActivityItem(new DateTime(2026, 11, 14, 9, 29, 3), "audio", ActivityKind.Change, "Restarting the Windows Audio service", TimeSpan.FromSeconds(1.2)) },
            new[] { new SessionLog.SessionSummary(DateTime.UtcNow, "audio", "fixed"), new SessionLog.SessionSummary(DateTime.UtcNow.AddDays(-1), "camera", "no_fault_found"), new SessionLog.SessionSummary(DateTime.UtcNow.AddDays(-2), "network", "not_fixed") },
            "1.2.3");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sample-report.html"), html, new System.Text.UTF8Encoding(false));
    }
}
