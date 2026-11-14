using System.Text.RegularExpressions;
using AutoFixScout.Repair;
using AutoFixScout.Triage;
using Xunit;

namespace AutoFixScout.Tests;

public class BrowserHijackTests
{
    private const string IdA = "abcdefghijklmnopabcdefghijklmnop";
    private const string IdB = "ponmlkjihgfedcbaponmlkjihgfedcba";
    private const string Google = "ghbmnnjooekpmoecnnnilnnbdlolhkhi"; // Google Docs Offline, on the allow-list

    private sealed class Fake : IBrowserRegistry
    {
        public Dictionary<string, Dictionary<string, string>> Values = new();
        public Dictionary<string, List<string>> Lists = new();
        public Dictionary<string, List<(string, string?)>> External = new();
        public IReadOnlyDictionary<string, string> PolicyValues(string hive, string browser) =>
            Values.TryGetValue(hive + browser, out var v) ? v : new Dictionary<string, string>();
        public IReadOnlyList<string> PolicyList(string hive, string browser, string subkey) =>
            Lists.TryGetValue(hive + browser + subkey, out var v) ? v : new List<string>();
        public IReadOnlyList<(string Id, string? UpdateUrl)> ExternalExtensions(string hive, string browser) =>
            External.TryGetValue(hive + browser, out var v) ? v : new List<(string, string?)>();
    }

    private static BrowserHijackState Scan(Fake f, bool managed = false) => BrowserHijackScan.Scan(f, managed);

