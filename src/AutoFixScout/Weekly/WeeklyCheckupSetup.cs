using System.Diagnostics;
using AutoFixScout.Probes;

namespace AutoFixScout.Weekly;

/// <summary>
/// Switches the optional weekly check-up on and off for the signed-in person with one Task Scheduler task. The task belongs
/// to the current user only (no /ru, no /rl highest), so no administrator rights are needed. The command lines are built by
/// pure functions; <see cref="On"/>, <see cref="Off"/> and <see cref="ReadState"/> only run them.
/// </summary>
public static class WeeklyCheckupSetup
{
    public const string TaskName = "AutoFix Scout weekly check-up";
    public const string ExeName = "AutoFixScout.UI.exe";
    public const string Argument = "--weekly-checkup";

    public static string ExePath(string? baseDir = null) => Path.Combine(baseDir ?? AppContext.BaseDirectory, ExeName);

    /// <summary>The task action: the program path in quotes (it may contain spaces), then the argument.</summary>
    public static string TaskRun(string exePath) => "\"" + exePath.Trim('"') + "\" " + Argument;

    /// <summary>Five minutes from now, rounded up to a whole five minutes, so the first run happens soon after switching on.</summary>
    public static DateTime FirstRun(DateTime now)
    {
        var t = now.AddMinutes(5);
        t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
        var extra = (5 - t.Minute % 5) % 5;
        return t.AddMinutes(extra);
    }

    /// <summary>Three-letter day name that schtasks understands in every Windows language.</summary>
    public static string DayCode(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "MON", DayOfWeek.Tuesday => "TUE", DayOfWeek.Wednesday => "WED", DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI", DayOfWeek.Saturday => "SAT", _ => "SUN",
    };

    public static IReadOnlyList<string> CreateArguments(string exePath, DateTime now)
    {
        var first = FirstRun(now);
        return new[]
        {
            "/create", "/tn", TaskName, "/sc", "weekly", "/d", DayCode(first.DayOfWeek),
            "/st", first.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            "/tr", TaskRun(exePath), "/f",
        };
    }

    public static IReadOnlyList<string> DeleteArguments() => new[] { "/delete", "/tn", TaskName, "/f" };
    public static IReadOnlyList<string> QueryArguments() => new[] { "/query", "/tn", TaskName };

    /// <summary>
    /// Task Scheduler settings that schtasks cannot set: run when the computer was off at the planned time, also on battery,
    /// and give up after five minutes. A task owned by the signed-in person can be changed by that person without administrator
    /// rights. Best effort: when it fails the task still runs weekly, it only does not catch up a missed run.
    /// </summary>
    public static IReadOnlyList<string> CatchUpArguments() => new[]
    {
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
        $"Set-ScheduledTask -TaskName '{TaskName}' -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable " +
        "-AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)) | Out-Null",
    };

    public static (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (-1, "could not start " + file);
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (Exception) { }
                return (-1, "timed out");
            }
            return (p.ExitCode, (outTask.Result + errTask.Result).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    private static string SchTasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Yes = the task exists; No = it does not; Unknown = schtasks could not be run.</summary>
    public static Tri ReadState()
    {
        var (code, _) = Run(SchTasks, QueryArguments(), 15000);
        return code == 0 ? Tri.Yes : code == -1 ? Tri.Unknown : Tri.No;
    }

    public static (bool Ok, string Detail) On(string? exePath = null, DateTime? now = null)
    {
        exePath ??= ExePath();
        if (!File.Exists(exePath)) return (false, ExeName + " was not found");
        var (code, output) = Run(SchTasks, CreateArguments(exePath, now ?? DateTime.Now));
        if (code != 0) return (false, "The scheduled task could not be created: " + output);
        var (c2, _) = Run(PowerShell, CatchUpArguments(), 30000);
        return (true, c2 == 0 ? "Weekly check-up switched on (a missed run is caught up)" : "Weekly check-up switched on (a missed run is not caught up)");
    }

    public static (bool Ok, string Detail) Off()
    {
        if (ReadState() == Tri.No) return (true, "Weekly check-up was already off");
        var (code, output) = Run(SchTasks, DeleteArguments());
        return code == 0 ? (true, "Weekly check-up switched off") : (false, "The scheduled task could not be removed: " + output);
    }
}
