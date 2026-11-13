using System.Management;
using System.Text;
using Microsoft.Win32;

namespace AutoFixScout.Repair;

/// <summary>
/// Browser policies that program-made hijacks use: forced extensions, home page, start-up pages, new tab page, search
/// engine and proxy. A home computer normally has none. Shared with the elevated executor: the executor computes the
/// list itself and never takes names, keys or paths as input. Only the listed values are removed, never whole keys,
/// except an <c>ExtensionInstallForcelist</c> subkey that the removal has emptied.
/// </summary>
internal static class BrowserPolicies
{
    /// <summary>One flagged value. <paramref name="Display"/> is what the person may see: the value name, or the extension id for a forced extension.</summary>
    public sealed record Flag(string Hive, string Browser, string KeyPath, string ValueName, string Display);

    public const string HiveMachine = "HKLM";
    public const string HiveUser = "HKCU";
    public const string ForcelistKey = "ExtensionInstallForcelist";

    private static readonly (string Browser, string Path, bool Firefox)[] Roots =
    {
        ("Chrome", @"Software\Policies\Google\Chrome", false),
        ("Edge", @"Software\Policies\Microsoft\Edge", false),
        ("Firefox", @"Software\Policies\Mozilla\Firefox", true),
    };

    /// <summary>Values directly under the Chrome or Edge policy key.</summary>
    public static readonly string[] ChromiumValues =
    {
        "HomepageLocation", "NewTabPageLocation", "DefaultSearchProviderEnabled", "DefaultSearchProviderSearchURL", "DefaultSearchProviderName",
        "ProxyServer", "ProxyMode", "ProxyPacUrl", "RestoreOnStartupURLs", "ExtensionInstallForcelist",
    };

    /// <summary>Subkeys of the Chrome or Edge policy key whose values are all flagged.</summary>
    public static readonly string[] ChromiumSubkeys = { ForcelistKey, "RestoreOnStartupURLs" };

    /// <summary>Subkeys of the Firefox policy key that are flagged with everything below them.</summary>
    public static readonly string[] FirefoxSubkeys = { "Extensions", "Homepage", "SearchEngines" };

