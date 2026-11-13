using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoFixScout.Updates;

/// <summary>A newer release that can be installed. Plain data, also stored in update.json.</summary>
public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    /// <summary>The GitHub release text (Markdown). Use <see cref="ReleaseNotes.ToPlainText"/> before showing it.</summary>
    public string Notes { get; set; } = "";
    public string SetupUrl { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>Url of AutoFixScout-Setup-&lt;version&gt;.exe.sha256, or empty when the release has none.</summary>
    public string Sha256Url { get; set; } = "";
}

/// <summary>
/// Checks GitHub for a newer release and installs it when the person clicks "Update". Nothing here ever throws to the
/// caller and nothing is shown on failure: offline, rate limit, no setup file all mean "no update to offer".
/// The only data sent is the request itself (User-Agent "AutoFixScout"); no version, name or computer details.
/// </summary>
public sealed class UpdateService
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/ShRike3601/autofix-scout/releases/latest";
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Arguments the setup is started with. /RELAUNCH=1 makes the installer start the app again afterwards.</summary>
    public const string SetupArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1";

    private static readonly Lazy<HttpClient> DefaultClient = new(() =>
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AutoFixScout");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    });

    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly Func<DateTime> _now;
    private readonly Func<AppVersion> _current;
    private bool _checkedThisRun;

    /// <param name="http">Tests pass a client with a fake handler; the app passes nothing.</param>
    /// <param name="dataDir">Default %LOCALAPPDATA%\AutoFixScout.</param>
    public UpdateService(HttpClient? http = null, string? dataDir = null, Func<DateTime>? utcNow = null, Func<AppVersion>? currentVersion = null)
    {
        _http = http ?? DefaultClient.Value;
        _dir = dataDir ?? AppPaths.DataDir;
        _now = utcNow ?? (() => DateTime.UtcNow);
        _current = currentVersion ?? AppVersion.Current;
    }

    public string UpdatesDir => Path.Combine(_dir, "updates");

    // ---- check ----

    /// <summary>
    /// The update to offer, or null. Contacts GitHub at most once per app start and not within 6 hours of the last
    /// successful check (in between, the result of that check is reused, so a waiting update stays visible).
    /// Returns null at once when "Check for updates" is off.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var settings = AppSettings.Load(_dir);
            if (!settings.CheckForUpdates) return null;
            var state = UpdateState.Load(_dir);
            var due = !_checkedThisRun && (state.LastCheckUtc is null || _now() - state.LastCheckUtc.Value >= MinInterval
                                           || state.LastCheckUtc.Value > _now());   // a clock set back counts as due
            if (due)
            {
                _checkedThisRun = true;
                var fetched = await FetchLatestAsync(ct);
                if (fetched is not null)
                {
                    state.Latest = fetched.Value.Info;   // null: no usable release
                    state.LastCheckUtc = _now();
                    state.Save(_dir);
                }
            }
            return Offer(state.Latest, _current(), settings);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Whether a found release should be offered to a running build.</summary>
    public static UpdateInfo? Offer(UpdateInfo? latest, AppVersion current, AppSettings settings)
    {
        if (latest is null || !AppVersion.TryParse(latest.Version, out var v) || v.IsPrerelease) return null;
        if (current.IsDev && !settings.OfferUpdatesToDevBuilds) return null;
        return AppVersion.IsNewer(v, current) ? latest : null;
    }

    private readonly record struct Fetched(UpdateInfo? Info);

    private async Task<Fetched?> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ApiTimeout);
            using var resp = await _http.GetAsync(LatestReleaseUrl, cts.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new Fetched(null);   // no release yet
            if (!resp.IsSuccessStatusCode) return null;                                  // rate limit (403/429), outage
            return new Fetched(ParseRelease(await resp.Content.ReadAsStringAsync(cts.Token)));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Reads a GitHub "latest release" response. Null for a draft, a prerelease, a bad tag or no Setup asset.</summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
            var tag = Str(root, "tag_name");
            if (!AppVersion.TryParse(tag, out var v) || v.IsPrerelease) return null;
            var version = v.ToString();
            var setupName = $"AutoFixScout-Setup-{version}.exe";
            string setupUrl = "", shaUrl = ""; long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name"); var url = Str(a, "browser_download_url");
                    if (name.Equals(setupName, StringComparison.OrdinalIgnoreCase) && IsGitHubUrl(url))
                    {
                        setupUrl = url;
                        size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    }
                    else if (name.Equals(setupName + ".sha256", StringComparison.OrdinalIgnoreCase) && IsGitHubUrl(url))
                        shaUrl = url;
                }
            if (setupUrl.Length == 0) return null;
            return new UpdateInfo { Version = version, Notes = Str(root, "body"), SetupUrl = setupUrl, SizeBytes = size, Sha256Url = shaUrl };
        }
        catch (Exception) { return null; }

        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    }

    /// <summary>Downloads only come from github.com over https.</summary>
    public static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    // ---- download and verify ----

    /// <summary>
    /// Downloads the setup (only after the person clicked Update) into updates\ and verifies it. Returns the path of a
    /// verified file, or null (the bad file is deleted). progress receives 0..1.
    /// </summary>
    public async Task<string?> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string? target = null;
        try
        {
            if (!IsGitHubUrl(update.SetupUrl) || !AppVersion.TryParse(update.Version, out var v)) return null;
            string? expectedHash = null;
            if (update.Sha256Url.Length > 0)
            {
                if (!IsGitHubUrl(update.Sha256Url)) return null;
                expectedHash = ParseSha256File(await _http.GetStringAsync(update.Sha256Url, ct));
                if (expectedHash is null) return null;   // a hash file that cannot be read is a failed check, not a skipped one
            }
            Directory.CreateDirectory(UpdatesDir);
            CleanOldDownloads();
            // The file name is built here, never taken from the web.
            target = Path.Combine(UpdatesDir, $"AutoFixScout-Setup-{v}.exe");
            using (var resp = await _http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode) return null;
                var total = resp.Content.Headers.ContentLength ?? update.SizeBytes;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[81920]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }
            if (Verify(target, update.SizeBytes, expectedHash)) return target;
            File.Delete(target);
            return null;
        }
        catch (Exception)
        {
            try { if (target is not null) File.Delete(target); } catch (Exception) { }
            return null;
        }
    }

    /// <summary>The file must have the size GitHub lists and, when the release has a .sha256 file, that SHA-256.</summary>
    public static bool Verify(string path, long expectedSize, string? expectedSha256)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || expectedSize <= 0 || info.Length != expectedSize) return false;
            if (expectedSha256 is null) return true;
            using var fs = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(fs));
            return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>First 64-hex-digit word of a ".sha256" file ("hash  name" or just "hash"); null if there is none.</summary>
    public static string? ParseSha256File(string text)
    {
        var m = Regex.Match(text ?? "", @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])");
        return m.Success ? m.Value : null;
    }

    private void CleanOldDownloads()
    {
        try { foreach (var f in Directory.GetFiles(UpdatesDir)) File.Delete(f); }
        catch (Exception) { }
    }

    // ---- install ----

    /// <summary>How the verified setup is started: silent, Windows asks for permission once (UAC), the app starts again afterwards.</summary>
    public static ProcessStartInfo SetupStartInfo(string setupPath) => new(setupPath, SetupArguments)
    {
        UseShellExecute = true,
        Verb = "runas",
    };

    /// <summary>
    /// Starts the downloaded setup. Only a file inside updates\ named like the setup is run. False when the person
    /// declined the Windows permission prompt or the file is not acceptable; on true the caller must close the app.
    /// </summary>
    public bool StartSetup(string setupPath)
    {
        try
        {
            var full = Path.GetFullPath(setupPath);
            var inUpdates = string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(UpdatesDir), StringComparison.OrdinalIgnoreCase);
            if (!inUpdates || !Regex.IsMatch(Path.GetFileName(full), @"^AutoFixScout-Setup-[0-9][0-9A-Za-z.\-]*\.exe$") || !File.Exists(full)) return false;
            using var p = Process.Start(SetupStartInfo(full));
            return p is not null;
        }
        catch (Exception) { return false; }   // includes the declined permission prompt (error 1223)
    }
}
