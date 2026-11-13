using System.Xml.Linq;

namespace AutoFixScout.Watching;

/// <summary>Command lines and output parsing for netsh, with no process calls. Profile names stay in memory.</summary>
public static class WifiNetsh
{
    private static bool Unsafe(string profile) =>
        string.IsNullOrWhiteSpace(profile) || profile.Length > 64 || profile.Any(c => c == '"' || char.IsControl(c));

    /// <summary>The arguments of "netsh wlan connect name=..." or null when the profile name is not safe to pass.</summary>
    public static string? ConnectArguments(string profile) =>
        Unsafe(profile) ? null : $"wlan connect name=\"{profile}\"";

    public static string? ShowProfileArguments(string profile) =>
        Unsafe(profile) ? null : $"wlan show profile name=\"{profile}\"";

    /// <summary>True when "Connection mode" of "netsh wlan show profile" says the profile connects automatically (English Windows; anything else fails closed).</summary>
    public static bool IsAutoConnect(string showProfileOutput)
    {
        foreach (var line in showProfileOutput.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("Connection mode", StringComparison.OrdinalIgnoreCase)) continue;
            var value = t[(t.IndexOf(':') + 1)..].Trim();
            return value.Equals("Connect automatically", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>State and profile from "netsh wlan show interfaces". The profile is null unless connected.</summary>
    public static (bool Connected, string? Profile) ParseInterfaces(string output)
    {
        string? state = null, profile = null;
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            var i = t.IndexOf(':');
            if (i < 0) continue;
            var key = t[..i].Trim();
            var value = t[(i + 1)..].Trim();
            if (key.Equals("State", StringComparison.OrdinalIgnoreCase) && state == null) state = value;
            else if (key.Equals("Profile", StringComparison.OrdinalIgnoreCase) && profile == null) profile = value;
        }
        var connected = string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase);
        return (connected, connected && !string.IsNullOrWhiteSpace(profile) ? profile : null);
    }

    /// <summary>The ProfileName of a WLAN-AutoConfig event, kept in memory to know what to reconnect. Null when absent.</summary>
    public static string? ParseProfileName(string eventXml)
    {
        var doc = XDocument.Parse(eventXml);
        var ns = doc.Root!.Name.Namespace;
        var v = doc.Root.Element(ns + "EventData")?.Elements(ns + "Data")
            .FirstOrDefault(d => (string?)d.Attribute("Name") == "ProfileName")?.Value;
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
