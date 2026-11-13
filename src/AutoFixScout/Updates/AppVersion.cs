using System.Reflection;

namespace AutoFixScout.Updates;

/// <summary>
/// A version such as "0.3.0", "v0.3.0", "0.3.0+abc123" (commit part ignored), "0.3.0-beta.1" (prerelease) or
/// "0.0.45-dev" (a CI build, older than every release).
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, int Build, string? Pre) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; if (pre.Length == 0) return false; }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var n = new int[4];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, null, out n[i])) return false;
        version = new AppVersion(n[0], n[1], n[2], n[3], pre);
        return true;
    }

    public static AppVersion? Parse(string? text) => TryParse(text, out var v) ? v : null;

    public bool IsPrerelease => Pre is not null;

    /// <summary>A CI build (0.0.N-dev) or a 0.0.x number: never offered updates unless the setting allows it.</summary>
    public bool IsDev => (Pre is not null && Pre.Contains("dev", StringComparison.OrdinalIgnoreCase)) || (Major == 0 && Minor == 0);

    public int CompareTo(AppVersion o)
    {
        var c = (Major, Minor, Patch, Build).CompareTo((o.Major, o.Minor, o.Patch, o.Build));
        if (c != 0) return c;
        if (Pre is null && o.Pre is null) return 0;
        if (Pre is null) return 1;      // 1.0.0 is newer than 1.0.0-beta
        if (o.Pre is null) return -1;
        return string.CompareOrdinal(Pre, o.Pre);
    }

    /// <summary>True when <paramref name="candidate"/> should replace <paramref name="current"/>. Dev builds are older than any release.</summary>
    public static bool IsNewer(AppVersion candidate, AppVersion current)
    {
        if (current.IsDev && !candidate.IsDev) return true;
        return candidate.CompareTo(current) > 0;
    }

    /// <summary>Normalised "major.minor.patch" (with prerelease), for comparing and storing.</summary>
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Build != 0 ? $".{Build}" : "") + (Pre is null ? "" : "-" + Pre);

    /// <summary>The running app's version (informational version without the commit part).</summary>
    public static AppVersion Current()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(info, out var v)) return v;
        var av = asm.GetName().Version;
        return av is null ? new AppVersion(0, 0, 0, 0, "dev") : new AppVersion(av.Major, av.Minor, Math.Max(av.Build, 0), 0, null);
    }
}
