using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AutoFixScout.Repair;

/// <summary>Where a start page, search engine or similar setting was found.</summary>
internal sealed record HijackHost(string Browser, string Setting, string Host);

/// <summary>An extension forced by policy.</summary>
internal sealed record ForcedExtension(string Browser, string Id);

/// <summary>An extension added through the registry (HKLM or HKCU \Software\{Google\Chrome|Microsoft\Edge}\Extensions\id). <see cref="UpdateHost"/> is the host of update_url only.</summary>
internal sealed record ExternalExtension(string Browser, string Id, string? UpdateHost);

/// <summary>Everything the browser hijack scan found. Hosts only, never a path or a query.</summary>
internal sealed class BrowserHijackState
{
    public bool Managed { get; init; }
    public IReadOnlyList<HijackHost> SearchHosts { get; init; } = Array.Empty<HijackHost>();
    public IReadOnlyList<HijackHost> StartHosts { get; init; } = Array.Empty<HijackHost>();
    public IReadOnlyList<ForcedExtension> Forced { get; init; } = Array.Empty<ForcedExtension>();
    /// <summary>Hosts named by ExtensionInstallSources (extension install allowed from there).</summary>
    public IReadOnlyList<HijackHost> InstallSources { get; init; } = Array.Empty<HijackHost>();
    public IReadOnlyList<ExternalExtension> External { get; init; } = Array.Empty<ExternalExtension>();
}

/// <summary>Read-only registry access of the scan, replaceable in tests. <c>hive</c> is "HKLM" or "HKCU", <c>browser</c> is "Chrome" or "Edge".</summary>
internal interface IBrowserRegistry
{
    /// <summary>The values directly under the policy key, as text (numbers as decimal), or an empty dictionary.</summary>
    IReadOnlyDictionary<string, string> PolicyValues(string hive, string browser);
    /// <summary>The values of a policy subkey such as ExtensionInstallForcelist or RestoreOnStartupURLs.</summary>
    IReadOnlyList<string> PolicyList(string hive, string browser, string subkey);
    /// <summary>The ids under ...\Extensions and their update_url value (null when missing). The path value is never read.</summary>
    IReadOnlyList<(string Id, string? UpdateUrl)> ExternalExtensions(string hive, string browser);
}

/// <summary>
/// Finds browser hijacks that the generic policy check does not describe: which web site the search engine and start
/// pages point to, which extensions are forced, and which extensions were added through the registry. Pure rules plus
/// a registry reader; nothing is changed. Privacy: only hosts and extension ids ever leave this class.
/// </summary>
internal static class BrowserHijackScan
{
    public static readonly string[] Browsers = { "Chrome", "Edge" };
    public static readonly string[] Hives = { "HKLM", "HKCU" };

    /// <summary>
    /// Hosts that are never flagged. Entries ending in a dot match the name followed by a country suffix (google.com,
    /// google.co.uk). The last group are start pages that Windows and the browsers use themselves.
    /// </summary>
    public static readonly string[] WellKnownHosts =
    {
        "google.", "bing.com", "duckduckgo.com", "yahoo.", "ecosia.org", "startpage.com", "brave.com", "yandex.",
        "msn.com", "microsoft.com", "live.com", "office.com", "microsoftonline.com",
    };

    /// <summary>
    /// Extension ids of Google apps that a browser or an installer may add on its own. Kept short on purpose: an id here is
    /// never reported. No Microsoft Edge id is listed because none is known with certainty.
    /// </summary>
    public static readonly HashSet<string> FirstPartyIds = new(StringComparer.Ordinal)
    {
        "ghbmnnjooekpmoecnnnilnnbdlolhkhi", // Google Docs Offline
        "nmmhkkegccagdldgiimedpiccmgmieda", // Chrome Web Store Payments
        "aapocclcgogkmnckokdopfmhonfmgoek", // Google Slides
        "aohghmighlieiainnegkcijnilnnccgp", // Google Docs
        "felcaaldnbdncclmgdcncolpebgiejap", // Google Sheets
        "apdfllckaahabafndbhieahigkjlhalf", // Google Drive
    };

    private static readonly Regex IdPattern = new("^[a-p]{32}$", RegexOptions.Compiled);

    /// <summary>A Chromium extension id: exactly 32 lowercase letters, a to p (the alphabet the browsers use for ids).</summary>
    public static bool IsValidExtensionId(string? id) => id is not null && IdPattern.IsMatch(id);

