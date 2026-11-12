using AutoFixScout.Probes;
using AutoFixScout.Repair;

namespace AutoFixScout.Triage;

/// <summary>The Windows shell packages that draw the Start menu, taskbar, notifications and search (as installed).</summary>
public sealed record ShellPackage(string Name, string FullName, string Status);

/// <summary>probe.shell: shell crashes, the Windows Search service and the shell packages.</summary>
public sealed class ShellState
{
    public IReadOnlyList<CrashInfo> ShellCrashes { get; set; } = new List<CrashInfo>();
    public UpdateServiceInfo? Search { get; set; }
    public IReadOnlyList<ShellPackage> Packages { get; set; } = new List<ShellPackage>();
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

/// <summary>
/// "The Start menu, taskbar or search does not work". These problems often leave no trace in the event log, so the
/// steps are offered by symptom, mildest first: restart the shell parts, repair the search service, register the shell
/// packages again, then repair Windows' own files.
/// </summary>
public static class ShellTriage
{
    /// <summary>Package names (Get-AppxPackage Name) that make up the shell on Windows 10 and 11.</summary>
    public static readonly string[] PackageNames =
    {
        "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Windows.ShellExperienceHost", "MicrosoftWindows.Client.CBS",
        "MicrosoftWindows.Client.Core", "Microsoft.Windows.Search",
    };

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    public static IReadOnlyList<Finding> Evaluate(ShellState s)
    {
        if (s.ProbeErrors.Count > 0 && s.ShellCrashes.Count == 0 && s.Packages.Count == 0)
            return new[] { F(FindingCode.ProbeIncomplete, "AutoFix Scout could not complete the check.", string.Join("; ", s.ProbeErrors)) };
        var f = new List<Finding>();
        if (s.Packages.FirstOrDefault(p => !p.Status.Equals("Ok", StringComparison.OrdinalIgnoreCase)) is { } broken)
            f.Add(F(FindingCode.ShellPackageDamaged, "A part of Windows that draws the Start menu or taskbar is damaged.", $"{broken.FullName}: {broken.Status}"));
        if (s.ShellCrashes.Count > 0)
            f.Add(F(FindingCode.HealthShellCrashing, "Parts of Windows itself, such as the taskbar, the Start menu or search, have closed unexpectedly.",
                string.Join("; ", s.ShellCrashes.Select(c => $"{c.App} x{c.Count} ({c.Module})"))));
        if (s.Search is { } ws && ServiceDefaults.IsFault(ws.Name, ws.Status, ws.StartType))
            f.Add(F(FindingCode.HealthServiceOff, "The \"Windows Search\" service is switched off or stopped, so search cannot find anything.",
                $"WSearch {ws.Status}/{ws.StartType}"));
        if (f.Count > 0) return f;
        return new[] { F(FindingCode.LooksNormal,
            "Windows has not recorded a fault in the Start menu, taskbar or search. AutoFix Scout can still restart and repair them step by step.",
            $"packages: {string.Join(", ", s.Packages.Select(p => p.Name))}") };
    }

    public static IReadOnlyList<Rung> Ladder(ShellState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var r = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return r;
        r.Add(new("restart_start_menu", "shell.restart_start_menu", 0,
            "AutoFix Scout can restart the Start menu and search. They start again by themselves within a few seconds; open programs stay open."));
        r.Add(new("restart_explorer", "shell.restart_explorer", 0,
            "AutoFix Scout can restart the Windows taskbar and File Explorer. The screen will flicker briefly; open programs stay open."));
        if (s.Search is { } ws && ServiceDefaults.IsFault(ws.Name, ws.Status, ws.StartType))
            r.Add(new("restore_wsearch", "service.restore_default", 1,
                "AutoFix Scout can set the \"Windows Search\" service back to the way Windows normally runs it. Windows will ask for your permission.", "WSearch"));
        foreach (var p in s.Packages.OrderBy(p => p.Status.Equals("Ok", StringComparison.OrdinalIgnoreCase) ? 1 : 0).Take(4))
            r.Add(new($"reregister_{p.Name.ToLowerInvariant()}", "apps.reregister", 0,
                $"AutoFix Scout can register the Windows component \"{Friendly(p.Name)}\" again, which repairs most Start menu and taskbar problems.", p.FullName));
        r.Add(new("rebuild_search_index", "search.rebuild_index", 1,
            "AutoFix Scout can rebuild the Windows search index. Search results may be incomplete for a while afterwards. Windows will ask for your permission."));
        r.Add(new("rebuild_icon_cache", "shell.rebuild_icon_cache", 0,
            "AutoFix Scout can rebuild the stored copies of icons and thumbnails, which Windows creates again automatically."));
        r.Add(new("repair_component_store", "repair.dism", 1,
            "AutoFix Scout can ask Windows to check its own installation files and download fresh copies of any that are damaged. " +
            "This can take 10 to 20 minutes and needs an internet connection. Windows will ask for your permission.", "system"));
        r.Add(new("repair_system_files", "repair.sfc", 1,
            "AutoFix Scout can check all Windows system files and replace any that are damaged. This can take 10 to 15 minutes. Windows will ask for your permission.", "system"));
        return r;
    }

    public static string Friendly(string package) => package switch
    {
        "Microsoft.Windows.StartMenuExperienceHost" => "Start menu",
        "Microsoft.Windows.ShellExperienceHost" => "Taskbar and notifications",
        "MicrosoftWindows.Client.CBS" => "Windows shell features",
        "MicrosoftWindows.Client.Core" => "Windows shell core",
        "Microsoft.Windows.Search" => "Search",
        _ => package,
    };

    public static string? NotFixedHint(IReadOnlyList<Finding> after) =>
        "If the Start menu or taskbar still does not work, creating a new Windows user account often helps, because the problem " +
        "can be limited to one account. The person helping you can do this in \"Settings\", \"Accounts\", \"Other users\".";
}
