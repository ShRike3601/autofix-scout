using System.Net;
using System.Text;
using AutoFixScout.Updates;
using Xunit;

namespace AutoFixScout.Tests;

public class PrereleaseOrderTests
{
    [Theory]
    [InlineData("0.4.0-beta.2", "0.4.0-beta.1", true)]
    [InlineData("0.4.0-beta.10", "0.4.0-beta.2", true)]
    [InlineData("0.4.0-beta.2", "0.4.0-beta.10", false)]
    [InlineData("0.4.0", "0.4.0-beta.2", true)]
    [InlineData("0.4.0-beta.2", "0.4.0", false)]
    [InlineData("0.4.0", "0.4.0-beta.1", true)]            // beta build moves to the stable release
    [InlineData("0.4.0-beta.1", "0.3.1", true)]
    [InlineData("0.3.1", "0.4.0-beta.1", false)]
    [InlineData("0.4.0-rc.1", "0.4.0-beta.9", true)]       // words compare as text
    [InlineData("0.4.0-beta.1", "0.4.0-alpha.9", true)]
    [InlineData("0.4.0-beta.1", "0.4.0-beta", true)]       // a longer list is newer
    [InlineData("0.4.0-beta", "0.4.0-beta.1", false)]
    [InlineData("0.4.0-beta.x", "0.4.0-beta.9", true)]     // number before word
    [InlineData("0.4.0-beta.9", "0.4.0-beta.x", false)]
    [InlineData("0.4.0-beta.01", "0.4.0-beta.1", false)]   // equal numerically
    [InlineData("0.4.1-beta.1", "0.4.0", true)]
    [InlineData("0.4.0-beta.1", "0.4.0-beta.1", false)]
    [InlineData("0.4.0-beta.1+abc", "0.4.0-beta.1", false)]
    [InlineData("0.4.0-beta.1.2", "0.4.0-beta.1", true)]
    [InlineData("0.4.0-beta.123456789012345678901234567890", "0.4.0-beta.99999999999999999999", true)]
    public void IsNewer(string candidate, string current, bool expected) =>
        Assert.Equal(expected, AppVersion.IsNewer(AppVersion.Parse(candidate)!.Value, AppVersion.Parse(current)!.Value));

    [Fact]
    public void SortsInSemanticOrder()
    {
        var order = new[] { "0.4.0-alpha", "0.4.0-alpha.1", "0.4.0-alpha.beta", "0.4.0-beta", "0.4.0-beta.2", "0.4.0-beta.11", "0.4.0-rc.1", "0.4.0" };
        var shuffled = order.Reverse().Select(s => AppVersion.Parse(s)!.Value).ToList();
        shuffled.Sort();
        Assert.Equal(order, shuffled.Select(v => v.ToString()));
    }
}

