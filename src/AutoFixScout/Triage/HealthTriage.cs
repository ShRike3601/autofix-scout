using AutoFixScout.Probes;
using AutoFixScout.Repair;

namespace AutoFixScout.Triage;

/// <summary>
/// "Something else is wrong": a broad software check-up. Pure logic.
/// Unlike the single-device areas, a part of the check that could not be read does not hide what the other parts
/// found; it only prevents a "looks normal" verdict (fail closed).
/// Every repair keeps a way back: settings are backed up before they change, hosts lines are commented out (not
/// deleted), Windows Update folders are renamed (not deleted), and system file repair only replaces damaged Windows files.
/// </summary>
public static class HealthTriage
{
    public const double ClockTolerSeconds = 180;

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    public static IReadOnlyList<CrashInfo> ShellCrashes(HealthState s) => s.Crashes.Where(c => CrashCauses.IsShell(c.App)).ToList();

    /// <summary>
    /// Programs (not part of Windows' shell) that closed unexpectedly at least twice. The program the person picked
    /// comes first and counts even after a single crash.
    /// </summary>
    public static IReadOnlyList<CrashInfo> ProgramCrashes(HealthState s) =>
        s.Crashes.Where(c => !CrashCauses.IsShell(c.App) && (c.Count >= 2 || IsChosen(s, c)))
            .OrderByDescending(c => IsChosen(s, c)).ToList();

    private static bool IsChosen(HealthState s, CrashInfo c) =>
        s.ChosenApp is { } a && c.App.Equals(a, StringComparison.OrdinalIgnoreCase);

