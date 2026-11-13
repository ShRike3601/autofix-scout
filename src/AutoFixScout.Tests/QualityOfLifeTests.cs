using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AutoFixScout.Updates;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AutoFixScout.Tests;

public sealed class FakeHandler : HttpMessageHandler
{
    public int Calls;
    public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Respond(request));
    }
}

public class VersionTests
{
    [Theory]
    [InlineData("0.3.0", "0.2.9", true)]
    [InlineData("v0.3.0", "0.3.0+abc123", false)]
    [InlineData("0.3.1", "0.3.0+abc123", true)]
    [InlineData("0.1.0", "0.0.45-dev", true)]      // any release is newer than a dev build
    [InlineData("0.0.46-dev", "0.0.45-dev", true)]
    [InlineData("1.0.0", "1.0.0-beta.1", true)]
    [InlineData("1.0.0-beta.1", "1.0.0", false)]
    [InlineData("0.10.0", "0.9.0", true)]
    public void IsNewer(string candidate, string current, bool expected) =>
        Assert.Equal(expected, AppVersion.IsNewer(AppVersion.Parse(candidate)!.Value, AppVersion.Parse(current)!.Value));

    [Theory]
    [InlineData("0.0.45-dev", true)]
    [InlineData("0.0.7", true)]
    [InlineData("0.3.0", false)]
    [InlineData("0.3.0+sha", false)]
    public void IsDev(string v, bool dev) => Assert.Equal(dev, AppVersion.Parse(v)!.Value.IsDev);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    public void BadVersionsAreRejected(string v) => Assert.Null(AppVersion.Parse(v));

    [Fact]
    public void CommitSuffixIsIgnoredAndPrereleaseKept()
    {
        Assert.Equal("0.3.0", AppVersion.Parse("0.3.0+deadbeef")!.Value.ToString());
        Assert.True(AppVersion.Parse("0.3.0-rc.1")!.Value.IsPrerelease);
    }
}

