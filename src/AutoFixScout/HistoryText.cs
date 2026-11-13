namespace AutoFixScout;

/// <summary>The "History and reports" screen text. Pure, so the wording is tested.</summary>
public static class HistoryText
{
    public static string Symptom(string s) => s switch
    {
        "audio" => "Sound", "camera" => "Camera", "network" => "Internet connection", "microphone" => "Microphone",
        "bluetooth" => "Bluetooth", "printer" => "Printer", "display" => "Screen", "performance" => "Computer speed", "graphics" => "Graphics card", "storage" => "Slow or full computer",
        "update" => "Windows Update", "stress" => "Short load test", "health" => "Windows health", "shell" => "Start menu and taskbar",
        "apps" => "Apps and programs", "security" => "Windows security", "power" => "Battery and power", "input" => "Keyboard, mouse and touchpad",
        _ => "Other check",
    };

    public static string Outcome(string? o) => o switch
    {
        "fixed" => "resolved", "not_fixed" => "not resolved", "probable_hardware" => "probably hardware",
        "no_fault_found" => "no problem found", "abandoned" => "stopped before the end", _ => "not finished",
    };

    public static string Build(string version, IReadOnlyList<SessionLog.SessionSummary> recent)
    {
        var lines = new List<string> { "History and reports", "", $"AutoFix Scout version {version}." };
        if (recent.Count == 0) lines.Add("No checks have been carried out on this computer yet.");
        else
        {
            lines.Add("Recent checks:");
            lines.AddRange(recent.Select(r => $"  {r.StartedUtc.ToLocalTime():d MMM, HH:mm}: {Symptom(r.Symptom)}, {Outcome(r.Outcome)}"));
        }
        lines.Add("Reports for the person helping you, and copies of the settings AutoFix Scout changed, are kept on this computer only.");
        return string.Join("\n", lines);
    }
}
