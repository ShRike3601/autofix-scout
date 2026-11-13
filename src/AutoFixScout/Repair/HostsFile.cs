using System.Net;

namespace AutoFixScout.Repair;

/// <summary>
/// The hosts file can redirect or block web sites. Malware uses it to send banking or update sites elsewhere, or to
/// block security software. Shared with the elevated executor: the executor works out the same lines itself and never
/// takes lines, hosts or paths as input. Lines are commented out (with a marker), never deleted, and the file is backed up.
/// </summary>
internal static class HostsFile
{
    public const string Marker = "# disabled by AutoFix Scout: ";

    public sealed record Entry(int Line, string Ip, string Host, bool Redirect);

    // Sites a normal hosts file has no reason to redirect. A suffix match: "login.microsoftonline.com" matches.
    private static readonly string[] WellKnown =
    {
        "microsoft.com", "microsoftonline.com", "windowsupdate.com", "windows.com", "windows.net", "live.com", "office.com",
        "office.net", "bing.com", "msn.com", "skype.com", "xbox.com", "google.com", "googleapis.com", "gstatic.com",
        "youtube.com", "gmail.com", "facebook.com", "instagram.com", "whatsapp.com", "whatsapp.net", "apple.com",
        "icloud.com", "amazon.com", "paypal.com", "github.com", "cloudflare.com", "mozilla.org", "mozilla.net",
        "wikipedia.org", "zoom.us", "netflix.com", "spotify.com", "steampowered.com", "discord.com",
        "avast.com", "avg.com", "kaspersky.com", "mcafee.com", "norton.com", "nortonlifelock.com", "eset.com",
        "malwarebytes.com", "bitdefender.com", "sophos.com", "trendmicro.com", "f-secure.com", "virustotal.com",
    };

    // Blocking telemetry or ads is a deliberate choice people make; never flag those.
    private static readonly string[] DeliberateBlocks = { "telemetry", "vortex", "watson", "settings-win", "adservice", "doubleclick", "analytics" };

    // Update and security sites: blocking them (127.0.0.1 / 0.0.0.0) is a classic malware move.
    private static readonly string[] MustNotBlock =
    {
        "windowsupdate.com", "update.microsoft.com", "download.microsoft.com", "go.microsoft.com", "wdcp.microsoft.com",
        "avast.com", "avg.com", "kaspersky.com", "mcafee.com", "norton.com", "eset.com", "malwarebytes.com",
        "bitdefender.com", "sophos.com", "trendmicro.com", "f-secure.com", "virustotal.com",
    };

    public static IReadOnlyList<Entry> Parse(IEnumerable<string> lines)
    {
        var list = new List<Entry>();
        var i = 0;
        foreach (var raw in lines)
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out var ip))
                foreach (var host in parts.Skip(1))
                    list.Add(new Entry(i, parts[0], host.ToLowerInvariant(), !IsBlockAddress(ip)));
            i++;
        }
        return list;
    }

    private static bool IsBlockAddress(IPAddress ip) =>
        IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);

    private static bool Matches(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>Entries that redirect a well-known site to another address, or block an update or security site.</summary>
    public static IReadOnlyList<Entry> Suspicious(IEnumerable<string> lines) => Parse(lines).Where(e =>
        !DeliberateBlocks.Any(d => e.Host.Contains(d, StringComparison.Ordinal)) &&
        (e.Redirect ? WellKnown.Any(d => Matches(e.Host, d)) : MustNotBlock.Any(d => Matches(e.Host, d)))).ToList();

    /// <summary>The file with every suspicious line commented out. Same line count; other lines untouched.</summary>
    public static string[] Clean(string[] lines, DateTime nowUtc)
    {
        var bad = Suspicious(lines).Select(e => e.Line).ToHashSet();
        return lines.Select((l, i) => bad.Contains(i) ? $"{Marker}{nowUtc:yyyy-MM-dd} {l}" : l).ToArray();
    }
}
