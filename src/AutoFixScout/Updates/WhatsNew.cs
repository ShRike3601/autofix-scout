namespace AutoFixScout.Updates;

/// <summary>What the "What's new" panel shows.</summary>
public sealed record WhatsNewContent(string Version, string Text);

/// <summary>
/// "What's new" appears once on the first start of a new version. The text is the GitHub release notes when they were
/// fetched during the update check, otherwise the built-in docs/WHATS_NEW.md. Development builds never show it.
/// </summary>
public sealed class WhatsNew
{
    private readonly string _dir;
    private readonly Func<AppVersion> _current;

    public WhatsNew(string? dataDir = null, Func<AppVersion>? currentVersion = null)
    {
        _dir = dataDir ?? AppPaths.DataDir;
        _current = currentVersion ?? AppVersion.Current;
    }

    /// <summary>
    /// The notes to show now, or null (same version as last time, or a development build). Calling it records the
    /// version as seen, so the panel appears exactly once.
    /// </summary>
    public WhatsNewContent? TakeOnce()
    {
        try
        {
            var current = _current();
            if (current.IsDev) return null;
            var state = UpdateState.Load(_dir);
            var seen = AppVersion.Parse(state.LastSeenVersion);
            if (seen is { } s && s.CompareTo(current) == 0) return null;
            var text = "";
            if (state.Latest is { } l && AppVersion.Parse(l.Version) is { } lv && lv.CompareTo(current) == 0)
                text = ReleaseNotes.ToPlainText(l.Notes);
            if (text.Length == 0) text = BuiltIn();
            state.LastSeenVersion = current.ToString();
            state.Save(_dir);
            return text.Length == 0 ? null : new WhatsNewContent(current.ToString(), text);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The embedded docs/WHATS_NEW.md as plain text.</summary>
    public static string BuiltIn()
    {
        using var s = typeof(WhatsNew).Assembly.GetManifestResourceStream("WHATS_NEW.md");
        if (s is null) return "";
        using var r = new StreamReader(s);
        return ReleaseNotes.ToPlainText(r.ReadToEnd(), 4000);
    }
}