    // ---- host extraction ----
    [Theory]
    [InlineData("https://www.Example-Search.net/results?q={searchTerms}&user=bob", "www.example-search.net")]
    [InlineData("http://user:pw@host.example.org:8080/a/b#c", "host.example.org")]
    [InlineData("example.net/path?x=1", "example.net")]
    [InlineData("example.net:8080", "example.net")]
    [InlineData("https://*.example.net/*", "example.net")]
    [InlineData("*://example.net/*", "example.net")]
    [InlineData("https://example.net./", "example.net")]
    [InlineData("https://bücher.example/x", "xn--bcher-kva.example")]
    [InlineData("https://[2001:db8::1]/x", "2001:db8::1")]
    [InlineData("https://192.168.1.5/search", "192.168.1.5")]
    public void ExtractHost_ReturnsOnlyTheHost(string url, string host) => Assert.Equal(host, BrowserHijackScan.ExtractHost(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("about:blank")]
    [InlineData("chrome://newtab")]
    [InlineData("edge://settings")]
    [InlineData("file:///C:/Users/bob/x.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("localdomain")]
    [InlineData("https://")]
    [InlineData("https://exa mple.net")]
    [InlineData("https://{searchTerms}")]
    [InlineData("https://-bad-.example")]
    public void ExtractHost_RejectsOddValues(string? url) => Assert.Null(BrowserHijackScan.ExtractHost(url));

    [Fact]
    public void ExtractHost_NeverKeepsPathOrQuery()
    {
        var h = BrowserHijackScan.ExtractHost("https://evil.example/Users/Bob/secret?token=abc#frag")!;
        Assert.DoesNotContain("/", h); Assert.DoesNotContain("?", h); Assert.DoesNotContain("Bob", h, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractHost_VeryLongInputIsRejected() => Assert.Null(BrowserHijackScan.ExtractHost("https://" + new string('a', 3000) + ".com"));

    // ---- allow-list of hosts ----
    [Theory]
    [InlineData("www.google.com", true)]
    [InlineData("google.co.uk", true)]
    [InlineData("search.yahoo.com", true)]
    [InlineData("yandex.ru", true)]
    [InlineData("www.bing.com", true)]
    [InlineData("duckduckgo.com", true)]
    [InlineData("www.ecosia.org", true)]
    [InlineData("startpage.com", true)]
    [InlineData("search.brave.com", true)]
    [InlineData("www.msn.com", true)]
    [InlineData("google.com.evil.net", false)]
    [InlineData("google.evil-site.net", false)]
    [InlineData("notgoogle.com", false)]
    [InlineData("bing.com.example.org", false)]
    [InlineData("evilbing.com", false)]
    [InlineData("example-search.net", false)]
    public void WellKnownHosts(string host, bool known) => Assert.Equal(known, BrowserHijackScan.IsWellKnownHost(host));

    // ---- extension ids ----
    [Theory]
    [InlineData(IdA, true)]
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmno", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmnopa", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmnoz", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmno1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ExtensionIdValidation(string? id, bool valid) => Assert.Equal(valid, BrowserHijackScan.IsValidExtensionId(id));

    [Theory]
    [InlineData(IdA + ";https://clients2.google.com/service/update2/crx", IdA)]
    [InlineData(IdA, IdA)]
    [InlineData(" " + IdA + " ;x", IdA)]
    [InlineData("not-an-id;https://x.example", null)]
    [InlineData(";https://x.example", null)]
    public void ForcedId_ParsesTheListEntry(string entry, string? id) => Assert.Equal(id, BrowserHijackScan.ForcedId(entry));

    [Fact]
    public void AllowListIdsAreValidAndNotReported()
    {
        Assert.All(BrowserHijackScan.FirstPartyIds, id => Assert.True(BrowserHijackScan.IsValidExtensionId(id)));
        Assert.False(BrowserHijackScan.IsReportable(Google));
        Assert.True(BrowserHijackScan.IsReportable(IdA));
    }

    // ---- policy parsing ----
    [Fact]
    public void Scan_ReadsSearchStartAndNewTabHostsOnly()
    {
        var f = new Fake();
        f.Values["HKCUEdge"] = new()
        {
            ["DefaultSearchProviderSearchURL"] = "https://example-search.net/s?q={searchTerms}",
            ["HomepageLocation"] = "https://home.example.org/start?uid=77",
            ["NewTabPageLocation"] = "https://www.bing.com/",
            ["DefaultSearchProviderEnabled"] = "1",
        };
        f.Lists["HKLMChromeRestoreOnStartupURLs"] = new() { "https://a.example.com/x", "https://www.google.com/", "about:blank" };
        var s = Scan(f);
        Assert.Equal(new[] { ("Edge", "search engine", "example-search.net") }, s.SearchHosts.Select(x => (x.Browser, x.Setting, x.Host)));
        Assert.Contains(s.StartHosts, x => x is { Browser: "Edge", Setting: "start page", Host: "home.example.org" });
        Assert.Contains(s.StartHosts, x => x is { Browser: "Chrome", Setting: "start-up page", Host: "a.example.com" });
        Assert.Equal(2, s.StartHosts.Count);
    }

    [Fact]
    public void Scan_ReadsForcedExtensionsInstallSourcesAndExternal()
    {
        var f = new Fake();
        f.Lists["HKLMChromeExtensionInstallForcelist"] = new() { IdA + ";https://x.example/u.xml", Google + ";https://clients2.google.com/", "junk" };
        f.Lists["HKCUEdgeExtensionInstallForcelist"] = new() { IdB };
        f.Lists["HKLMChromeExtensionInstallSources"] = new() { "https://*.dl.example.net/*" };
        f.External["HKCUChrome"] = new() { (IdA, "https://upd.example.net/crx?u=1"), (Google, null), ("bad", null) };
        var s = Scan(f);
        Assert.Equal(new[] { ("Chrome", IdA), ("Edge", IdB) }, s.Forced.Select(x => (x.Browser, x.Id)));
        Assert.Equal("dl.example.net", Assert.Single(s.InstallSources).Host);
        var e = Assert.Single(s.External);
        Assert.Equal(IdA, e.Id); Assert.Equal("upd.example.net", e.UpdateHost);
    }

    [Fact]
    public void Scan_DuplicatesAcrossHivesCountOnce()
    {
        var f = new Fake();
        f.Lists["HKLMChromeExtensionInstallForcelist"] = new() { IdA };
        f.Lists["HKCUChromeExtensionInstallForcelist"] = new() { IdA };
        Assert.Single(Scan(f).Forced);
    }

    [Fact]
    public void Scan_NothingSet_FindsNothing()
    {
        var s = Scan(new Fake());
        Assert.Empty(s.SearchHosts); Assert.Empty(s.StartHosts); Assert.Empty(s.Forced); Assert.Empty(s.External); Assert.Empty(s.InstallSources);
        Assert.Empty(SecurityTriage.BrowserHijackFindings(s));
    }

    // ---- findings on and off, managed exemption ----
    private static BrowserHijackState Full(bool managed)
    {
        var f = new Fake();
        f.Values["HKLMEdge"] = new()
        {
            ["DefaultSearchProviderSearchURL"] = "https://example-search.net/s?q={searchTerms}",
            ["HomepageLocation"] = "https://start.example.org/?id=1",
        };
        f.Lists["HKLMChromeExtensionInstallForcelist"] = new() { IdA, IdB };
        f.External["HKCUEdge"] = new() { (IdA, "https://upd.example.net/x") };
        return Scan(f, managed);
    }

    [Fact]
    public void Findings_EachOneOnAndOff()
    {
        var on = SecurityTriage.BrowserHijackFindings(Full(false)).Select(x => x.Code).ToList();
        Assert.Equal(new[] { FindingCode.BrowserSearchHijacked, FindingCode.BrowserStartPageHijacked, FindingCode.BrowserForcedExtensions, FindingCode.BrowserExternalExtensions }, on);
        var f = new Fake();
        f.Values["HKLMEdge"] = new() { ["DefaultSearchProviderSearchURL"] = "https://example-search.net/s" };
        Assert.Equal(new[] { FindingCode.BrowserSearchHijacked }, SecurityTriage.BrowserHijackFindings(Scan(f)).Select(x => x.Code));
        f = new Fake(); f.Values["HKLMEdge"] = new() { ["HomepageLocation"] = "https://start.example.org" };
        Assert.Equal(new[] { FindingCode.BrowserStartPageHijacked }, SecurityTriage.BrowserHijackFindings(Scan(f)).Select(x => x.Code));
        f = new Fake(); f.Lists["HKCUEdgeExtensionInstallForcelist"] = new() { IdA };
        Assert.Equal(new[] { FindingCode.BrowserForcedExtensions }, SecurityTriage.BrowserHijackFindings(Scan(f)).Select(x => x.Code));
        f = new Fake(); f.External["HKLMChrome"] = new() { (IdA, null) };
        Assert.Equal(new[] { FindingCode.BrowserExternalExtensions }, SecurityTriage.BrowserHijackFindings(Scan(f)).Select(x => x.Code));
    }

    [Fact]
    public void Findings_ManagedDevice_IsExempt() => Assert.Empty(SecurityTriage.BrowserHijackFindings(Full(true)));

    [Fact]
    public void Findings_WellKnownProviderAndAllowListedExtension_AreNotFlagged()
    {
        var f = new Fake();
        f.Values["HKLMChrome"] = new() { ["DefaultSearchProviderSearchURL"] = "https://www.google.com/search?q={searchTerms}", ["HomepageLocation"] = "https://duckduckgo.com/" };
        f.Lists["HKLMChromeExtensionInstallForcelist"] = new() { Google };
        Assert.Empty(SecurityTriage.BrowserHijackFindings(Scan(f)));
    }

    [Fact]
    public void Findings_NullState_NoFindings() => Assert.Empty(SecurityTriage.BrowserHijackFindings(null));

    [Fact]
    public void Findings_ExampleWording()
    {
        var f = new Fake();
        f.Values["HKCUEdge"] = new() { ["HomepageLocation"] = "https://example-search.net/home?uid=9" };
        var text = Assert.Single(SecurityTriage.BrowserHijackFindings(Scan(f))).PlainMessage;
        Assert.Equal("A program has set the start page of Microsoft Edge to \"example-search.net\". This was not done in the browser itself.", text);
    }

    [Fact]
    public void Findings_ForcedCountAndIds()
    {
        var text = SecurityTriage.BrowserHijackFindings(Full(false)).Single(x => x.Code == FindingCode.BrowserForcedExtensions).PlainMessage;
        Assert.Contains("Google Chrome has 2 extensions installed by a program, not by you", text);
        Assert.Contains(IdA, text); Assert.Contains(IdB, text);
    }

    [Fact]
    public void Findings_IpAddressIsNotShown()
    {
        var f = new Fake();
        f.Values["HKCUEdge"] = new() { ["HomepageLocation"] = "http://203.0.113.9/x" };
        var text = Assert.Single(SecurityTriage.BrowserHijackFindings(Scan(f))).PlainMessage;
        Assert.DoesNotContain("203.0.113.9", text);
        Assert.Contains("an IP address", text);
    }

    // ---- wording ----
    private static readonly Regex Contraction = new(@"n't\b|\b(I|we|it|that|there|what)'(s|m|d|ll|re|ve)\b|'(re|ve|ll)\b", RegexOptions.IgnoreCase);
    private static readonly Regex FirstPerson = new(@"\b(I|we|my|our|us)\b", RegexOptions.IgnoreCase);

    private static void AssertWording(string text)
    {
        Assert.False(Contraction.IsMatch(text), text);
        Assert.False(FirstPerson.IsMatch(text), text);
        Assert.DoesNotContain("!", text);
        Assert.EndsWith(".", text);
        Assert.True(char.IsUpper(text[0]), text);
        foreach (var sentence in Regex.Split(text, @"(?<=\.)\s+"))
            Assert.True(sentence.Split(' ').Length <= 40, sentence);
    }

    [Fact]
    public void Wording_OfFindingsAndSteps_FollowsTheWritingGuide()
    {
        var s = new SecurityState { BrowserHijack = Full(false), ManagedPc = false };
        var findings = SecurityTriage.BrowserHijackFindings(s.BrowserHijack);
        Assert.All(findings, x => AssertWording(x.PlainMessage));
        var ladder = SecurityTriage.Ladder(s, findings);
        Assert.All(ladder.Where(r => r.Id.StartsWith("browser_")), r => AssertWording(r.Say));
    }

    [Fact]
    public void Wording_InstallSourcesOnly()
    {
        var f = new Fake();
        f.Lists["HKLMEdgeExtensionInstallSources"] = new() { "https://*.dl.example.net/*" };
        var x = Assert.Single(SecurityTriage.BrowserHijackFindings(Scan(f)));
        Assert.Equal(FindingCode.BrowserForcedExtensions, x.Code);
        AssertWording(x.PlainMessage);
        Assert.Contains("\"dl.example.net\"", x.PlainMessage);
    }

    // ---- ladder ----
    [Fact]
    public void Ladder_HumanStepsOnly_UniqueIds_NoNewWriter()
    {
        var s = new SecurityState { BrowserHijack = Full(false) };
        var findings = SecurityTriage.BrowserHijackFindings(s.BrowserHijack);
        var ladder = SecurityTriage.Ladder(s, findings);
        Assert.Equal(ladder.Count, ladder.Select(r => r.Id).Distinct().Count());
        var mine = ladder.Where(r => r.Id.StartsWith("browser_")).ToList();
        Assert.Equal(new[] { "browser_reset_settings", "browser_uninstall_unknown" }, mine.Select(r => r.Id));
        Assert.All(mine, r => Assert.True(r.ActionId is "ui.human_step" or "ui.open_settings_page"));
        Assert.Contains("edge://settings", mine[0].Say); Assert.Contains("chrome://settings", mine[0].Say);
    }

    [Fact]
    public void Ladder_NoBrowserFinding_NoBrowserSteps()
    {
        var s = new SecurityState { BrowserHijack = Scan(new Fake()) };
        Assert.DoesNotContain(SecurityTriage.Ladder(s, SecurityTriage.BrowserHijackFindings(s.BrowserHijack)), r => r.Id.StartsWith("browser_"));
    }

    [Fact]
    public void Ladder_WithPolicyFinding_ReusesTheGuardedRemoval()
    {
        var s = new SecurityState { BrowserHijack = Full(false) };
        var policy = new Finding(FindingCode.SecurityBrowserPolicy, "x", "y");
        var ladder = SecurityTriage.Ladder(s, new[] { policy }.Concat(SecurityTriage.BrowserHijackFindings(s.BrowserHijack)).ToList());
        Assert.Single(ladder, r => r.ActionId == "security.remove_browser_policies");
        Assert.Equal(ladder.Count, ladder.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void RungIds_AreUniqueAcrossTheSecurityLadder()
    {
        var all = new[] { "browser_reset_settings", "browser_uninstall_unknown" };
        var src = File.ReadAllText(FindFile("src/AutoFixScout/Triage/SecurityTriage.cs"));
        foreach (var id in all) Assert.Single(Regex.Matches(src, "new\\(\"" + id + "\""));
    }

    [Fact]
    public void FindingCodes_AreAtTheEndOfTheEnum()
    {
        var names = Enum.GetNames<FindingCode>();
        Assert.Equal(new[] { "BrowserSearchHijacked", "BrowserStartPageHijacked", "BrowserForcedExtensions", "BrowserExternalExtensions" }, names[^4..]);
    }

    // ---- activity ----
    [Fact]
    public void ActivityLines_HaveNoPathsOrUserNames()
    {
        var src = File.ReadAllText(FindFile("src/AutoFixScout/Areas/SecurityFlow.cs"));
        var m = Regex.Match(src, "Part\\(\"browser search, start pages and extensions\".*?\\}\\);", RegexOptions.Singleline);
        Assert.True(m.Success);
        Assert.DoesNotMatch(@"Users|%USERPROFILE%|Environment\.UserName|AppData", m.Value);
        var b = Full(false);
        var line = $"Found {b.SearchHosts.Count + b.StartHosts.Count} unusual browser address settings and {b.Forced.Count + b.External.Count} extensions added by programs";
        Assert.All(new[] { line }, text =>
        {
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\Users\", text);
            Assert.DoesNotContain("http", text);
        });
    }

    private static string FindFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "AutoFixScout.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, rel);
    }
}
