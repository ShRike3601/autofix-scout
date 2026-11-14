using System.Net;
using System.Text.RegularExpressions;
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

    // ---- the HTML report (one self-contained file a person can e-mail) ----

    private static readonly Regex UserFolder = new(@"[A-Za-z]:\\Users\\[^\\/:*?""<>|\r\n\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex Ipv6 = new(@"(?<![\w:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?=[0-9A-Fa-f:]*[0-9A-Fa-f])[0-9A-Fa-f:]*::[0-9A-Fa-f:]*)(?![\w:])", RegexOptions.Compiled);
    private static readonly Regex Mac = new(@"\b[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(\1[0-9A-Fa-f]{2}){4}\b", RegexOptions.Compiled);
    private static readonly Regex Ssid = new(@"(?i)\b(ssid|network name|profile)(""?\s*[:=]\s*""?)([^"",;\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex Serial = new(@"(?i)\b(serial(?: number)?|service tag)(""?\s*[:=]\s*""?)([^"",;\r\n]+)", RegexOptions.Compiled);

    /// <summary>
    /// Removes what must never leave this computer: user and computer name, user folder paths, serial numbers, network
    /// names, IP and MAC addresses. <paramref name="hide"/> lists extra values to remove (for example a network name).
    /// </summary>
    public static string Redact(string? text, IEnumerable<string>? hide = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = Activity.Scrub(text);
        var secrets = new List<string>(hide ?? Array.Empty<string>()) { Environment.UserName, Environment.MachineName };
        foreach (var secret in secrets.Where(x => x is { Length: >= 3 }).OrderByDescending(x => x.Length))
            s = Regex.Replace(s, Regex.Escape(secret), "[removed]", RegexOptions.IgnoreCase);
        s = UserFolder.Replace(s, "%USERPROFILE%");
        s = Mac.Replace(s, "a hardware address");
        s = Ipv4.Replace(s, "an IP address");
        s = Ipv6.Replace(s, "an IP address");
        s = Ssid.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "[removed]");
        s = Serial.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "[removed]");
        return s;
    }

    public static string OutcomeSentence(Outcome o) => o switch
    {
        Outcome.Fixed => "The problem has been resolved.",
        Outcome.NoFaultFound => "No problem was found, so no changes were made.",
        Outcome.ProbableHardware => "AutoFix Scout could not resolve this problem. It is probably caused by the hardware.",
        Outcome.NotFixed => "AutoFix Scout could not resolve this problem.",
        _ => "The check was stopped before the end.",
    };

    /// <summary>
    /// The report as one HTML file: inline style only, no script, no link and no image; light and dark colours,
    /// print friendly. Pure: every input is passed in. Every value is redacted and then HTML-encoded.
    /// </summary>
    public static string BuildHtml(string topic, IReadOnlyList<Triage.Finding> findings, IEnumerable<string> tried, Outcome outcome,
        DateTime created, IReadOnlyList<SummaryLine> computer, IReadOnlyList<ChangeEntry> changes,
        IReadOnlyList<ActivityItem> steps, IReadOnlyList<SessionLog.SessionSummary> recent, string version, IEnumerable<string>? hide = null)
    {
        var hidden = hide?.ToList();
        string E(string? v) => WebUtility.HtmlEncode(Redact(v, hidden));
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<meta name=\"color-scheme\" content=\"light dark\">\n")
          .Append("<title>AutoFix Scout report for the person helping you</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");
        sb.Append("<h1>AutoFix Scout: report for the person helping you</h1>\n");

        sb.Append("<section id=\"summary\">\n<h2>Summary</h2>\n<dl>\n")
          .Append($"<dt>Problem checked</dt><dd>{E(HistoryText.Symptom(topic))}</dd>\n")
          .Append($"<dt>Outcome</dt><dd><span class=\"badge {(outcome is Outcome.Fixed or Outcome.NoFaultFound ? "ok" : "warn")}\">{E(OutcomeSentence(outcome))}</span></dd>\n")
          .Append($"<dt>Date</dt><dd>{created:d MMMM yyyy, HH:mm}</dd>\n</dl>\n</section>\n");

        sb.Append("<section id=\"computer\">\n<h2>This computer</h2>\n");
        if (computer.Count == 0) sb.Append("<p class=\"muted\">No details about this computer could be read.</p>\n");
        else
        {
            sb.Append("<dl>\n");
            foreach (var l in computer) sb.Append($"<dt>{E(l.Label)}</dt><dd>{E(l.Value)}</dd>\n");
            sb.Append("</dl>\n");
        }
        sb.Append("</section>\n");

        sb.Append("<section id=\"found\">\n<h2>What was found</h2>\n");
        if (findings.Count == 0) sb.Append("<p class=\"muted\">No findings were recorded.</p>\n");
        else
        {
            sb.Append("<ul class=\"items\">\n");
            foreach (var f in findings)
                sb.Append($"<li><strong>{E(f.PlainMessage)}</strong><br><code>{E(f.Code.ToString())}</code> <span class=\"detail\">{E(f.TechnicalDetail)}</span></li>\n");
            sb.Append("</ul>\n");
        }
        sb.Append("</section>\n");

        var triedList = tried.ToList();
        sb.Append("<section id=\"tried\">\n<h2>What was tried</h2>\n");
        if (triedList.Count == 0) sb.Append("<p class=\"muted\">No steps were tried.</p>\n");
        else
        {
            sb.Append("<ol class=\"items\">\n");
            foreach (var t in triedList) sb.Append($"<li>{E(t)}</li>\n");
            sb.Append("</ol>\n");
        }
        sb.Append("</section>\n");

        sb.Append("<section id=\"changes\">\n<h2>Changes AutoFix Scout made</h2>\n");
        if (changes.Count == 0) sb.Append("<p class=\"muted\">AutoFix Scout has not made any changes to this computer.</p>\n");
        else
        {
            sb.Append("<table>\n<thead><tr><th>When</th><th>Change</th><th>Undo</th></tr></thead>\n<tbody>\n");
            foreach (var c in changes)
            {
                var state = c.Undone ? "Undone" : c.CanUndo ? "Can be undone" : "Cannot be undone automatically";
                sb.Append($"<tr><td>{c.WhenUtc.ToLocalTime():d MMM yyyy, HH:mm}</td><td>{E(c.Description)}{(c.Hint is { Length: > 0 } ? $"<br><span class=\"muted\">{E(c.Hint)}</span>" : "")}</td><td>{state}</td></tr>\n");
            }
            sb.Append("</tbody>\n</table>\n");
        }
        sb.Append("</section>\n");

        sb.Append("<section id=\"steps\">\n<h2>Steps AutoFix Scout took</h2>\n");
        if (steps.Count == 0) sb.Append("<p class=\"muted\">No steps were recorded for this check.</p>\n");
        else
        {
            sb.Append($"<details>\n<summary>Show the {steps.Count} steps</summary>\n<ol class=\"log\">\n");
            foreach (var a in steps)
                sb.Append($"<li><span class=\"muted\">{a.Time:HH:mm:ss}</span> {E(a.Text)}{(a.Took is { } t ? $" <span class=\"muted\">({Activity.Seconds(t)})</span>" : "")}</li>\n");
            sb.Append("</ol>\n</details>\n");
        }
        sb.Append("</section>\n");

        sb.Append("<section id=\"recent\">\n<h2>Recent checks</h2>\n");
        if (recent.Count == 0) sb.Append("<p class=\"muted\">No checks have been carried out on this computer yet.</p>\n");
        else
        {
            sb.Append("<table>\n<thead><tr><th>When</th><th>Check</th><th>Result</th></tr></thead>\n<tbody>\n");
            foreach (var r in recent.Take(10))
                sb.Append($"<tr><td>{r.StartedUtc.ToLocalTime():d MMM yyyy, HH:mm}</td><td>{E(HistoryText.Symptom(r.Symptom))}</td><td>{E(HistoryText.Outcome(r.Outcome))}</td></tr>\n");
            sb.Append("</tbody>\n</table>\n");
        }
        sb.Append("</section>\n");

        sb.Append($"<section id=\"version\">\n<h2>App version</h2>\n<p>AutoFix Scout version {E(version)}.</p>\n</section>\n");
        sb.Append("<footer>This report was created on the computer it describes. Personal details such as the user name, the computer name and network names have been left out.</footer>\n");
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private const string Css = @":root{color-scheme:light dark;--bg:#f6f7f9;--card:#fff;--fg:#1c2330;--muted:#5d6878;--line:#d9dee6;--accent:#1f5fbf;--okbg:#dff3e4;--okfg:#14532d;--warnbg:#fdecc8;--warnfg:#6b3e00}
@media (prefers-color-scheme:dark){:root{--bg:#14171c;--card:#1d222a;--fg:#e8ebf0;--muted:#9aa5b5;--line:#343b47;--accent:#7fb0ff;--okbg:#17361f;--okfg:#9be3b0;--warnbg:#44330f;--warnfg:#ffd98a}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,'Segoe UI',Roboto,Arial,sans-serif}
main{max-width:820px;margin:0 auto;padding:24px 16px 48px}
h1{font-size:1.5rem;margin:0 0 16px}
h2{font-size:1.1rem;margin:0 0 10px;color:var(--accent)}
section{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin:0 0 14px;break-inside:avoid}
dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 18px;margin:0}
dt{color:var(--muted)}dd{margin:0;overflow-wrap:anywhere}
.badge{display:inline-block;padding:2px 10px;border-radius:999px;font-weight:600}
.badge.ok{background:var(--okbg);color:var(--okfg)}.badge.warn{background:var(--warnbg);color:var(--warnfg)}
.muted{color:var(--muted)}.detail{color:var(--muted);overflow-wrap:anywhere}
ul.items,ol.items{margin:0;padding-left:22px}ul.items li,ol.items li{margin:0 0 8px}
code{font:13px ui-monospace,Consolas,monospace;background:var(--bg);padding:1px 5px;border-radius:4px}
table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-weight:600}
summary{cursor:pointer;color:var(--accent);font-weight:600}
ol.log{font:13px ui-monospace,Consolas,monospace;padding-left:26px}
footer{color:var(--muted);font-size:13px;margin-top:18px}
@media print{body{background:#fff;color:#000;font-size:11pt}section{border-color:#999;background:#fff}h2{color:#000}main{padding:0}}
";

    /// <summary>The HTML report for the check that just finished, with live data from this computer. Never throws on missing data.</summary>
    public static string BuildHtmlNow(string topic, IReadOnlyList<Triage.Finding> findings, IEnumerable<string> tried, Outcome outcome, string version)
    {
        IReadOnlyList<SummaryLine> computer; IReadOnlyList<ChangeEntry> changes; IReadOnlyList<SessionLog.SessionSummary> recent;
        try { computer = SystemSummary.Read(); } catch (Exception) { computer = Array.Empty<SummaryLine>(); }
        try { changes = new ChangeHistory().List(20); } catch (Exception) { changes = Array.Empty<ChangeEntry>(); }
        try { using var db = new SessionLog(SessionLog.DefaultPath); recent = db.Recent(10); } catch (Exception) { recent = Array.Empty<SessionLog.SessionSummary>(); }
        var steps = Activity.Recent().Where(a => a.Area.Equals(topic, StringComparison.OrdinalIgnoreCase)).ToList();
        return BuildHtml(topic, findings, tried, outcome, DateTime.Now, computer, changes, steps, recent, version);
    }

    /// <summary>Writes AutoFix-Scout-report-yyyy-MM-dd-HHmm.html under the reports folder and returns the path.</summary>
    public static string SaveHtml(string html, DateTime? now = null, string? folder = null)
    {
        var dir = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"AutoFix-Scout-report-{now ?? DateTime.Now:yyyy-MM-dd-HHmm}.html");
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return path;
    }
}
