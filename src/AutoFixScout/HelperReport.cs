using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoFixScout;

/// <summary>report.export (spec 4.3): plain text a helper can read before touching the machine. Stays on this PC.</summary>
public static class HelperReport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string Build(string topic, object probeState, IReadOnlyList<Triage.Finding> findings, IEnumerable<string> tried, Outcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AutoFix Scout: report for the person helping you ({topic})");
        sb.AppendLine($"Created: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"Windows version: {Environment.OSVersion.VersionString}");
        sb.AppendLine($"Result: {outcome}");
        sb.AppendLine();
        sb.AppendLine("What AutoFix Scout found:");
        foreach (var f in findings) sb.AppendLine($"  [{f.Code}] {f.PlainMessage} -- {f.TechnicalDetail}");
        sb.AppendLine();
        sb.AppendLine("Steps that were tried:");
        foreach (var t in tried) sb.AppendLine($"  {t}");
        sb.AppendLine();
        sb.AppendLine("Technical details reported by Windows (before any change):");
        sb.AppendLine(JsonSerializer.Serialize(probeState, probeState.GetType(), Json));
        return sb.ToString();
    }

    /// <summary>Writes the report under %LOCALAPPDATA%\AutoFixScout\reports and returns the path.</summary>
    public static string Save(string topic, string text)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{topic}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, text);
        return path;
    }
}
