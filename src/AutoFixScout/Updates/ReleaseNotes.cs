using System.Text;
using System.Text.RegularExpressions;

namespace AutoFixScout.Updates;

public static class ReleaseNotes
{
    /// <summary>
    /// A GitHub release body as plain text for a panel: stops at the first horizontal rule (the release page keeps
    /// the installation guide below it), removes Markdown marks, keeps bullets as "-" lines, at most maxChars.
    /// </summary>
    public static string ToPlainText(string? markdown, int maxChars = 1500)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (Regex.IsMatch(line, @"^\s*(-{3,}|\*{3,}|_{3,})\s*$")) break;
            line = Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");
            line = Regex.Replace(line, @"^\s*[*+]\s+", "- ");
            line = Regex.Replace(line, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            sb.AppendLine(line);
        }
        var text = Regex.Replace(sb.ToString().Trim(), @"\n{3,}", "\n\n");
        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "...";
    }
}