    /// <summary>Valid and not on the first-party allow-list.</summary>
    public static bool IsReportable(string id) => IsValidExtensionId(id) && !FirstPartyIds.Contains(id);

    /// <summary>
    /// The host of a web address, in ASCII (punycode) form so that look-alike letters are visible. No scheme, port, user,
    /// path or query. Addresses without a scheme ("example.net/x") and patterns ("https://*.example.net/*") are accepted.
    /// Returns null for anything else (about:, chrome://, edge://, file:, javascript:, templates, nonsense).
    /// </summary>
    public static string? ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var t = url.Trim();
        if (t.Length > 2048) return null;
        var scheme = Regex.Match(t, @"^([A-Za-z][A-Za-z0-9+.\-]*|\*)://");
        if (scheme.Success)
        {
            var s = scheme.Groups[1].Value.ToLowerInvariant();
            if (s is not ("http" or "https" or "*")) return null;
            t = t[scheme.Length..];
        }
        else if (Regex.IsMatch(t, @"^[A-Za-z][A-Za-z0-9+.\-]*:(?!\d+([/?#]|$))")) return null; // about:blank, javascript:, data:
        var end = t.IndexOfAny(new[] { '/', '?', '#', '\\' });
        var auth = end < 0 ? t : t[..end];
        var at = auth.LastIndexOf('@');
        if (at >= 0) auth = auth[(at + 1)..];
        if (auth.StartsWith('[')) { var close = auth.IndexOf(']'); return close > 0 ? auth[1..close].ToLowerInvariant() : null; }
        var colon = auth.IndexOf(':');
        if (colon >= 0) auth = auth[..colon];
        auth = auth.TrimEnd('.');
        while (auth.StartsWith("*.")) auth = auth[2..];
        if (auth.Length == 0 || auth.Contains('*') || auth.Contains('{') || auth.Contains(' ')) return null;
        if (!auth.Contains('.') && !auth.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var ascii = new IdnMapping().GetAscii(auth).ToLowerInvariant();
            return Regex.IsMatch(ascii, @"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)*$") ? ascii : null;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Is the host one of the well-known providers? A name ending in a dot must be followed by a country suffix, so "google.evil-site.net" does not match.</summary>
    public static bool IsWellKnownHost(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var entry in WellKnownHosts)
        {
            if (entry.EndsWith('.'))
            {
                var name = Regex.Escape(entry[..^1]);
                if (Regex.IsMatch(host, $@"(^|\.){name}\.([a-z]{{2,3}}|(co|com|org|net)\.[a-z]{{2}})$")) return true;
            }
            else if (host == entry || host.EndsWith("." + entry, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>What a person may read for a host: an IP address is not shown.</summary>
    public static string Shown(string host) =>
        System.Net.IPAddress.TryParse(host, out _) ? "an IP address" : host;

    /// <summary>"id;update-url" of a forced-extension entry becomes the id, or null when it is not a valid id.</summary>
    public static string? ForcedId(string? entry)
    {
        if (entry is null) return null;
        var id = entry.Split(';')[0].Trim();
        return IsValidExtensionId(id) ? id : null;
    }

    /// <summary>Is this computer managed by an organization (domain, Azure AD / Entra, or enrolled in Intune)? Read only.</summary>
    public static bool IsManagedDevice()
    {
        if (BrowserPolicies.IsManagedPc()) return true;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
            if (k is null) return false;
            // Enrollments are GUID-named subkeys; "Context", "Ownership" and similar are not enrollments.
            return k.GetSubKeyNames().Any(n => Guid.TryParse(n, out _));
        }
        catch (Exception) { return false; }
    }

    private static readonly (string Value, string Setting)[] SingleStartValues =
    {
        ("HomepageLocation", "start page"),
        ("NewTabPageLocation", "new tab page"),
    };

    /// <summary>Pure analysis of what the reader returns.</summary>
    public static BrowserHijackState Scan(IBrowserRegistry reg, bool managed)
    {
        var search = new List<HijackHost>();
        var start = new List<HijackHost>();
        var forced = new List<ForcedExtension>();
        var sources = new List<HijackHost>();
        var external = new List<ExternalExtension>();
        foreach (var browser in Browsers)
            foreach (var hive in Hives)
            {
                var values = reg.PolicyValues(hive, browser);
                if (values.TryGetValue("DefaultSearchProviderSearchURL", out var searchUrl)
                    && ExtractHost(searchUrl) is { } sh && !IsWellKnownHost(sh))
                    search.Add(new HijackHost(browser, "search engine", sh));
                foreach (var (name, setting) in SingleStartValues)
                    if (values.TryGetValue(name, out var url) && ExtractHost(url) is { } h && !IsWellKnownHost(h))
                        start.Add(new HijackHost(browser, setting, h));
                foreach (var url in reg.PolicyList(hive, browser, "RestoreOnStartupURLs"))
                    if (ExtractHost(url) is { } h && !IsWellKnownHost(h))
                        start.Add(new HijackHost(browser, "start-up page", h));
                foreach (var entry in reg.PolicyList(hive, browser, "ExtensionInstallForcelist"))
                    if (ForcedId(entry) is { } id && IsReportable(id)) forced.Add(new ForcedExtension(browser, id));
                foreach (var src in reg.PolicyList(hive, browser, "ExtensionInstallSources"))
                    if (ExtractHost(src) is { } h) sources.Add(new HijackHost(browser, "extension source", h));
                foreach (var (id, upd) in reg.ExternalExtensions(hive, browser))
                    if (IsReportable(id)) external.Add(new ExternalExtension(browser, id, upd is null ? null : ExtractHost(upd)));
            }
        return new BrowserHijackState
        {
            Managed = managed,
            SearchHosts = search.DistinctBy(x => (x.Browser, x.Setting, x.Host)).ToList(),
            StartHosts = start.DistinctBy(x => (x.Browser, x.Setting, x.Host)).ToList(),
            Forced = forced.DistinctBy(x => (x.Browser, x.Id)).ToList(),
            InstallSources = sources.DistinctBy(x => (x.Browser, x.Host)).ToList(),
            External = external.DistinctBy(x => (x.Browser, x.Id)).ToList(),
        };
    }

    /// <summary>The scan of the real registry.</summary>
    public static BrowserHijackState ScanRegistry() => Scan(new RegistryBrowserReader(), IsManagedDevice());

    internal static string BrowserName(string browser) => browser == "Edge" ? "Microsoft Edge" : "Google Chrome";
}

/// <summary>The real reader. Opens keys read-only; every failure reads as "nothing there".</summary>
internal sealed class RegistryBrowserReader : IBrowserRegistry
{
    private static string PolicyPath(string browser) => browser == "Edge" ? @"Software\Policies\Microsoft\Edge" : @"Software\Policies\Google\Chrome";
    private static string ExtPath(string browser) => browser == "Edge" ? @"Software\Microsoft\Edge\Extensions" : @"Software\Google\Chrome\Extensions";

    private static List<RegistryKey> Open(string hive, string path)
    {
        var keys = new List<RegistryKey>();
        try
        {
            if (hive == "HKCU")
            {
                if (Registry.CurrentUser.OpenSubKey(path) is { } k) keys.Add(k);
                return keys;
            }
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var p in new[] { path, @"Software\WOW6432Node" + path["Software".Length..] })
                if (root.OpenSubKey(p) is { } k) keys.Add(k);
        }
        catch (Exception) { }
        return keys;
    }

    public IReadOnlyDictionary<string, string> PolicyValues(string hive, string browser)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Open(hive, PolicyPath(browser)))
            using (k)
                try
                {
                    foreach (var n in k.GetValueNames())
                        if (k.GetValue(n) is { } v && !d.ContainsKey(n)) d[n] = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                }
                catch (Exception) { }
        return d;
    }

    public IReadOnlyList<string> PolicyList(string hive, string browser, string subkey)
    {
        var l = new List<string>();
        foreach (var k in Open(hive, PolicyPath(browser)))
            using (k)
                try
                {
                    using var s = k.OpenSubKey(subkey);
                    if (s is null) continue;
                    foreach (var n in s.GetValueNames())
                        if (s.GetValue(n) is string v) l.Add(v);
                }
                catch (Exception) { }
        return l;
    }

    public IReadOnlyList<(string Id, string? UpdateUrl)> ExternalExtensions(string hive, string browser)
    {
        var l = new List<(string, string?)>();
        foreach (var k in Open(hive, ExtPath(browser)))
            using (k)
                try
                {
                    foreach (var id in k.GetSubKeyNames())
                    {
                        using var e = k.OpenSubKey(id);
                        l.Add((id, e?.GetValue("update_url") as string));
                    }
                }
                catch (Exception) { }
        return l;
    }
}