public class BetaChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afs-beta-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Rel(string tag, bool pre = false, bool draft = false, bool asset = true, string? setupName = null)
    {
        var v = tag.TrimStart('v');
        var name = setupName ?? $"AutoFixScout-Setup-{v}.exe";
        var url = $"https://github.com/ShRike3601/autofix-scout/releases/download/{tag}/{name}";
        var assets = asset
            ? "[ { \"name\": \"" + name + "\", \"size\": 100, \"browser_download_url\": \"" + url + "\" },"
              + " { \"name\": \"" + name + ".sha256\", \"size\": 90, \"browser_download_url\": \"" + url + ".sha256\" } ]"
            : "[]";
        return "{ \"tag_name\": \"" + tag + "\", \"draft\": " + (draft ? "true" : "false") + ", \"prerelease\": " + (pre ? "true" : "false")
               + ", \"body\": \"notes " + tag + "\", \"assets\": " + assets + " }";
    }

    private static string List(params string[] items) => "[" + string.Join(",", items) + "]";

    // Newest first, as GitHub returns it.
    private static readonly string Sample = List(
        Rel("v0.5.0", draft: true),
        Rel("v0.4.0-beta.10", pre: true),
        Rel("v0.4.0-beta.2", pre: true),
        Rel("v0.4.0-beta.11", pre: true, asset: false),
        Rel("v0.3.1"),
        Rel("garbage-tag"));

    private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8) };

    [Fact]
    public void ListParsing_SkipsDraftsAndMissingAssets_PicksHighestIncludingPrerelease()
    {
        Assert.Equal("0.4.0-beta.10", UpdateService.ParseReleaseList(Sample)!.Version);
        Assert.Equal("0.4.0", UpdateService.ParseReleaseList(List(Rel("v0.4.0-beta.2", pre: true), Rel("v0.4.0"), Rel("v0.3.1")))!.Version);
        Assert.Equal("0.3.1", UpdateService.ParseReleaseList(List(Rel("v0.4.0-beta.2", pre: true, asset: false), Rel("v0.3.1")))!.Version);
        var u = UpdateService.ParseReleaseList(List(Rel("v0.4.0-beta.1", pre: true)))!;
        Assert.EndsWith("AutoFixScout-Setup-0.4.0-beta.1.exe", u.SetupUrl);
        Assert.EndsWith(".exe.sha256", u.Sha256Url);
        Assert.Equal(100, u.SizeBytes);
    }

    [Fact]
    public void ListParsing_ReturnsNullWhenNothingUsable()
    {
        Assert.Null(UpdateService.ParseReleaseList("[]"));
        Assert.Null(UpdateService.ParseReleaseList("not json"));
        Assert.Null(UpdateService.ParseReleaseList("{}"));
        Assert.Null(UpdateService.ParseReleaseList(List(Rel("v0.4.0", draft: true), Rel("v0.3.0", asset: false))));
        Assert.Null(UpdateService.ParseReleaseList(List(Rel("v0.4.0-beta.1", pre: true, setupName: "other.exe"))));
    }

    [Fact]
    public void StableParsingStillIgnoresPrereleases()
    {
        Assert.Null(UpdateService.ParseRelease(Rel("v0.4.0-beta.1", pre: true)));
        Assert.Null(UpdateService.ParseRelease(Sample));   // an array is not a single release
    }

    private UpdateService Service(FakeHandler h, string current, bool beta, DateTime? now = null)
    {
        new AppSettings { BetaUpdates = beta }.Save(_dir);
        var n = now ?? new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        return new UpdateService(new HttpClient(h), _dir, () => n, () => AppVersion.Parse(current)!.Value);
    }

    private static FakeHandler Server(List<string> urls) => new()
    {
        Respond = r =>
        {
            urls.Add(r.RequestUri!.ToString());
            return r.RequestUri.AbsolutePath.EndsWith("/latest") ? Json(Rel("v0.3.1")) : Json(Sample);
        },
    };

    [Fact]
    public async Task BetaOff_UsesLatestEndpointOnly()
    {
        var urls = new List<string>();
        var offer = await Service(Server(urls), "0.3.0", beta: false).CheckAsync();
        Assert.Equal("0.3.1", offer?.Version);
        Assert.Equal(new[] { UpdateService.LatestReleaseUrl }, urls);
    }

    [Fact]
    public async Task BetaOn_UsesListEndpointAndOffersPrerelease_OnStableBuild()
    {
        var urls = new List<string>();
        var offer = await Service(Server(urls), "0.3.0", beta: true).CheckAsync();
        Assert.Equal("0.4.0-beta.10", offer?.Version);
        Assert.Equal(new[] { UpdateService.ReleaseListUrl }, urls);
        Assert.Contains("per_page=10", UpdateService.ReleaseListUrl);
    }

    [Fact]
    public async Task BetaOn_OnBetaBuild_OffersOnlyNewerBeta()
    {
        Assert.Equal("0.4.0-beta.10", (await Service(Server(new()), "0.4.0-beta.2", beta: true).CheckAsync())?.Version);
        Assert.Null(await Service(Server(new()), "0.4.0-beta.10", beta: true).CheckAsync());
        Assert.Null(await Service(Server(new()), "0.4.0-beta.11", beta: true).CheckAsync());
    }

    [Fact]
    public async Task BetaOff_OnBetaBuild_GetsNextStableOnceNewer()
    {
        // 0.3.1 is older than 0.4.0-beta.1: nothing offered.
        Assert.Null(await Service(Server(new()), "0.4.0-beta.1", beta: false).CheckAsync());

        var h = new FakeHandler { Respond = _ => Json(Rel("v0.4.0")) };
        Assert.Equal("0.4.0", (await Service(h, "0.4.0-beta.1", beta: false, new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc)).CheckAsync())?.Version);
    }

    [Fact]
    public void Offer_HidesPrereleaseWhenBetaOff()
    {
        var pre = new UpdateInfo { Version = "0.4.0-beta.1" };
        var cur = AppVersion.Parse("0.3.0")!.Value;
        Assert.Null(UpdateService.Offer(pre, cur, new AppSettings { BetaUpdates = false }));
        Assert.Equal(pre, UpdateService.Offer(pre, cur, new AppSettings { BetaUpdates = true }));
    }

    [Fact]
    public async Task Throttling_Unchanged_AndChangingTheSettingChecksAgain()
    {
        var h = Server(new());
        var svc = Service(h, "0.3.0", beta: true);
        await svc.CheckAsync(); await svc.CheckAsync();
        Assert.Equal(1, h.Calls);                                                      // once per start
        await Service(h, "0.3.0", beta: true, new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc)).CheckAsync();
        Assert.Equal(1, h.Calls);                                                      // within 6 hours, same channel
        // Switching the channel off drops the cached beta at once, with one new request to /latest.
        Assert.Equal("0.3.1", (await Service(h, "0.3.0", beta: false, new DateTime(2026, 11, 14, 12, 5, 0, DateTimeKind.Utc)).CheckAsync())?.Version);
        Assert.Equal(2, h.Calls);
        await Service(h, "0.3.0", beta: false, new DateTime(2026, 11, 14, 13, 0, 0, DateTimeKind.Utc)).CheckAsync();
        Assert.Equal(2, h.Calls);
        await Service(h, "0.3.0", beta: false, new DateTime(2026, 11, 14, 19, 0, 0, DateTimeKind.Utc)).CheckAsync();
        Assert.Equal(3, h.Calls);                                                      // 6 hours later
    }

    [Fact]
    public async Task UpdatesOff_NoRequestEvenWithBeta()
    {
        new AppSettings { BetaUpdates = true, CheckForUpdates = false }.Save(_dir);
        var h = Server(new());
        var svc = new UpdateService(new HttpClient(h), _dir, () => DateTime.UtcNow, () => AppVersion.Parse("0.3.0")!.Value);
        Assert.Null(await svc.CheckAsync());
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public void BetaUpdatesDefaultsToOff() => Assert.False(new AppSettings().BetaUpdates);
}
