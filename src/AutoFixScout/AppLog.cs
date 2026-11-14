using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace AutoFixScout;

/// <summary>
/// AutoFix Scout's own problem log, so that a person who tests the app can send something useful. One line per entry
/// in %LOCALAPPDATA%\AutoFixScout\logs\app-yyyy-MM.log. Each file stays under 1 MB and the last 3 files are kept.
/// Every line passes through <see cref="HelperReport.Redact"/> before it is written. Nothing here ever throws, and the
/// log never leaves the computer unless the person sends the helper report.
/// Line format: time | level | version | area | text.
/// </summary>
public static class AppLog
{
    /// <summary>The log of this user, in %LOCALAPPDATA%\AutoFixScout\logs.</summary>
    public static AppLogFile Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "logs"), CurrentVersion());

    /// <summary>Records that the app started, with its version.</summary>
    public static void Start() => Default.Info("start", $"AutoFix Scout {Default.Version} started");

    /// <summary>Records a warning such as "probe part X failed", with the exception. Never throws.</summary>
    public static void Warn(string area, Exception ex) => Default.Warn(area, ex);

    /// <summary>Records a warning text without an exception. Never throws.</summary>
    public static void Warn(string area, string text) => Default.Write("WARN", area, text);

    /// <summary>Records an unexpected exception (type, message, top frames, version, area). Never throws.</summary>
    public static void Error(string source, Exception ex) => Default.Error(source, ex);

    /// <summary>The newest <paramref name="count"/> entries, oldest first. Never throws.</summary>
    public static IReadOnlyList<string> Recent(int count = 20) => Default.Recent(count);

    internal static string CurrentVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AppLog).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(info) ? "unknown" : info.Split('+')[0];
        }
        catch (Exception) { return "unknown"; }
    }
}

/// <summary>The writer behind <see cref="AppLog"/>. A separate class so tests can use a temporary folder.</summary>
public sealed class AppLogFile
{
    public const long MaxFileBytes = 1024 * 1024;
    public const int FilesKept = 3;
    public const int MaxFrames = 8;

    private readonly object _gate = new();
    private readonly Func<IEnumerable<string>> _hide;

    public string Version { get; }
    public string Folder { get; }

    public AppLogFile(string folder, string version, Func<IEnumerable<string>>? hide = null)
    {
        Folder = folder;
        Version = version;
        _hide = hide ?? (() => Array.Empty<string>());
    }

    public void Info(string area, string text) => Write("INFO", area, text);

    public void Warn(string area, Exception ex) => Write("WARN", area, Describe(ex, withFrames: false));

    public void Error(string source, Exception ex) => Write("ERROR", source, Describe(ex, withFrames: true));

    /// <summary>"Type: message | at Class.Method; at ..." with at most 8 frames, method names only.</summary>
    public static string Describe(Exception ex, bool withFrames = true)
    {
        try
        {
            var sb = new StringBuilder(ex.GetType().FullName).Append(": ").Append(ex.Message);
            if (ex.InnerException is { } inner) sb.Append(" (inner ").Append(inner.GetType().Name).Append(": ").Append(inner.Message).Append(')');
            if (withFrames)
            {
                var frames = Frames(ex);
                if (frames.Count > 0) sb.Append(" | at ").Append(string.Join("; at ", frames));
            }
            return sb.ToString();
        }
        catch (Exception) { return "an exception that could not be described"; }
    }

    /// <summary>The top frames as "Class.Method": no file paths, no line numbers, no parameters.</summary>
    public static IReadOnlyList<string> Frames(Exception ex)
    {
        var list = new List<string>();
        try
        {
            foreach (var f in new StackTrace(ex, false).GetFrames())
            {
                var m = f.GetMethod();
                if (m == null) continue;
                list.Add($"{m.DeclaringType?.Name ?? "?"}.{m.Name}");
                if (list.Count >= MaxFrames) break;
            }
        }
        catch (Exception) { /* no frames is fine */ }
        return list;
    }

    public void Write(string level, string area, string text)
    {
        try
        {
            var shownArea = string.IsNullOrWhiteSpace(area) ? Activity.Area : area;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {level} | {Version} | {(shownArea.Length == 0 ? "-" : shownArea)} | {text}";
            line = HelperReport.Redact(line.Replace("\r", " ").Replace("\n", " "), SafeHide());
            if (line.Length > 4000) line = line[..4000] + "...";
            lock (_gate)
            {
                Directory.CreateDirectory(Folder);
                var path = CurrentPath();
                var bytes = Encoding.UTF8.GetByteCount(line) + 2;
                if (File.Exists(path) && new FileInfo(path).Length + bytes > MaxFileBytes) Roll(path);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                Prune();
            }
        }
        catch (Exception) { /* logging never throws */ }
    }

    /// <summary>The newest <paramref name="count"/> entries across the kept files, oldest first. Never throws.</summary>
    public IReadOnlyList<string> Recent(int count = 20)
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<string>();
            var result = new List<string>();
            foreach (var file in LogFiles())
            {
                string[] lines;
                lock (_gate) lines = ReadLines(file.FullName);
                for (var i = lines.Length - 1; i >= 0 && result.Count < count; i--)
                    if (lines[i].Length > 0) result.Add(lines[i]);
                if (result.Count >= count) break;
            }
            result.Reverse();
            return result;
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private IEnumerable<string> SafeHide()
    {
        try { return _hide().ToList(); } catch (Exception) { return Array.Empty<string>(); }
    }

    private string CurrentPath() => Path.Combine(Folder, $"app-{DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.log");

    private void Roll(string path)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmssfff", CultureInfo.InvariantCulture);
        var rolled = Path.Combine(Folder, $"app-{stamp}-earlier.log");
        for (var n = 1; File.Exists(rolled); n++) rolled = Path.Combine(Folder, $"app-{stamp}-earlier{n}.log");
        File.Move(path, rolled);
    }

    private IEnumerable<FileInfo> LogFiles() =>
        new DirectoryInfo(Folder).GetFiles("app-*.log").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name);

    private void Prune()
    {
        foreach (var old in LogFiles().Skip(FilesKept))
            try { old.Delete(); } catch (Exception) { }
    }
}