    /// <summary>Pure rule: is this value, found at <paramref name="relativeSubkey"/> below a browser's policy key, a hijack-type policy?</summary>
    public static bool IsHijackValue(string browser, string relativeSubkey, string valueName)
    {
        var firefox = browser == "Firefox";
        if (relativeSubkey.Length == 0) return !firefox && ChromiumValues.Contains(valueName, StringComparer.OrdinalIgnoreCase);
        var top = relativeSubkey.Split('\\')[0];
        return (firefox ? FirefoxSubkeys : ChromiumSubkeys).Contains(top, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"id;update-url" in a forced-extension list becomes "id"; anything else is shown as its value name or subkey name.</summary>
    public static string DisplayFor(string relativeSubkey, string valueName, object? data)
    {
        if (relativeSubkey.Equals(ForcelistKey, StringComparison.OrdinalIgnoreCase) && data is string s)
        {
            var id = s.Split(';')[0].Trim();
            if (id.Length is > 0 and <= 64 && id.All(char.IsLetterOrDigit)) return id;
        }
        return relativeSubkey.Length == 0 ? valueName : relativeSubkey.Split('\\')[0];
    }

    /// <summary>Reads the flagged values under one hive. <paramref name="hive"/> is the machine or user base key.</summary>
    public static IReadOnlyList<Flag> Scan(RegistryKey hive, string hiveName)
    {
        var found = new List<Flag>();
        foreach (var (browser, path, firefox) in Roots)
        {
            var paths = hiveName == HiveMachine
                ? new[] { path, @"Software\WOW6432Node" + path["Software".Length..] }
                : new[] { path };
            foreach (var p in paths)
            {
                using var k = hive.OpenSubKey(p);
                if (k is null) continue;
                if (!firefox)
                    foreach (var name in k.GetValueNames())
                        if (IsHijackValue(browser, "", name)) found.Add(new Flag(hiveName, browser, p, name, name));
                foreach (var sub in firefox ? FirefoxSubkeys : ChromiumSubkeys)
                    Walk(k, sub, sub, p, hiveName, browser, found);
            }
        }
        return found;
    }

    private static void Walk(RegistryKey parent, string name, string relative, string basePath, string hiveName, string browser, List<Flag> found)
    {
        using var k = parent.OpenSubKey(name);
        if (k is null) return;
        foreach (var value in k.GetValueNames())
            found.Add(new Flag(hiveName, browser, basePath + "\\" + relative, value, DisplayFor(relative, value, k.GetValue(value))));
        foreach (var child in k.GetSubKeyNames())
            Walk(k, child, relative + "\\" + child, basePath, hiveName, browser, found);
    }

    /// <summary>A .reg-like text of the flagged values, for the backup that is written before anything is removed.</summary>
    public static string BackupText(RegistryKey hive, IReadOnlyList<Flag> flags)
    {
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var g in flags.GroupBy(f => f.KeyPath, StringComparer.OrdinalIgnoreCase))
        {
            var root = g.First().Hive == HiveMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
            sb.Append("\r\n[").Append(root).Append('\\').Append(g.Key).Append("]\r\n");
            using var k = hive.OpenSubKey(g.Key);
            foreach (var f in g)
            {
                var kind = k?.GetValueKind(f.ValueName) ?? RegistryValueKind.String;
                var v = k?.GetValue(f.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                sb.Append(Quote(f.ValueName)).Append('=').Append(kind switch
                {
                    RegistryValueKind.DWord when v is int i => $"dword:{i:x8}",
                    RegistryValueKind.String or RegistryValueKind.ExpandString when v is string s => Quote(s),
                    _ => "hex:" + (v is byte[] b ? string.Join(",", b.Select(x => x.ToString("x2"))) : ""),
                }).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Deletes exactly the flagged values, then an emptied ExtensionInstallForcelist subkey. Returns how many values were removed.</summary>
    public static int Remove(RegistryKey hive, IReadOnlyList<Flag> flags)
    {
        var removed = 0;
        foreach (var f in flags)
        {
            using var k = hive.OpenSubKey(f.KeyPath, writable: true);
            if (k is null || k.GetValue(f.ValueName) is null) continue;
            k.DeleteValue(f.ValueName, throwOnMissingValue: false);
            removed++;
        }
        foreach (var path in flags.Select(f => f.KeyPath).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(p => p.EndsWith("\\" + ForcelistKey, StringComparison.OrdinalIgnoreCase)))
        {
            using var parent = hive.OpenSubKey(path[..path.LastIndexOf('\\')], writable: true);
            bool empty;
            using (var k = parent?.OpenSubKey(ForcelistKey))
                empty = k is not null && k.ValueCount == 0 && k.SubKeyCount == 0;
            if (empty) parent!.DeleteSubKey(ForcelistKey, throwOnMissingSubKey: false);
        }
        return removed;
    }

    /// <summary>
    /// A computer that belongs to a domain or to Azure AD / Entra is managed by an organization, and browser policies
    /// there are normal: they are reported as information only and never removed.
    /// </summary>
    public static bool IsManagedPc()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
            if (k is { SubKeyCount: > 0 }) return true;
        }
        catch (Exception) { }
        try
        {
            using var q = new ManagementObjectSearcher("SELECT PartOfDomain FROM Win32_ComputerSystem");
            foreach (var m in q.Get().Cast<ManagementBaseObject>())
                using (m)
                    if (m["PartOfDomain"] is true) return true;
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>Browser and policy names only, no addresses: "Chrome (HKCU): HomepageLocation, ProxyServer".</summary>
    public static string Summary(IEnumerable<Flag> flags) =>
        string.Join("; ", flags.GroupBy(f => (f.Browser, f.Hive)).Select(g =>
            $"{g.Key.Browser} ({g.Key.Hive}): " + string.Join(", ", g.Select(f => f.Display).Distinct())));
}
