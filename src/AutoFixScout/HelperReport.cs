using System.Text;
using AutoFixScout.Probes;
using AutoFixScout.Triage;

namespace AutoFixScout;

/// <summary>report.export (spec 4.3): plain text a helper can read before touching the machine.</summary>
public static class HelperReport
{
    public static string Build(AudioState state, IReadOnlyList<Triage.Finding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AutoFix Scout - helper report (sound)");
        sb.AppendLine($"Created: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
        sb.AppendLine();
        sb.AppendLine("Findings:");
        foreach (var f in findings) sb.AppendLine($"  [{f.Code}] {f.PlainMessage} -- {f.TechnicalDetail}");
        sb.AppendLine();
        sb.AppendLine("Services:");
        foreach (var s in state.Services) sb.AppendLine($"  {s.Name}: {s.Status}");
        sb.AppendLine();
        sb.AppendLine($"Default output: muted={state.DefaultMuted}, volume={state.DefaultVolumePercent?.ToString() ?? "unknown"}%");
        sb.AppendLine("Output endpoints:");
        foreach (var e in state.Endpoints)
            sb.AppendLine($"  {(e.IsDefault ? "*" : " ")} {e.Name} [{e.KindGuess}] {(e.IsActive ? "active" : "inactive")}");
        sb.AppendLine();
        sb.AppendLine("Sound devices (MEDIA class):");
        foreach (var d in state.Devices)
            sb.AppendLine($"  {d.Name}\n    id={d.InstanceId}\n    problem code={d.ProblemCode?.ToString() ?? "unknown"}, driver {d.DriverVersion} ({d.DriverDate}) by {d.DriverProvider}");
        if (state.ProbeErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Probe errors:");
            foreach (var err in state.ProbeErrors) sb.AppendLine($"  {err}");
        }
        return sb.ToString();
    }

    /// <summary>Writes the report under %LOCALAPPDATA%\AutoFixScout\reports and returns the path.</summary>
    public static string Save(string text)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"sound-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, text);
        return path;
    }
}
