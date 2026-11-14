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

    /// <summary>Devices that need attention; devices the person switched off (code 22) are listed in the detail only.</summary>
    public static IReadOnlyList<DeviceProblem> DeviceFaults(HealthState s) => s.DeviceProblems.Where(d => !d.DisabledByPerson).ToList();

    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"\"{n}\""));

    private static string ServiceName(string name) => ServiceDefaults.Find(name)?.DisplayName ?? name;

    // ---- drive wear, errors and temperature (reliability counters, read through diag.read_drive_health) ----

    /// <summary>Hot: 65 C or more right now, or a recorded maximum of 85 C or more. A lifetime peak in the 70s is normal for an NVMe SSD under load (bench, 2026-11-14: 76 C), so it alone is not a problem.</summary>
    public const int WearWornOutPercent = 90, WearWearingPercent = 70, HotNowC = 65, HotMaxC = 85;

    public static bool HasUncorrectedErrors(PhysicalDriveInfo d) => d.ReadErrorsUncorrected > 0 || d.WriteErrorsUncorrected > 0;

    /// <summary>Wear of an SSD only (a hard drive reports no wear). Null = Unknown, which gives no finding.</summary>
    private static int? SsdWear(PhysicalDriveInfo d) => d.MediaType == "SSD" ? d.WearPercent : null;

    public static IEnumerable<Finding> DriveHealthFindings(HealthState s)
    {
        var worn = s.Drives.Where(d => SsdWear(d) >= WearWornOutPercent).OrderByDescending(d => d.WearPercent).FirstOrDefault();
        var wearing = s.Drives.Where(d => SsdWear(d) is >= WearWearingPercent and < WearWornOutPercent).OrderByDescending(d => d.WearPercent).FirstOrDefault();
        if (worn is not null)
            yield return F(FindingCode.HealthDriveWornOut,
                $"The SSD \"{worn.Name}\" has used about {worn.WearPercent} percent of its expected life. Please copy important files to another drive or to cloud storage now, and plan to replace the SSD.",
                $"{worn.Name} wear={worn.WearPercent}% hours={worn.PowerOnHours?.ToString() ?? "unknown"}");
        else if (wearing is not null)
            yield return F(FindingCode.HealthDriveWearing,
                $"The SSD \"{wearing.Name}\" has used about {wearing.WearPercent} percent of its expected life. It still works, but please keep a recent backup of your important files and plan to replace the SSD in the future.",
                $"{wearing.Name} wear={wearing.WearPercent}% hours={wearing.PowerOnHours?.ToString() ?? "unknown"}");
        if (s.Drives.FirstOrDefault(HasUncorrectedErrors) is { } bad)
            yield return F(FindingCode.HealthDriveErrors,
                $"The drive \"{bad.Name}\" has reported errors that it could not correct while reading or writing. Please copy important files to another drive or to cloud storage now.",
                $"{bad.Name} uncorrected read errors={bad.ReadErrorsUncorrected?.ToString() ?? "unknown"} write errors={bad.WriteErrorsUncorrected?.ToString() ?? "unknown"}");
        if (s.Drives.Where(d => d.TemperatureC >= HotNowC || d.TemperatureMaxC >= HotMaxC)
                .OrderByDescending(d => Math.Max(d.TemperatureC ?? 0, d.TemperatureMaxC ?? 0)).FirstOrDefault() is { } hot)
            yield return F(FindingCode.HealthDriveHot,
                (hot.TemperatureC >= HotNowC
                    ? $"The drive \"{hot.Name}\" is now at {hot.TemperatureC} degrees Celsius, which is too warm. "
                    : $"The drive \"{hot.Name}\" has reached {hot.TemperatureMaxC} degrees Celsius, which is too warm. ") +
                $"Please make sure the air vents are free of dust, that nothing blocks the air flow, and that the computer is in a cool place.",
                $"{hot.Name} temperature={hot.TemperatureC?.ToString() ?? "unknown"} C max={hot.TemperatureMaxC} C");
    }

    /// <summary>True when drives exist, none has reliability counters yet and the helper has not read them: the optional read is worth offering.</summary>
    public static bool OfferDriveRead(HealthState s) => !s.DriveHealthRead && s.Drives.Count > 0 && s.Drives.All(d => d.CountersUnknown);

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(HealthState s)
    {
        var f = new List<Finding>();

        var failing = s.Drives.Where(d => d.Failing).ToList();
        if (failing.Count > 0)
            f.Add(F(FindingCode.HealthDriveFailing, $"Windows reports that the drive \"{failing[0].Name}\" may fail soon. Please copy your important files to another place now.",
                string.Join("; ", failing.Select(d => $"{d.Name} ({d.MediaType}) health={d.Health} status={string.Join(",", d.OperationalStatus)} predictFail={d.PredictFailure}"))));
        else if (s.Drives.FirstOrDefault(d => d.Warning) is { } warn)
            f.Add(F(FindingCode.HealthDriveWarning, $"Windows reports a warning for the drive \"{warn.Name}\". It is a good idea to back up your important files.",
                $"{warn.Name} ({warn.MediaType}) health={warn.Health} status={string.Join(",", warn.OperationalStatus)}"));
        foreach (var d in DriveHealthFindings(s)) f.Add(d);
        if (s.TempProfile)
            f.Add(F(FindingCode.HealthTempProfile, "Windows signed you in with a temporary profile, so your desktop and files may look missing. Your files are most likely still safe in your normal profile; nothing is deleted.",
                s.TempProfileDetail ?? "temporary profile"));
        var devices = DeviceFaults(s);
        if (devices.Count > 0)
            f.Add(F(FindingCode.HealthDeviceProblem,
                $"Windows reports a problem with the device \"{devices[0].Name}\": it {DeviceErrors.Meaning(devices[0].Code)}." +
                (devices.Count > 1 ? $" {devices.Count - 1} other {(devices.Count == 2 ? "device has" : "devices have")} a problem as well." : ""),
                string.Join("; ", s.DeviceProblems.OrderBy(d => d.DisabledByPerson).Select(d => $"{d.Name} class={d.PnpClass} code={d.Code}{(d.DisabledByPerson ? " (switched off, information only)" : "")}"))));
        if (s.HardwareErrors > 0)
            f.Add(F(FindingCode.HealthHardwareErrors, "Windows has recorded errors reported by the computer's hardware (processor or memory). Software repairs cannot correct these.",
                $"WHEA errors (error level) in 14 days: {s.HardwareErrors}"));
        if (s.DiskErrors > 0)
            f.Add(F(FindingCode.HealthDiskErrors, "Windows has recorded errors while reading from or writing to the drive.",
                $"disk/NTFS errors in 14 days: {s.DiskErrors}"));
        if (s.ClockOffsetSeconds is { } off && Math.Abs(off) > ClockTolerSeconds)
            f.Add(F(FindingCode.HealthClockWrong, "The computer's clock is wrong. A wrong clock causes secure web sites, sign-in and updates to fail." +
                (s.AutoTimeOff ? " The setting \"Set time automatically\" is switched off." : ""),
                $"clock offset {off:0} s; automatic time {(s.AutoTimeOff ? "off" : "on")}"));
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
        if (BlueScreenCount(s) > 0)
            f.Add(F(FindingCode.HealthBlueScreens, $"Windows has stopped with an error screen (a \"blue screen\") {Times(BlueScreenCount(s))} in the last two weeks.{BlueScreenCause(s)}",
                $"bugchecks: {BlueScreenCount(s)}, unexpected shutdowns: {s.UnexpectedShutdowns}" +
                (s.StopCodes.Count > 0 ? "; stop codes: " + string.Join(", ", s.StopCodes.Select(c => $"{StopCodes.Hex(c.Code)}{(StopCodes.Meaning(c.Code) is { } m ? " " + m.Name : "")} x{c.Count}")) : "")));
        if (PageFileTooSmall(s))
            f.Add(F(FindingCode.HealthPageFileTooSmall, PageFileSentence(s), PageFileDetail(s)));
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
        if (s.GraphicsResets > 0)
            f.Add(F(FindingCode.HealthGraphicsResets, $"The graphics driver has stopped responding and been reset {Times(s.GraphicsResets)} in the last two weeks. This can cause black or flickering screens and closing programs.",
                $"Display 4101 and LiveKernelEvent 141/117 in 14 days: {s.GraphicsResets}"));
        if (s.Usb is { } usb)
        {
            if (usb.Churn.Count > 0)
            {
                var names = usb.Churn.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                f.Add(F(FindingCode.HealthUsbDisconnects,
                    $"The USB device \"{names[0]}\" has connected and disconnected {Times(usb.Churn[0].Count)} in the last three days." +
                    (names.Count > 1 ? $" {names.Count - 1} other USB {(names.Count == 2 ? "device has" : "devices have")} done the same." : "") +
                    " A loose cable, a weak port or an unpowered hub is a common cause.",
                    string.Join("; ", usb.Churn.Select(c => $"{c.Name} class={(c.PnpClass.Length > 0 ? c.PnpClass : "unknown")} events={c.Count}"))));
            }
            if (usb.HubErrors >= UsbHealth.ErrorThreshold)
                f.Add(F(FindingCode.HealthUsbErrors,
                    $"Windows has recorded {usb.HubErrors} errors from the USB ports or hubs in the last three days, which points to a USB device or cable that does not connect reliably.",
                    $"USB hub and controller errors and warnings in 3 days: {usb.HubErrors}; device events: {usb.PnpEvents}"));
        }
        // Recent changes are only a lead when something else is wrong; a restart that is waiting is the result of a change, not a problem.
        if (s.RecentChanges.Count > 0 && f.Any(x => x.Code != FindingCode.HealthRestartPending))
            f.Add(F(FindingCode.HealthRecentChanges, RecentChangesSentence(s),
                string.Join("; ", s.RecentChanges.Take(15).Select(c => $"{c.Kind} \"{c.Name}\" {c.WhenUtc:yyyy-MM-dd}"))));

        if (f.Count > 0) return f;
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, "AutoFix Scout could not complete the check.", string.Join("; ", s.ProbeErrors)) };
        return new[] { F(FindingCode.LooksNormal, "No common software problems were found. AutoFix Scout can still check the Windows system files for damage.",
            $"crashes={s.Crashes.Sum(c => c.Count)}, clock offset={s.ClockOffsetSeconds:0} s") };
    }

    private static string RecentChangesSentence(HealthState s)
    {
        var kinds = s.RecentChanges.Select(c => c.Kind).Distinct().ToList();
        var what = kinds.Contains(RecentChangeKind.Program) || kinds.Contains(RecentChangeKind.Update)
            ? (kinds.Contains(RecentChangeKind.Program) && kinds.Contains(RecentChangeKind.Update) ? "programs or updates" : kinds.Contains(RecentChangeKind.Program) ? "programs" : "updates")
            : "drivers";
        return $"Some {what} were installed shortly before the problems started. They may be the cause.";
    }

    private static bool HasKind(HealthState s, RecentChangeKind k) => s.RecentChanges.Any(c => c.Kind == k);

    private static void AddRecentChangeRungs(HealthState s, List<Rung> r)
    {
        if (s.RecentChanges.Where(c => c.Kind == RecentChangeKind.Program).OrderByDescending(c => c.WhenUtc).FirstOrDefault() is { } prog)
            r.Add(new("uninstall_recent_program", "ui.open_settings_page", 0,
                $"AutoFix Scout will open \"Installed apps\". The program \"{prog.Name}\" was installed recently. If you do not need it, select the three dots next to it, and select \"Uninstall\". Then restart the computer and check whether the problem remains.",
                "apps"));
        if (HasKind(s, RecentChangeKind.Update))
            r.Add(new("uninstall_recent_update", "ui.open_settings_page", 0,
                "AutoFix Scout will open \"Update history\". Please scroll down to \"Uninstall updates\", and remove the most recent update. Then restart the computer and check whether the problem remains. Windows may install the update again later.",
                "updatehistory"));
        var first = s.RecentChanges.Min(c => c.WhenUtc);
        var olderPoint = s.RestorePointsKnown == Tri.Yes && s.RestorePoints.Any(p => p < first);
        if (s.RestorePointsKnown == Tri.Unknown || olderPoint)
            r.Add(new("system_restore", "ui.human_step", 0,
                "As a last resort, System Restore can return Windows to an earlier time. Please press the Windows key and R together, type \"rstrui.exe\", and select \"OK\". " +
                "Choose a restore point from before the problems started, and follow the steps. Your documents and photos are not removed, but programs installed after that time are. AutoFix Scout does not start System Restore."));
    }

    /// <summary>The most frequent stop code, explained; empty when no code could be read or it is not a common one.</summary>
    private static string BlueScreenCause(HealthState s)
    {
        var total = BlueScreenCount(s);
        var text = "";
        if (s.StopCodes.Count > 0)
        {
            var top = s.StopCodes[0];
            text = StopCodes.Meaning(top.Code) is { } m
                ? $" The most frequent stop code was {StopCodes.Hex(top.Code)} ({m.Name}). {m.Explanation}"
                : $" The most frequent stop code was {StopCodes.Hex(top.Code)}.";
        }
        return text + BlueScreenDriverSentence(s, total) + BlueScreenRecurrenceSentence(s, total);
    }

    /// <summary>The module Windows blamed most often, as a sentence; empty when no report named one.</summary>
    public static string BlueScreenDriverSentence(HealthState s, int total)
    {
        if (s.BlueScreenModules.Count == 0) return "";
        var top = s.BlueScreenModules[0];
        if (BlueScreenReports.NameModule(top.Module) is not { } d) return "";
        if (d.IsWindowsItself)
            return $" Windows named its own core file \"{d.Module}\", which does not point to one specific driver.";
        var what = d.Display.Equals(d.Module, StringComparison.OrdinalIgnoreCase) ? $"the file \"{d.Module}\"" : $"the {d.Display} (\"{d.Module}\")";
        var when = total <= 1 ? "" : top.Count >= total ? "Each time, " : $"In {top.Count} of these, ";
        return when.Length == 0 ? $" Windows named {what} as the cause." : $" {when}Windows named {what} as the cause.";
    }

    /// <summary>Whether the blue screens keep happening: how many days, and whether the stop code is always the same.</summary>
    public static string BlueScreenRecurrenceSentence(HealthState s, int total)
    {
        var r = BlueScreenReports.Recur(total, s.BlueScreenTimes, s.StopCodes);
        if (total <= 1) return " It has happened only once so far.";
        if (r.KeepsHappening)
            return (r.Days >= 2 ? $" The problem keeps happening, on {r.Days} different days." : " The problem keeps happening.")
                + (r.SameCodeEachTime ? " The stop code was the same every time, which points to one cause." : "");
        return " It happened more than once, but on one day only.";
    }

    /// <summary>Blue screens from the System log; when it has none, crashes recorded with a stop code by Kernel-Power 41.</summary>
    public static int BlueScreenCount(HealthState s) => s.BlueScreens > 0 ? s.BlueScreens : s.KernelPowerBugchecks;

    /// <summary>The cause groups of the most frequent stop codes (usual cause and second possibility), plus the group of the named driver.</summary>
    private static List<CauseGroup> BlueScreenCauseGroups(HealthState s)
    {
        var g = new List<CauseGroup>();
        foreach (var c in s.StopCodes.Take(3))
            if (BlueScreenReports.CausesOf(c.Code) is { } x)
            {
                g.Add(x.Primary);
                if (x.Secondary is { } sec) g.Add(sec);
            }
            else if (StopCodes.Meaning(c.Code)?.Category is { } cat)
                g.Add(cat switch { StopCategory.Storage => CauseGroup.Disk, StopCategory.Memory => CauseGroup.Memory, StopCategory.Graphics => CauseGroup.Graphics, StopCategory.Driver => CauseGroup.Driver, _ => CauseGroup.Unknown });
        if (s.BlueScreenModules.Count > 0 && BlueScreenReports.NameModule(s.BlueScreenModules[0].Module) is { IsWindowsItself: false } d)
            g.Add(d.Group == CauseGroup.Unknown ? CauseGroup.Driver : d.Group);
        return g.Distinct().ToList();
    }

    public const long PageFileMinimumMb = 1024;

    /// <summary>No page file at all, or less than 1 GB, while Windows is not managing it (fail closed when it could not be read).</summary>
    public static bool PageFileTooSmall(HealthState s) =>
        s.PageFile is { Automatic: false } p && (!p.Exists || p.AllocatedMb < PageFileMinimumMb);

    private static string PageFileSentence(HealthState s)
    {
        var p = s.PageFile!;
        var what = !p.Exists ? "The page file (virtual memory) is switched off." : $"The page file (virtual memory) is only {p.AllocatedMb} MB.";
        var evidence = s.LowMemoryEvents > 0 ? $" Windows has also warned about low virtual memory {Times(s.LowMemoryEvents)} in the last two weeks." : "";
        return what + " Programs can close or fail to start when the computer runs short of memory." + evidence;
    }

    private static string PageFileDetail(HealthState s)
    {
        var p = s.PageFile!;
        return $"automatic={p.Automatic}; RAM {p.TotalRamMb} MB; allocated {p.AllocatedMb} MB; " +
               $"settings: {(p.Settings.Count == 0 ? "none" : string.Join(", ", p.Settings.Select(x => $"{x.Name} {x.InitialMb}-{x.MaximumMb} MB")))}; " +
               $"usage: {(p.Usage.Count == 0 ? "none" : string.Join(", ", p.Usage.Select(x => $"{x.Name} {x.AllocatedMb} MB (in use {x.CurrentUsageMb} MB)")))}; low virtual memory events: {s.LowMemoryEvents}";
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

        if (codes.Contains(FindingCode.HealthTempProfile))
        {
            r.Add(new("restart_sign_in_again", "ui.human_step", 0,
                "Please save your work and restart the computer: select \"Start\", then \"Power\", then \"Restart\". Then sign in again as usual. A restart often brings back the normal profile."));
            r.Add(new("profile_helper_steps", "ui.human_step", 0,
                "If the temporary profile appears again after a restart, a helper can usually correct it. In the registry, under \"ProfileList\", the entry for this account ends in \".bak\", and Windows no longer finds the normal one. " +
                "The helper removes \".bak\" from that name and sets the state value to zero. AutoFix Scout does not change the registry or any profile; please ask a person who is comfortable with this to do it, and keep a backup of your files first."));
        }
        if (codes.Contains(FindingCode.HealthDeviceProblem))
            AddDeviceRungs(s, r);
        if (codes.Contains(FindingCode.HealthDriveFailing) || codes.Contains(FindingCode.HealthDriveWarning) || codes.Contains(FindingCode.HealthDriveWornOut) || codes.Contains(FindingCode.HealthDriveWearing) || codes.Contains(FindingCode.HealthDriveErrors))
            r.Add(new("back_up_files", "ui.open_settings_page", 0,
                "Please copy your important files (documents, photos) to a USB drive or to OneDrive before anything else. " +
                "AutoFix Scout will open the \"Windows Backup\" settings, which can do this for you.", "backup"));
        if (codes.Contains(FindingCode.HealthDriveHot))
            r.Add(new("drive_cooling", "ui.human_step", 0,
                "Please make sure the air vents and fans are free of dust and that nothing blocks them. A laptop should stand on a hard, flat surface, not on a bed or a blanket. " +
                "If the drive is in a desktop computer, a helper can check that the case fans work."));
        // Optional and never automatic: the wear and error counters of a drive can be read by administrators only.
        if (OfferDriveRead(s)) r.Add(ReadRungs.DriveHealth());
        if (codes.Contains(FindingCode.HealthClockWrong) && s.AutoTimeOff)
            r.Add(new("auto_time_on", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Date and time\" settings. Please switch on \"Set time automatically\", and check that the time zone is correct.", "dateandtime"));
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
        var causes = codes.Contains(FindingCode.HealthBlueScreens) ? BlueScreenCauseGroups(s) : new List<CauseGroup>();
        var named = codes.Contains(FindingCode.HealthBlueScreens) && s.BlueScreenModules.Count > 0 ? BlueScreenReports.NameModule(s.BlueScreenModules[0].Module) : null;
        // Optional and never automatic: the reports that name the driver are readable by administrators only.
        if (codes.Contains(FindingCode.HealthBlueScreens) && s.BlueScreenModules.Count == 0 && !s.CrashReportsRead) r.Add(ReadRungs.CrashReports());
        if (codes.Contains(FindingCode.HealthDiskErrors) || causes.Contains(CauseGroup.Disk))
            r.Add(Admin("scan_disk", "disk.scan", "AutoFix Scout can ask Windows to scan the drive for file system errors while you keep working.", "C"));
        if (causes.Contains(CauseGroup.Memory))
            r.Add(new("memory_diagnostic", "ui.human_step", 0,
                "Faulty memory (RAM) can cause these blue screens. Please save your work, then press the Windows key and R together, type \"mdsched.exe\", and select \"OK\". " +
                "Choose \"Restart now and check for problems\". The computer restarts, and the Windows Memory Diagnostic test takes several minutes. " +
                "Windows shows the result after it starts again. AutoFix Scout does not start the Windows Memory Diagnostic."));
        if (named is { IsWindowsItself: false })
            r.Add(new("bluescreen_named_driver", "ui.human_step", 0,
                named.Display.Equals(named.Module, StringComparison.OrdinalIgnoreCase)
                    ? $"Windows named the file \"{named.Module}\" as the cause. Please find out which program or device it belongs to, then update or reinstall that program or driver. If it belongs to a program that is no longer needed, please uninstall the program."
                    : $"Windows named the {named.Display} (\"{named.Module}\") as the cause. Please update that driver, or reinstall it if it is already up to date. The newest version is available through Windows Update or from the web site of its maker."));
        if (causes.Contains(CauseGroup.Graphics) || causes.Contains(CauseGroup.Driver))
            r.Add(new("bluescreen_driver_updates", "ui.open_settings_page", 0,
                "AutoFix Scout will open \"Windows Update\". Please select \"Check for updates\", then \"Advanced options\", then \"Optional updates\", then \"Driver updates\", and install any driver offered" +
                (causes.Contains(CauseGroup.Graphics) ? ", in particular a display driver." : "."), "windowsupdate"));
        if (causes.Contains(CauseGroup.Driver))
            r.Add(new("bluescreen_driver_maker", "ui.human_step", 0,
                "If Windows Update offers no driver, please open the support web site of the computer's maker, search for the model, and download the newest drivers for Windows, starting with the chipset, storage and network drivers. " +
                "Run the installers, then restart the computer. AutoFix Scout does not install drivers from other sources."));
        if (causes.Contains(CauseGroup.Graphics))
            r.Add(GraphicsTriage.CleanDriverRung());
        if (causes.Contains(CauseGroup.Hardware))
        {
            r.Add(PerfTriage.CoolingRung());
            r.Add(new("bluescreen_hardware_settings", "ui.human_step", 0,
                "The computer's hardware reported an error that could not be corrected. If the processor, memory or graphics card has been overclocked, please return it to the standard settings. " +
                "Please also use the charger that came with the computer, and restart it after the change. If the blue screens continue, a repair shop should check the computer, because a part may be failing."));
        }
        if (codes.Contains(FindingCode.HealthPageFileTooSmall))
        {
            r.Add(Admin("pagefile_automatic", "system.pagefile_auto",
                "AutoFix Scout can let Windows manage the size of the page file (virtual memory) itself. The current settings are saved first. The change takes effect after the computer restarts."));
            r.Add(new("restart_after_pagefile", "ui.human_step", 0,
                "Please save your work and restart the computer: select \"Start\", then \"Power\", then \"Restart\". The new page file size is used after the restart."));
        }
        if (codes.Contains(FindingCode.HealthGraphicsResets))
        {
            r.Add(new("graphics_reset_updates", "ui.open_settings_page", 0,
                "AutoFix Scout will open \"Windows Update\". Please select \"Check for updates\", then \"Advanced options\", then \"Optional updates\", then \"Driver updates\", and install any display driver offered.",
                "windowsupdate"));
            r.Add(new("graphics_reset_maker", "ui.human_step", 0,
                "If Windows Update offers no display driver, please get the newest graphics driver from the support web site of the computer's maker, or from the web site of NVIDIA, AMD or Intel, depending on the graphics card. " +
                "Run the installer, then restart the computer. AutoFix Scout does not install drivers from other sources."));
        }
        if (codes.Contains(FindingCode.HealthUsbDisconnects) || codes.Contains(FindingCode.HealthUsbErrors))
            AddUsbRungs(s, r);
        if (codes.Contains(FindingCode.HealthRecentChanges))
            AddRecentChangeRungs(s, r);
        if (systemRepair) { r.Add(Dism); r.Add(Sfc); }
        if (codes.Contains(FindingCode.HealthRestartPending))
            r.Add(new("restart_pc", "ui.human_step", 0,
                "Windows needs a restart to finish installing changes. Please save your work, then select \"Start\", then \"Power\", then \"Restart\". After the restart, please open AutoFix Scout again."));
        return r;
    }

    /// <summary>Human steps, the selective suspend setting (guided, never changed by AutoFix Scout), a driver update, and a hardware rescan.</summary>
    private static void AddUsbRungs(HealthState s, List<Rung> r)
    {
        r.Add(new("usb_port_cable", "ui.human_step", 0,
            "Please unplug the USB device, wait ten seconds, and plug it into a different USB port directly on the computer. Avoid USB hubs that have no power supply of their own, " +
            "and try a different cable, because a damaged or loose cable is a common cause of a device that keeps disconnecting."));
        if (s.Usb?.SelectiveSuspendOn == true)
            r.Add(new("usb_selective_suspend_off", "ui.human_step", 0,
                "USB selective suspend lets Windows switch off a USB port to save power, which can make a device disconnect. To turn it off, open the Windows search, type \"Control Panel\", open \"Hardware and Sound\", \"Power Options\", " +
                "select \"Change plan settings\" for the plan in use, then \"Change advanced power settings\". Open \"USB settings\", then \"USB selective suspend setting\", and set it to \"Disabled\". Windows may ask for your permission."));
        r.Add(new("usb_driver_updates", "ui.open_settings_page", 0,
            "AutoFix Scout will open \"Windows Update\". Please select \"Check for updates\", then \"Advanced options\", then \"Optional updates\", and install any chipset or USB driver offered.",
            "windowsupdate"));
        if (!r.Any(x => x.Id == "rescan"))
            r.Add(new("rescan", "device.rescan", 1,
                "AutoFix Scout can ask Windows to search for hardware changes again. " +
                "This is the same as \"Scan for hardware changes\" in \"Device Manager\". Windows will ask for your permission.", "devices"));
    }

    /// <summary>Scan for hardware changes (any class, nothing is changed), then guided steps only: no driver of any device is switched or changed.</summary>
    private static void AddDeviceRungs(HealthState s, List<Rung> r)
    {
        var devices = DeviceFaults(s);
        r.Add(new("rescan", "device.rescan", 1,
            "A device does not work properly. AutoFix Scout can ask Windows to search for hardware changes again. " +
            "This is the same as \"Scan for hardware changes\" in \"Device Manager\". Windows will ask for your permission.", "devices"));
        if (devices.Any(d => d.NeedsDriver))
        {
            r.Add(new("device_driver_update", "ui.open_settings_page", 0,
                "AutoFix Scout will open \"Windows Update\". Please select \"Check for updates\", then \"Advanced options\", then \"Optional updates\", and install any driver offered for the device.",
                "windowsupdate"));
            r.Add(new("device_driver_manufacturer", "ui.human_step", 0,
                "If Windows Update offers no driver, please open the support web site of the computer's maker (or of the device's maker), search for the model, and download the driver for Windows. " +
                "Run the installer, then restart the computer. AutoFix Scout does not install drivers from other sources."));
        }
        if (devices.Any(d => d.Code == 43 && d.IsUsb))
            r.Add(new("device_replug", "ui.human_step", 0,
                "A USB device reported a problem to Windows. Please unplug it, wait ten seconds, and plug it into a different USB port directly on the computer, not through a hub."));
    }

    public static string RuntimeArch() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>Hardware and disk errors are the only findings that point at hardware.</summary>
    public static bool ProbablyHardware(HealthState s) => s.HardwareErrors > 0 || s.DiskErrors >= 5 || s.GraphicsResets >= 20 || s.Drives.Any(d => d.Failing) || s.StopCodes.Any(c => StopCodes.IsHardware(c.Code));

    public static string? NotFixedHint(IReadOnlyList<Finding> after)
    {
        var codes = after.Select(x => x.Code).ToHashSet();
        if (codes.Contains(FindingCode.HealthDriveFailing))
            return "Windows still reports that a drive may fail. Please back up your files, and ask a repair service to replace the drive.";
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
        FindingCode.HealthPageFileTooSmall,
    };

    public static bool Resolved(IReadOnlyList<Finding> before, IReadOnlyList<Finding> after) =>
        !after.Any(a => a.Code == FindingCode.ProbeIncomplete) &&
        !before.Select(b => b.Code).Intersect(Rechecked).Intersect(after.Select(a => a.Code)).Any();

    // ---- internet: the software causes behind "connected, but web sites do not work" ----

    private static readonly FindingCode[] NetworkCodes =
    {
        FindingCode.HealthClockWrong, FindingCode.HealthDeadProxy, FindingCode.HealthDeadSystemProxy,
        FindingCode.HealthHostsHijacked, FindingCode.HealthWinsockAddOn,
    };

    private static readonly string[] NetworkActions =
    {
        "time.resync", "network.reset_user_proxy", "network.reset_winhttp_proxy", "network.clean_hosts", "network.reset_winsock",
    };

    private static bool IsInternetService(string name) => ServiceDefaults.Find(name)?.Area == "internet";

    /// <summary>Only the health findings and steps that can stop web sites and apps from connecting.</summary>
    public static (IReadOnlyList<Finding> Findings, IReadOnlyList<Rung> Ladder) NetworkExtras(HealthState s)
    {
        var all = Evaluate(s);
        var findings = all.Where(x => NetworkCodes.Contains(x.Code)).ToList();
        var services = ServiceFaults(s).Where(x => IsInternetService(x.Name)).ToList();
        if (services.Count > 0)
            findings.Add(F(FindingCode.HealthServiceOff,
                $"A part of Windows that internet connections need is switched off or stopped: {Names(services.Select(x => ServiceName(x.Name)))}.",
                string.Join(", ", services.Select(x => $"{x.Name}:{x.Status}/{x.StartType}"))));
        var ladder = Ladder(s, findings).Where(r =>
            NetworkActions.Contains(r.ActionId) || r.Id == "restart_after_reset" ||
            (r.ActionId == "service.restore_default" && IsInternetService(r.Arg ?? ""))).ToList();
        return (findings, ladder);
    }

    /// <summary>
    /// Adds the health findings to the internet check, first: they are concrete causes with harmless fixes, and a dead
    /// proxy also makes the connection test itself fail, so "the internet is not responding" can be its symptom.
    /// </summary>
    public static Diagnosis CombineNetwork(Diagnosis network, HealthState health)
    {
        var (f, l) = NetworkExtras(health);
        if (f.Count == 0) return network;
        var rest = network.Findings.Where(x => x.Code != FindingCode.LooksNormal);
        var findings = f.Concat(rest).ToList();
        var ladder = l.Concat(network.Ladder).ToList();
        return network with { Findings = findings, Ladder = ladder.DistinctBy(r => r.Id).ToList() };
    }
}