public class UpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afs-upd-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Release = """
        {
          "tag_name": "v0.3.0", "draft": false, "prerelease": false,
          "body": "## Changes\n\n* **Faster** checks\n* See [the guide](https://x.y)\n\n---\n\nTesting guide below",
          "assets": [
            { "name": "AutoFixScout-Setup-0.3.0.exe", "size": 1234, "browser_download_url": "https://github.com/ShRike3601/autofix-scout/releases/download/v0.3.0/AutoFixScout-Setup-0.3.0.exe" },
            { "name": "AutoFixScout-Setup-0.3.0.exe.sha256", "size": 90, "browser_download_url": "https://github.com/ShRike3601/autofix-scout/releases/download/v0.3.0/AutoFixScout-Setup-0.3.0.exe.sha256" }
          ]
        }
        """;

    private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };

    [Fact]
    public void ParsesGitHubRelease()
    {
        var u = UpdateService.ParseRelease(Release)!;
        Assert.Equal("0.3.0", u.Version);
        Assert.Equal(1234, u.SizeBytes);
        Assert.EndsWith("AutoFixScout-Setup-0.3.0.exe", u.SetupUrl);
        Assert.EndsWith(".exe.sha256", u.Sha256Url);
        Assert.Contains("Faster", u.Notes);
    }

    [Fact]
    public void ReleaseWithoutSetupDraftOrPrereleaseIsIgnored()
    {
        Assert.Null(UpdateService.ParseRelease(Release.Replace("AutoFixScout-Setup-0.3.0.exe\"", "other.exe\"").Replace("AutoFixScout-Setup-0.3.0.exe.sha256", "x")));
        Assert.Null(UpdateService.ParseRelease(Release.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(UpdateService.ParseRelease(Release.Replace("\"draft\": false", "\"draft\": true")));
        Assert.Null(UpdateService.ParseRelease(Release.Replace("v0.3.0", "v0.3.0-beta.1")));
        Assert.Null(UpdateService.ParseRelease("not json"));
        Assert.Null(UpdateService.ParseRelease(Release.Replace("https://github.com/", "http://evil.example/")));
    }

    [Fact]
    public void NotesBecomePlainTextAndStopAtRule()
    {
        var text = ReleaseNotes.ToPlainText(UpdateService.ParseRelease(Release)!.Notes);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("Testing guide", text);
        Assert.Contains("- Faster checks", text);
        Assert.Contains("See the guide", text);
    }

    private UpdateService Service(FakeHandler h, string current, DateTime? now = null, AppSettings? s = null)
    {
        s?.Save(_dir);
        var n = now ?? new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        return new UpdateService(new HttpClient(h), _dir, () => n, () => AppVersion.Parse(current)!.Value);
    }

    [Fact]
    public async Task OffersNewerRelease_AndThrottlesFollowingChecks()
    {
        var h = new FakeHandler { Respond = _ => Json(Release) };
        var first = await Service(h, "0.2.0").CheckAsync();
        Assert.Equal("0.3.0", first?.Version);
        Assert.Equal(1, h.Calls);

        // Same process: no second request.
        var svc = Service(h, "0.2.0", new DateTime(2026, 11, 14, 11, 0, 0, DateTimeKind.Utc));
        Assert.Equal("0.3.0", (await svc.CheckAsync())?.Version);   // from update.json
        Assert.Equal(1, h.Calls);                                     // 1 h later: within 6 h
        Assert.Equal("0.3.0", (await Service(h, "0.2.0", new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc)).CheckAsync())?.Version);
        Assert.Equal(1, h.Calls);

        // 7 h later, new start: asks again.
        var again = Service(h, "0.2.0", new DateTime(2026, 11, 14, 17, 30, 0, DateTimeKind.Utc));
        await again.CheckAsync();
        await again.CheckAsync();                                     // but only once per start
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task NoOfferWhenCurrentOrNewer_Offline_RateLimited_Or_SettingOff()
    {
        var h = new FakeHandler { Respond = _ => Json(Release) };
        Assert.Null(await Service(h, "0.3.0+abc").CheckAsync());

        File.Delete(UpdateState.FileIn(_dir));
        var offline = new FakeHandler { Respond = _ => throw new HttpRequestException("offline") };
        Assert.Null(await Service(offline, "0.2.0").CheckAsync());
        File.Delete(UpdateState.FileIn(_dir));
        var limited = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        Assert.Null(await Service(limited, "0.2.0").CheckAsync());

        File.Delete(UpdateState.FileIn(_dir));
        var off = new FakeHandler { Respond = _ => Json(Release) };
        Assert.Null(await Service(off, "0.2.0", s: new AppSettings { CheckForUpdates = false }).CheckAsync());
        Assert.Equal(0, off.Calls);
    }

    [Fact]
    public async Task DevBuildsAreOnlyOfferedWhenTheSettingAllows()
    {
        var h = new FakeHandler { Respond = _ => Json(Release) };
        Assert.Null(await Service(h, "0.0.45-dev").CheckAsync());
        var h2 = new FakeHandler { Respond = _ => Json(Release) };
        Assert.Equal("0.3.0", (await Service(h2, "0.0.45-dev", s: new AppSettings { OfferUpdatesToDevBuilds = true }).CheckAsync())?.Version);
    }

    [Fact]
    public void VerifyRejectsWrongSizeAndHash()
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, "a.exe");
        var bytes = Encoding.ASCII.GetBytes("setup-bytes");
        File.WriteAllBytes(f, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(UpdateService.Verify(f, bytes.Length, null));
        Assert.True(UpdateService.Verify(f, bytes.Length, hash.ToLowerInvariant()));
        Assert.False(UpdateService.Verify(f, bytes.Length + 1, hash));
        Assert.False(UpdateService.Verify(f, bytes.Length, new string('0', 64)));
        Assert.False(UpdateService.Verify(f, 0, null));
        Assert.False(UpdateService.Verify(Path.Combine(_dir, "missing.exe"), 5, null));
    }

    [Fact]
    public void Sha256FileIsParsed()
    {
        var h = new string('a', 64);
        Assert.Equal(h, UpdateService.ParseSha256File($"{h}  AutoFixScout-Setup-0.3.0.exe\n"));
        Assert.Null(UpdateService.ParseSha256File("garbage"));
    }

    private UpdateInfo InfoFor(byte[] bytes, string? hash, bool withSha = true) => new()
    {
        Version = "0.3.0", SizeBytes = bytes.Length,
        SetupUrl = "https://github.com/ShRike3601/autofix-scout/releases/download/v0.3.0/AutoFixScout-Setup-0.3.0.exe",
        Sha256Url = withSha ? "https://github.com/ShRike3601/autofix-scout/releases/download/v0.3.0/AutoFixScout-Setup-0.3.0.exe.sha256" : "",
    };

    private FakeHandler Server(byte[] file, string hashText) => new()
    {
        Respond = r => r.RequestUri!.AbsolutePath.EndsWith(".sha256") ? Json(hashText) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) },
    };

    [Fact]
    public async Task Download_AcceptsMatchingFile_RejectsAndDeletesMismatch()
    {
        var bytes = Encoding.ASCII.GetBytes("pretend setup");
        var good = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = await Service(Server(bytes, good + "  x.exe"), "0.2.0").DownloadAsync(InfoFor(bytes, good));
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(_dir, "updates"), Path.GetDirectoryName(path));

        // wrong hash
        var bad = await Service(Server(bytes, new string('0', 64)), "0.2.0").DownloadAsync(InfoFor(bytes, good));
        Assert.Null(bad);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "updates")));

        // wrong size
        var info = InfoFor(bytes, good); info.SizeBytes += 5;
        Assert.Null(await Service(Server(bytes, good), "0.2.0").DownloadAsync(info));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "updates")));

        // unreadable hash file counts as failure
        Assert.Null(await Service(Server(bytes, "nothing"), "0.2.0").DownloadAsync(InfoFor(bytes, good)));

        // no hash file: size alone decides
        Assert.NotNull(await Service(Server(bytes, ""), "0.2.0").DownloadAsync(InfoFor(bytes, null, withSha: false)));

        // not from github.com
        var foreign = InfoFor(bytes, good); foreign.SetupUrl = "https://example.com/a.exe";
        Assert.Null(await Service(Server(bytes, good), "0.2.0").DownloadAsync(foreign));
    }

    [Fact]
    public void SetupIsStartedSilentlyWithRelaunchAndPermissionPrompt()
    {
        var psi = UpdateService.SetupStartInfo(@"C:\x\AutoFixScout-Setup-0.3.0.exe");
        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1", psi.Arguments);
    }

    [Fact]
    public void SetupOutsideUpdatesFolderIsNotStarted()
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, "AutoFixScout-Setup-0.3.0.exe");
        File.WriteAllText(f, "x");
        Assert.False(Service(new FakeHandler(), "0.2.0").StartSetup(f));
    }

    [Fact]
    public void WhatsNew_ShowsOncePerVersion_UsingReleaseNotesWhenKnown()
    {
        var v = "0.3.0";
        var wn = new WhatsNew(_dir, () => AppVersion.Parse(v)!.Value);
        var state = new UpdateState { Latest = UpdateService.ParseRelease(Release) };
        state.Save(_dir);
        var first = wn.TakeOnce();
        Assert.NotNull(first);
        Assert.Contains("Faster checks", first!.Text);
        Assert.Null(wn.TakeOnce());
        Assert.Null(new WhatsNew(_dir, () => AppVersion.Parse("0.3.0+abc")!.Value).TakeOnce());   // same version, other commit
        v = "0.3.1";
        var next = wn.TakeOnce();                                   // new version, no notes fetched: built-in text
        Assert.NotNull(next);
        Assert.Contains("One-click updates", next!.Text);
        Assert.Null(wn.TakeOnce());
    }

    [Fact]
    public void WhatsNew_NeverForDevBuilds()
    {
        Assert.Null(new WhatsNew(_dir, () => AppVersion.Parse("0.0.9-dev")!.Value).TakeOnce());
    }

    [Fact]
    public void SettingsDefaultOnAndRoundTrip()
    {
        Assert.True(AppSettings.Load(_dir).CheckForUpdates);
        Assert.False(AppSettings.Load(_dir).OfferUpdatesToDevBuilds);
        new AppSettings { CheckForUpdates = false }.Save(_dir);
        Assert.False(AppSettings.Load(_dir).CheckForUpdates);
    }
}