    /// <summary>Programs to offer in "A program keeps closing", most frequent first (shell excluded).</summary>
    public static IReadOnlyList<CrashInfo> CrashCandidates(HealthState s) =>
        s.Crashes.Where(c => !CrashCauses.IsShell(c.App)).GroupBy(c => c.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Count).First()).Take(3).ToList();

    public static IReadOnlyList<UpdateServiceInfo> ServiceFaults(HealthState s) =>
        s.Services.Where(x => ServiceDefaults.IsFault(x.Name, x.Status, x.StartType)).ToList();

    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"\"{n}\""));

    private static string ServiceName(string name) => ServiceDefaults.Find(name)?.DisplayName ?? name;

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(HealthState s)
    {
        var f = new List<Finding>();

        if (s.HardwareErrors > 0)
            f.Add(F(FindingCode.HealthHardwareErrors, "Windows has recorded errors reported by the computer's hardware (processor or memory). Software repairs cannot correct these.",
                $"WHEA errors (error level) in 14 days: {s.HardwareErrors}"));
        if (s.DiskErrors > 0)
            f.Add(F(FindingCode.HealthDiskErrors, "Windows has recorded errors while reading from or writing to the drive.",
                $"disk/NTFS errors in 14 days: {s.DiskErrors}"));
        if (s.ClockOffsetSeconds is { } off && Math.Abs(off) > ClockTolerSeconds)
            f.Add(F(FindingCode.HealthClockWrong, "The computer's clock is wrong. A wrong clock causes secure web sites, sign-in and updates to fail.",
                $"clock offset {off:0} s"));
        if (s.UserProxyReachable == Tri.No)
            f.Add(F(FindingCode.HealthDeadProxy, "Your internet settings send web traffic through a proxy server that does not respond. Web sites and apps may not load.",
                s.UserProxyEnabled ? $"proxy {s.UserProxyServer} unreachable" : $"setup script {s.AutoConfigUrl} unreachable"));
        if (s.WinHttpProxy is not null && s.WinHttpProxyReachable == Tri.No)
            f.Add(F(FindingCode.HealthDeadSystemProxy, "Windows is set to use a proxy server for its own connections, and that server does not respond. Updates and the Microsoft Store may fail.",
                $"WinHTTP proxy {s.WinHttpProxy} unreachable"));
        if (s.HostsHijacks.Count > 0)
            f.Add(F(FindingCode.HealthHostsHijacked, "The hosts file redirects or blocks well-known web sites. Unwanted software often does this.",
                string.Join("; ", s.HostsHijacks.Select(h => $"{h.Host} -> {h.Ip}"))));
        var services = ServiceFaults(s);
        if (services.Count > 0)
            f.Add(F(FindingCode.HealthServiceOff, $"A part of Windows that should be running is switched off or stopped: {Names(services.Select(x => ServiceName(x.Name)))}.",
                string.Join(", ", services.Select(x => $"{x.Name}:{x.Status}/{x.StartType}"))));
        if (s.MissingPathEntries.Count > 0)
            f.Add(F(FindingCode.HealthPathDamaged, "A Windows setting that tells programs where Windows' own tools are has been damaged. Programs and installers may fail to start.",
                "missing from system PATH: " + string.Join("; ", s.MissingPathEntries)));
        if (s.TempFolderMissing)
            f.Add(F(FindingCode.HealthTempMissing, "The folder for temporary files is missing. Installers and many programs need it.", "TEMP folder does not exist"));
        if (s.ThirdPartyWinsock.Count > 0)
            f.Add(F(FindingCode.HealthWinsockAddOn, "A program has added itself to the network functions of Windows. This can stop web sites or apps from connecting.",
                string.Join("; ", s.ThirdPartyWinsock)));
        if (s.BrokenStoreApps.Count > 0)
            f.Add(F(FindingCode.HealthStoreAppsDamaged, $"Some apps from the Microsoft Store are damaged: {Names(s.BrokenStoreApps.Select(a => a.Name))}.",
                string.Join("; ", s.BrokenStoreApps.Select(a => $"{a.FullName}:{a.Status}"))));
        var shell = ShellCrashes(s);
        if (shell.Count > 0)
            f.Add(F(FindingCode.HealthShellCrashing, "Parts of Windows itself, such as the taskbar, the Start menu or search, have closed unexpectedly.",
                string.Join("; ", shell.Select(c => $"{c.App} x{c.Count} ({c.Module}, {c.ExceptionCode})"))));
        if (s.BlueScreens > 0)
            f.Add(F(FindingCode.HealthBlueScreens, $"Windows has stopped with an error screen (a \"blue screen\") {Times(s.BlueScreens)} in the last two weeks.",
                $"bugchecks: {s.BlueScreens}, unexpected shutdowns: {s.UnexpectedShutdowns}"));
        var programs = ProgramCrashes(s);
        if (programs.Count > 0)
            f.Add(F(FindingCode.HealthProgramCrashing, $"\"{programs[0].App}\" has closed unexpectedly {Times(programs[0].Count)} in the last two weeks. {CauseSentence(programs[0].Cause)}",
                string.Join("; ", programs.Take(5).Select(c => $"{c.App} x{c.Count} module={c.Module} code={c.ExceptionCode} cause={c.Cause}"))));
        if (s.RuntimeErrors.Count > 0)
            f.Add(F(FindingCode.HealthRuntimeMissing, "Some programs could not start because a Microsoft component they need is missing or damaged.",
                string.Join("; ", s.RuntimeErrors)));
        if (s.FailingServices.Count > 0)
            f.Add(F(FindingCode.HealthServicesFailing, $"Some background services keep failing: {Names(s.FailingServices)}.",
                string.Join("; ", s.FailingServices)));
        if (s.UpdateFailures >= 2)
            f.Add(F(FindingCode.HealthUpdatesFailing, $"Windows Update has failed to install updates {Times(s.UpdateFailures)} in the last two weeks.",
                $"WindowsUpdateClient event 20: {s.UpdateFailures}"));
        if (s.RebootPending == Tri.Yes)
            f.Add(F(FindingCode.HealthRestartPending, "Windows is waiting for a restart to finish installing changes.", "RebootRequired/RebootPending key present"));

        if (f.Count > 0) return f;
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, "AutoFix Scout could not complete the check.", string.Join("; ", s.ProbeErrors)) };
        return new[] { F(FindingCode.LooksNormal, "No common software problems were found. AutoFix Scout can still check the Windows system files for damage.",
            $"crashes={s.Crashes.Sum(c => c.Count)}, clock offset={s.ClockOffsetSeconds:0} s") };
    }

    private static string Times(int n) => n == 1 ? "once" : n == 2 ? "twice" : $"{n} times";

    public static string CauseSentence(CrashCause c) => c switch
    {
        CrashCause.Runtime => "The cause is most likely a damaged Microsoft Visual C++ component, which AutoFix Scout can reinstall.",
        CrashCause.MissingFile => "The program could not find a file it needs.",
        CrashCause.DotNet => "The cause is most likely in the .NET components the program uses, or in the program itself.",
        CrashCause.Graphics => "The cause is most likely the graphics driver.",
        CrashCause.WindowsFiles => "The error occurred in a Windows file, which can mean that Windows system files are damaged.",
        CrashCause.Program => "The error occurred in the program's own files.",
        _ => "The cause could not be determined from the error record.",
    };

    private static Rung Admin(string id, string action, string say, string arg = "system") =>
        new(id, action, 1, say + " Windows will ask for your permission.", arg);

    private static readonly Rung Dism = Admin("repair_component_store", "repair.dism",
        "AutoFix Scout can ask Windows to check its own installation files and download fresh copies of any that are damaged. " +
        "This can take 10 to 20 minutes and needs an internet connection.");
    private static readonly Rung Sfc = Admin("repair_system_files", "repair.sfc",
        "AutoFix Scout can check all Windows system files and replace any that are damaged. This can take 10 to 15 minutes.");

    /// <summary>Least risky first; each kind of problem gets its own steps, then system file repair once at the end where it helps.</summary>
    public static IReadOnlyList<Rung> Ladder(HealthState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var r = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return r;
        var systemRepair = codes.Contains(FindingCode.LooksNormal);

        if (codes.Contains(FindingCode.HealthClockWrong))
            r.Add(Admin("sync_clock", "time.resync", "AutoFix Scout can set the clock from Microsoft's internet time server."));
        if (codes.Contains(FindingCode.HealthDeadProxy))
            r.Add(new("remove_proxy", "network.reset_user_proxy", 1,
                "AutoFix Scout can switch off the proxy server in your internet settings. The previous setting is saved, so the person helping you can restore it.", "user"));
        if (codes.Contains(FindingCode.HealthDeadSystemProxy))
            r.Add(Admin("remove_system_proxy", "network.reset_winhttp_proxy", "AutoFix Scout can set Windows to connect directly instead of through the proxy server. The previous setting is saved."));
        if (codes.Contains(FindingCode.HealthHostsHijacked))
            r.Add(Admin("clean_hosts", "network.clean_hosts",
                "AutoFix Scout can switch off the hosts file entries that redirect or block well-known web sites. A copy of the file is saved first, and the entries are marked, not deleted."));
        foreach (var svc in ServiceFaults(s))
            r.Add(Admin($"restore_{svc.Name.ToLowerInvariant()}", "service.restore_default",
                $"AutoFix Scout can set the \"{ServiceName(svc.Name)}\" service back to the way Windows normally runs it.", svc.Name));
        if (codes.Contains(FindingCode.HealthPathDamaged))
            r.Add(Admin("repair_path", "system.repair_path", "AutoFix Scout can add the missing Windows folders back to this setting. Nothing else in the setting is changed, and the previous value is saved."));
        if (codes.Contains(FindingCode.HealthTempMissing))
            r.Add(new("create_temp", "system.repair_temp", 0, "AutoFix Scout can create the missing folder for temporary files.", "user"));
        if (codes.Contains(FindingCode.HealthWinsockAddOn))
        {
            r.Add(Admin("reset_winsock", "network.reset_winsock",
                "AutoFix Scout can reset the network functions of Windows to their original state. The computer must restart afterwards. A program that added itself may need to be reinstalled."));
            r.Add(new("restart_after_reset", "ui.human_step", 0,
                "Please save your work and restart the computer: select \"Start\", then \"Power\", then \"Restart\". After the restart, please open AutoFix Scout again."));
        }
        foreach (var app in s.BrokenStoreApps.Take(5))
            r.Add(new($"reregister_{app.Name.ToLowerInvariant()}", "apps.reregister", 0,
                $"AutoFix Scout can ask Windows to repair the app \"{app.Name}\" by registering it again. Your data in the app is kept.", app.FullName));
        if (codes.Contains(FindingCode.HealthShellCrashing))
        {
            r.Add(new("restart_explorer", "shell.restart_explorer", 0,
                "AutoFix Scout can restart the Windows taskbar and File Explorer. The screen will flicker briefly; open programs stay open."));
            r.Add(new("rebuild_icon_cache", "shell.rebuild_icon_cache", 0,
                "AutoFix Scout can rebuild the stored copies of icons and thumbnails, which Windows creates again automatically."));
            r.Add(Admin("rebuild_search_index", "search.rebuild_index",
                "AutoFix Scout can rebuild the Windows search index. Search results may be incomplete for a while afterwards."));
            systemRepair = true;
        }
        if (ProgramCrashes(s).FirstOrDefault() is { } crash)
        {
            switch (crash.Cause)
            {
                case CrashCause.Runtime:
                case CrashCause.MissingFile:
                    r.Add(Admin("install_vc_runtime", "runtime.install_vcredist",
                        "AutoFix Scout can download the Microsoft Visual C++ components from Microsoft and reinstall them. The download is checked for Microsoft's signature before it runs.",
                        RuntimeArch()));
                    break;
                case CrashCause.Graphics:
                    r.Add(new("graphics_driver", "ui.open_settings_page", 0,
                        "AutoFix Scout will open \"Windows Update\". Please select \"Check for updates\", then \"Advanced options\", then \"Optional updates\", and install any display driver offered.",
                        "windowsupdate"));
                    break;
                case CrashCause.WindowsFiles:
                case CrashCause.DotNet:
                case CrashCause.Unknown:
                    systemRepair = true;
                    break;
            }
            r.Add(new("repair_program", "ui.open_settings_page", 0,
                $"AutoFix Scout will open \"Installed apps\". Please find \"{crash.App.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)}\", select the three dots next to it, and select \"Modify\", \"Repair\" or \"Advanced options\" and then \"Repair\". If none of these is offered, uninstalling and installing the program again usually helps.",
                "apps"));
        }
        if (codes.Contains(FindingCode.HealthRuntimeMissing) && !r.Any(x => x.ActionId == "runtime.install_vcredist"))
            r.Add(Admin("install_vc_runtime", "runtime.install_vcredist",
                "AutoFix Scout can download the Microsoft Visual C++ components from Microsoft and reinstall them. The download is checked for Microsoft's signature before it runs.",
                RuntimeArch()));
        if (codes.Contains(FindingCode.HealthUpdatesFailing))
        {
            r.Add(Admin("reset_update_components", "update.reset_components",
                "AutoFix Scout can reset the Windows Update download folders. The old folders are renamed, not deleted, and Windows Update downloads what it needs again."));
            systemRepair = true;
        }
        if (codes.Contains(FindingCode.HealthStoreAppsDamaged) || codes.Contains(FindingCode.HealthServicesFailing) || codes.Contains(FindingCode.HealthBlueScreens))
            systemRepair = true;
        if (codes.Contains(FindingCode.HealthDiskErrors))
            r.Add(Admin("scan_disk", "disk.scan", "AutoFix Scout can ask Windows to scan the drive for file system errors while you keep working.", "C"));
        if (systemRepair) { r.Add(Dism); r.Add(Sfc); }
        if (codes.Contains(FindingCode.HealthRestartPending))
            r.Add(new("restart_pc", "ui.human_step", 0,
                "Windows needs a restart to finish installing changes. Please save your work, then select \"Start\", then \"Power\", then \"Restart\". After the restart, please open AutoFix Scout again."));
        return r;
    }

    public static string RuntimeArch() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>Hardware and disk errors are the only findings that point at hardware.</summary>
    public static bool ProbablyHardware(HealthState s) => s.HardwareErrors > 0 || s.DiskErrors >= 5;

    public static string? NotFixedHint(IReadOnlyList<Finding> after)
    {
        var codes = after.Select(x => x.Code).ToHashSet();
        if (codes.Contains(FindingCode.HealthHardwareErrors) || codes.Contains(FindingCode.HealthDiskErrors))
            return "Windows has recorded hardware or drive errors. Please back up your files, and ask a repair service to test the computer's memory and drive.";
        if (codes.Contains(FindingCode.HealthRestartPending))
            return "Some repairs finish only after a restart. Please restart the computer and check whether the problem remains.";
        return null;
    }

    /// <summary>
    /// The automatic re-check: every fixable problem found before is gone. Crash, blue-screen and update-failure history
    /// stays in the event log after a repair, so those are judged by the person instead.
    /// </summary>
    public static readonly FindingCode[] Rechecked =
    {
        FindingCode.HealthClockWrong, FindingCode.HealthDeadProxy, FindingCode.HealthDeadSystemProxy, FindingCode.HealthHostsHijacked,
        FindingCode.HealthServiceOff, FindingCode.HealthPathDamaged, FindingCode.HealthTempMissing, FindingCode.HealthStoreAppsDamaged,
    };

    public static bool Resolved(IReadOnlyList<Finding> before, IReadOnlyList<Finding> after) =>
        !after.Any(a => a.Code == FindingCode.ProbeIncomplete) &&
        !before.Select(b => b.Code).Intersect(Rechecked).Intersect(after.Select(a => a.Code)).Any();
}
