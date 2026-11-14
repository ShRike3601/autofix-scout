using System.Text.RegularExpressions;

namespace AutoFixScout.Areas;

/// <summary>Finds the one app setting a typed description is about, so the search can offer to open it.</summary>
public static class SettingsSearch
{
    private static readonly (string Title, string[] Keywords)[] Settings =
    [
        ("Theme", ["dark", "light", "theme", "colours"]),
        ("Accent colour", ["accent", "colour", "color"]),
        ("Text size", ["text size", "bigger text", "font size", "zoom"]),
        ("Frosted glass", ["glass", "transparency", "blur"]),
        ("Reduce motion", ["animation", "motion"]),
        ("Check for updates", ["update", "version"]),
        ("Get test versions early", ["beta", "test version"]),
        ("Weekly check-up", ["weekly", "schedule"]),
        ("Clear history", ["clear history", "delete history"]),
    ];

    /// <summary>The title of the first setting with a keyword in the text, or null. A keyword matches as a whole word, or the word plus "s"; case does not matter.</summary>
    public static string? Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var (title, keywords) in Settings)
            foreach (var k in keywords)
                if (Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(k)}s?(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase))
                    return title;
        return null;
    }
}