public class ChangeHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afs-hist-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "session.db");
    private string SavePoints => Path.Combine(_dir, "savepoints");
    private const string Sp = "0123456789abcdef0123456789abcdef";

    public ChangeHistoryTests() => Directory.CreateDirectory(SavePoints);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static StepLog Step(string action, string detail, DateTime when, StepResult r = StepResult.Success, bool approved = true) => new()
    {
        PlaybookStep = action, ActionId = action, Tier = 1, ProposedBy = "playbook", Approved = approved,
        ApprovedAt = when, StartedAt = when, FinishedAt = when, Result = r, Detail = detail,
    };

    private void Seed(params StepLog[] steps)
    {
        using var log = new SessionLog(Db);
        var id = log.Start("audio");
        log.AddSteps(id, steps);
    }

    [Fact]
    public void ListsNewestFirst_OnlyApprovedChanges_WithUndoOnlyWhereAPathExists()
    {
        var t = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.Combine(SavePoints, Sp));
        Seed(
            Step("service.restart", "executor exit code 0", t),
            Step("driver.rollback", "rolled back; save point " + Sp, t.AddHours(1)),
            Step("startup.disable", "startup entry 'Steam' disabled in StartupApproved\\Run; before: no value (enabled)", t.AddHours(2)),
            Step("ui.open_settings_page", "ms-settings:sound", t.AddHours(3)),
            Step("audio.play_test_tone", "x", t.AddHours(3)),
            Step("service.set_manual", "declined", t.AddHours(4), approved: false),
            Step("network.flush_dns", "failed", t.AddHours(5), StepResult.Failed));

        var list = new ChangeHistory(Db, SavePoints).List();
        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { t.AddHours(2), t.AddHours(1), t }, list.Select(e => e.WhenUtc));
        Assert.True(list[0].CanUndo);
        Assert.True(list[1].CanUndo);
        Assert.False(list[2].CanUndo);
        Assert.NotNull(list[2].Hint);
        Assert.Null(list[1].Hint);
    }

    [Fact]
    public void DriverUndoNeedsTheSavePointToStillExist()
    {
        Seed(Step("driver.install_from_windows_update", "installed the Windows Update driver; save point " + Sp, DateTime.UtcNow));
        var e = new ChangeHistory(Db, SavePoints).List().Single();
        Assert.False(e.CanUndo);
        Assert.Contains("Roll Back Driver", e.Hint);
    }

    [Fact]
    public async Task Undo_RunsTheUndoPathOnce_AndMarksTheChange()
    {
        Directory.CreateDirectory(Path.Combine(SavePoints, Sp));
        Seed(Step("driver.rollback", "rolled back; save point " + Sp, DateTime.UtcNow));
        var calls = new List<(string, string)>();
        var h = new ChangeHistory(Db, SavePoints, (k, p) => { calls.Add((k, p)); return Task.FromResult((true, "ok")); });
        var entry = h.List().Single();

        var r = await h.UndoAsync(entry.StepId);
        Assert.True(r.Ok);
        Assert.Equal(new[] { ("driver", Sp) }, calls);
        var after = h.List().Single();
        Assert.True(after.Undone);
        Assert.False(after.CanUndo);

        Assert.False((await h.UndoAsync(entry.StepId)).Ok);   // not twice
        Assert.Single(calls);
    }

    [Fact]
    public async Task FailedUndoLeavesTheChangeUndoable_AndNonUndoableIsRefused()
    {
        Directory.CreateDirectory(Path.Combine(SavePoints, Sp));
        Seed(Step("driver.rollback", "rolled back; save point " + Sp, DateTime.UtcNow.AddMinutes(1)),
             Step("service.restart", "executor exit code 0", DateTime.UtcNow));
        var h = new ChangeHistory(Db, SavePoints, (k, p) => Task.FromResult((false, "declined")));
        var list = h.List();
        Assert.False((await h.UndoAsync(list[0].StepId)).Ok);
        Assert.True(h.List()[0].CanUndo);
        Assert.False((await h.UndoAsync(list[1].StepId)).Ok);
    }

    [Fact]
    public void OldDatabaseWithoutDetailColumnIsMigrated()
    {
        using (var c = new SqliteConnection($"Data Source={Db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE step_log (step_id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, playbook_step TEXT NOT NULL, action_id TEXT NOT NULL, tier INTEGER NOT NULL, proposed_by TEXT NOT NULL, approved INTEGER NOT NULL DEFAULT 0, approved_at TEXT, started_at TEXT, finished_at TEXT, result TEXT, user_confirmed_fixed INTEGER, error TEXT);";
            cmd.ExecuteNonQuery();
        }
        Seed(Step("service.restart", "executor exit code 0", DateTime.UtcNow));
        Assert.Single(new ChangeHistory(Db, SavePoints).List());
    }

    [Fact]
    public void UndoFor_RecognisesOnlyRealUndoPaths()
    {
        Assert.Equal(("driver", Sp), ChangeHistory.UndoFor("driver.rollback", "rolled back; save point " + Sp));
        Assert.Equal(("startup", "Run|My App"), ChangeHistory.UndoFor("startup.disable", "startup entry 'My App' disabled in StartupApproved\\Run; before: x"));
        Assert.Null(ChangeHistory.UndoFor("service.restart", "executor exit code 0"));
        Assert.Null(ChangeHistory.UndoFor("driver.rollback", "no previous driver"));
    }
}

