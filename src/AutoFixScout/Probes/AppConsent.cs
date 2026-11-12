using Microsoft.Win32;

namespace AutoFixScout.Probes;

/// <summary>
/// Per-app privacy choices under HKCU\...\CapabilityAccessManager\ConsentStore\microphone or \webcam.
/// Packaged apps are subkeys named after the package family; desktop apps sit under NonPackaged, one subkey per
/// exe path with '#' instead of '\'. Only the exe file name is ever kept, never a full path (it may contain a user name).
/// </summary>
public static class AppConsent
{
    /// <summary>Most apps named in one finding.</summary>
    public const int MaxNamed = 3;

    private static readonly (string Part, string Name)[] KnownApps =
    {
        ("MicrosoftTeams", "Microsoft Teams"), ("MSTeams", "Microsoft Teams"),
        ("Zoom", "Zoom"), ("WhatsApp", "WhatsApp"), ("Skype", "Skype"),
        ("WindowsCamera", "Camera"), ("Discord", "Discord"), ("Slack", "Slack"),
    };

    /// <summary>Names of apps whose choice is "Deny", packaged first, at most <see cref="MaxNamed"/> distinct names.</summary>
    public static IReadOnlyList<string> ReadDenied(RegistryKey? capability)
    {
        var names = new List<string>();
        if (capability is null) return names;
        foreach (var key in capability.GetSubKeyNames().Where(k => k != "NonPackaged").OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            if (IsDenied(capability, key)) names.Add(PackagedName(key));
        using var desktop = capability.OpenSubKey("NonPackaged");
        if (desktop != null)
            foreach (var key in desktop.GetSubKeyNames().OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                if (IsDenied(desktop, key)) names.Add(DesktopName(key));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => n.Length > 0).ToList();
    }

    private static bool IsDenied(RegistryKey parent, string name)
    {
        using var app = parent.OpenSubKey(name);
        return app?.GetValue("Value") as string == "Deny";
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "Camera"; unknown families -> the last word of the family.</summary>
    public static string PackagedName(string keyName)
    {
        var family = keyName.Split('_')[0];
        foreach (var (part, name) in KnownApps)
            if (family.Contains(part, StringComparison.OrdinalIgnoreCase)) return name;
        return family[(family.LastIndexOf('.') + 1)..];
    }

    /// <summary>"C:#Users#Ann#AppData#Local#Zoom#bin#Zoom.exe" -> "Zoom" (file name only, without .exe).</summary>
    public static string DesktopName(string keyName)
    {
        var file = keyName.Split('#', '\\', '/').Last();
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
        foreach (var (part, name) in KnownApps)
            if (string.Equals(file, part, StringComparison.OrdinalIgnoreCase)) return name;
        return file;
    }

    /// <summary>Up to <see cref="MaxNamed"/> names as a sentence part: "A", "A and B", "A, B and C".</summary>
    public static string NameList(IReadOnlyList<string> apps)
    {
        var shown = apps.Take(MaxNamed).Select(a => $"\"{a}\"").ToList();
        return shown.Count <= 1 ? string.Concat(shown) : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
    }
}