/// <summary>docs/WRITING.md for the new strings: no contractions, no "I" or "we", no exclamation marks.</summary>
public class QolWritingTests
{
    private static readonly Regex Bad = new(@"n't\b|'ll\b|'re\b|'ve\b|'m\b|'d\b|\b(it|that|there|here|what|he|she)'s\b|\bI\b|\bwe\b|\bour\b|!", RegexOptions.IgnoreCase);

    private static string Strip(string s) => s.Replace("AutoFix Scout's", "AutoFix Scout");

    [Fact]
    public void ChangeHistoryStrings()
    {
        var ids = new[]
        {
            "audio.unmute_and_set_volume", "audio.unmute_microphone", "audio.unmute_app", "audio.ducking_off", "service.restart",
            "service.set_manual", "service.restore_default", "device.disable_enable", "device.enable", "device.rescan", "driver.rollback",
            "driver.install_from_windows_update", "network.reset_dns", "network.renew_address", "network.flush_dns",
            "network.reset_winhttp_proxy", "network.reset_user_proxy", "network.clean_hosts", "network.reset_winsock",
            "network.wifi_stability", "display.extend", "display.set_brightness", "printer.clear_queue", "printer.set_online",
            "repair.dism", "repair.sfc", "time.resync", "system.repair_path", "system.repair_temp", "system.pagefile_auto",
            "update.reset_components", "search.rebuild_index", "runtime.install_vcredist", "storage.cleanup_components",
            "storage.clear_user_temp", "storage.empty_recycle_bin", "office.quick_repair", "apps.reregister", "apps.reset_store_cache",
            "apps.start_onedrive", "apps.reset_onedrive", "power.balanced_plan", "shell.notifications_on", "shell.restart_start_menu",
            "shell.restart_explorer", "shell.rebuild_icon_cache", "startup.disable", "security.update_signatures", "security.firewall_on",
            "security.uac_on", "security.remove_browser_policies", "security.remove_browser_policies_user", "input.touchpad_on",
            "input.accessibility_keys_off", "something.unknown",
        };
        foreach (var id in ids)
            foreach (var s in new[] { ChangeHistory.Describe(id, null), ChangeHistory.HintFor(id) })
            {
                Assert.DoesNotMatch(Bad, Strip(s));
                Assert.EndsWith(".", s);
            }
    }

    [Fact]
    public void WhatsNewBuiltInText()
    {
        var text = WhatsNew.BuiltIn();
        Assert.NotEmpty(text);
        Assert.DoesNotMatch(Bad, text);
    }

    [Fact]
    public async Task UndoMessages()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afs-w-" + Guid.NewGuid().ToString("N"));
        try
        {
            var h = new ChangeHistory(Path.Combine(dir, "s.db"), dir);
            var r = await h.UndoAsync(42);
            Assert.DoesNotMatch(Bad, r.Message);
            Assert.DoesNotMatch(Bad, "The change has been undone. The change was not undone. The Windows permission prompt may have been declined, or the previous driver could not be restored.");
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
