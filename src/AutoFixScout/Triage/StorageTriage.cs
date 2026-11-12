using System.Globalization;
using AutoFixScout.Repair;

namespace AutoFixScout.Triage;

public enum SpaceLevel { Unknown, Ok, Low, Critical }

/// <summary>
/// "My computer is slow or full". Pure local logic. Disks and storage controllers are only reported, never changed:
/// a bad change there can stop Windows from starting.
/// </summary>
public static class StorageTriage
{
    private const long GB = 1L << 30, MB = 1L << 20;
    public const long LowBytes = 10 * GB, CriticalBytes = 2 * GB;
    public const double LowPercent = 10, CriticalPercent = 3;
    /// <summary>Old temporary files plus the Recycle Bin above this are worth a finding on their own.</summary>
    public const long LargeTempBytes = 2 * GB;
    /// <summary>Below this a clean-up rung is not worth offering.</summary>
    public const long WorthCleaning = 50 * MB;
    public const int ManyStartupApps = 12;
    public const int HighMemoryPercent = 90;
    public static readonly TimeSpan LongUptime = TimeSpan.FromDays(7);
    /// <summary>One program (not part of Windows) above this share of all processor cores, or of installed memory, is a finding.</summary>
    public const double HeavyCpuPercent = 40, HeavyMemoryPercent = 25;

    private static string Pct(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The busiest program above the processor threshold, if any.</summary>
    public static ProcessUsage? HeavyCpu(StorageState s) =>
        s.TopProcesses?.Where(p => p.CpuPercent > HeavyCpuPercent).MaxBy(p => p.CpuPercent);

    /// <summary>The largest program above the memory threshold, unless it is already the processor finding.</summary>
    public static ProcessUsage? HeavyMemory(StorageState s)
    {
        var cpu = HeavyCpu(s);
        return s.TopProcesses?.Where(p => p.MemoryPercent > HeavyMemoryPercent && !ReferenceEquals(p, cpu)).MaxBy(p => p.MemoryPercent);
    }

    /// <summary>The person's own startup entries that may be offered (see <see cref="StartupApps.Offerable"/>).</summary>
    public static IReadOnlyList<StartupEntry> StartupOffers(StorageState s) =>
        s.StartupEntries is { } e ? StartupApps.Offerable(e, s.WindowsDirectory) : Array.Empty<StartupEntry>();

    private static string StartupDetail(StorageState s)
    {
        var offer = StartupOffers(s);
        var machine = s.StartupEntries is { } e ? StartupApps.MachineWide(e, s.WindowsDirectory) : Array.Empty<StartupEntry>();
        return $"{s.StartupAppCount} enabled Run/Startup entries; can be switched off without admin: " +
               (offer.Count > 0 ? string.Join(", ", offer.Select(x => x.Name)) : "none") +
               "; machine-wide (switching off needs admin, not offered): " +
               (machine.Count > 0 ? string.Join(", ", machine.Select(x => $"{x.Name} ({x.Scope})")) : "none");
    }

    private const string Incomplete = "AutoFix Scout could not complete the storage check.";

    private const string StorageSenseSay =
        "AutoFix Scout will open the \"Storage\" settings. Please select \"Temporary files\" or \"Cleanup recommendations\" " +
        "to see what else Windows can remove safely.";

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    /// <summary>Below 10% or 10 GB free is low, below 3% or 2 GB is critical (whichever is reached first).</summary>
    public static SpaceLevel Space(long? free, long? total)
    {
        if (free is not { } f || total is not { } t || t <= 0) return SpaceLevel.Unknown;
        var pct = 100.0 * f / t;
        if (pct < CriticalPercent || f < CriticalBytes) return SpaceLevel.Critical;
        if (pct < LowPercent || f < LowBytes) return SpaceLevel.Low;
        return SpaceLevel.Ok;
    }

    public static string Size(long bytes) =>
        bytes >= GB ? (bytes / (double)GB).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                    : Math.Max(1, bytes / MB).ToString(CultureInfo.InvariantCulture) + " MB";

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(StorageState s)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)) };

        var f = new List<Finding>();
        if (s.ProblemDevices is { Count: > 0 } bad)
            f.Add(F(FindingCode.StorageDeviceError, "A disk or storage controller in this computer reports a problem.",
                string.Join("; ", bad.Select(d => $"'{d.Name}' ({d.PnpClass}) problem code {d.ProblemCode}"))));

        var space = Space(s.FreeBytes, s.TotalBytes);
        var spaceDetail = s.FreeBytes is { } fr && s.TotalBytes is { } tot
            ? $"{s.SystemDrive} {Size(fr)} free of {Size(tot)} ({100.0 * fr / tot:0.0}%)" : "unknown";
        if (space == SpaceLevel.Critical)
            f.Add(F(FindingCode.SystemDriveCritical, "The drive Windows is installed on is almost completely full.", spaceDetail));
        else if (space == SpaceLevel.Low)
            f.Add(F(FindingCode.SystemDriveLow, "The drive Windows is installed on is nearly full.", spaceDetail));

        if (HeavyCpu(s) is { } cpu)
            f.Add(F(FindingCode.HeavyProcessCpu, $"The program \"{cpu.Name}\" is using {Pct(cpu.CpuPercent)}% of the processor at the moment.",
                $"{cpu.Path} cpu {cpu.CpuPercent.ToString(CultureInfo.InvariantCulture)}% of all cores, memory {Size(cpu.WorkingSetBytes)}"));
        if (HeavyMemory(s) is { } mem)
            f.Add(F(FindingCode.HeavyProcessMemory, $"The program \"{mem.Name}\" is using {Pct(mem.MemoryPercent)}% of the memory in this computer.",
                $"{mem.Path} working set {Size(mem.WorkingSetBytes)} ({mem.MemoryPercent.ToString(CultureInfo.InvariantCulture)}%), cpu {mem.CpuPercent.ToString(CultureInfo.InvariantCulture)}%"));

        var reclaim = (s.ReclaimableTempBytes ?? 0) + (s.RecycleBinBytes ?? 0);
        if (reclaim >= LargeTempBytes)
            f.Add(F(FindingCode.LargeTempFiles, $"Old temporary files and the Recycle Bin are taking up {Size(reclaim)} of space.",
                $"temp>24h={s.ReclaimableTempBytes} temp={s.UserTempBytes} recycle={s.RecycleBinBytes} windowsTemp={s.WindowsTempBytes?.ToString() ?? "unreadable"}"));

        if (s.MemoryLoadPercent >= HighMemoryPercent)
            f.Add(F(FindingCode.HighMemoryPressure, "Almost all of the memory in this computer is in use at the moment.", $"memory load {s.MemoryLoadPercent}%"));
        if (s.StartupAppCount >= ManyStartupApps)
            f.Add(F(FindingCode.ManyStartupApps, $"{s.StartupAppCount} apps start automatically when you sign in to Windows.", StartupDetail(s)));
        else if (StartupOffers(s) is { Count: > 0 } offer)
            f.Add(F(FindingCode.StartupAppsOptional, offer.Count == 1
                    ? $"The app \"{StartupApps.DisplayName(offer[0])}\" starts automatically when you sign in to Windows, which can slow the computer down."
                    : $"{offer.Count} apps that are not part of Windows start automatically when you sign in, which can slow the computer down.",
                StartupDetail(s)));
        if (s.Uptime > LongUptime)
            f.Add(F(FindingCode.LongUptime, $"The computer has not been restarted for {(int)s.Uptime.Value.TotalDays} days.", $"uptime {s.Uptime.Value.TotalHours:0} h"));

        if (f.Count > 0) return f;

        // Fail closed: nothing found in what could not be read is not "fine".
        var unread = new (string Name, object? Value)[]
        {
            ("free space", s.FreeBytes), ("temp", s.ReclaimableTempBytes), ("recycle bin", s.RecycleBinBytes),
            ("startup apps", s.StartupAppCount), ("programs", s.TopProcesses), ("uptime", s.Uptime), ("memory", s.MemoryLoadPercent), ("storage devices", s.ProblemDevices),
        }.Where(x => x.Value is null).Select(x => x.Name).ToList();
        if (unread.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, "not read: " + string.Join(", ", unread)) };
        return new[] { F(FindingCode.LooksNormal, "Windows reports no storage or memory problem on this computer.",
            $"{spaceDetail}; startup={s.StartupAppCount}; memory={s.MemoryLoadPercent}%; uptime={s.Uptime!.Value.TotalHours:0} h") };
    }

    /// <summary>Least risky first. A disk or controller fault gets no rung: it is reported, never changed.</summary>
    public static IReadOnlyList<Rung> Ladder(StorageState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var rungs = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return rungs;

        // A busy program is the quickest cause to remove, but only the person may close it: unsaved work would be lost.
        foreach (var (p, id, what) in new[] { (HeavyCpu(s), "close_busy_program", "processor"), (HeavyMemory(s), "close_large_program", "memory") })
            if (p is not null)
                rungs.Add(new(id, "ui.human_step", 0,
                    $"The program \"{p.Name}\" is using a large part of the {what} in this computer. " +
                    $"Please save your work in \"{p.Name}\", then close it. AutoFix Scout never closes programs itself, so that no work is lost."));

        var junk = codes.Contains(FindingCode.SystemDriveCritical) || codes.Contains(FindingCode.SystemDriveLow)
                   || codes.Contains(FindingCode.LargeTempFiles);
        if (junk && s.ReclaimableTempBytes >= WorthCleaning)
            rungs.Add(new("clear_temp", "storage.clear_user_temp", 1,
                $"AutoFix Scout can delete your temporary files that are older than one day ({Size(s.ReclaimableTempBytes!.Value)}). " +
                "These are temporary files that apps no longer need. Files that are in use are skipped."));
        if (junk && s.RecycleBinBytes >= WorthCleaning)
            rungs.Add(new("empty_recycle_bin", "storage.empty_recycle_bin", 1,
                $"The Recycle Bin holds {Size(s.RecycleBinBytes!.Value)} of deleted files. AutoFix Scout can empty it. " +
                "This cannot be undone: the files in the Recycle Bin will be deleted permanently. " +
                "Please continue only if you do not need any of them."));
        if (codes.Contains(FindingCode.SystemDriveCritical) || codes.Contains(FindingCode.SystemDriveLow))
            rungs.Add(new("cleanup_components", "storage.cleanup_components", 1,
                "AutoFix Scout can ask Windows to remove old versions of Windows components that newer updates have replaced. " +
                "This can take 10 to 30 minutes and cannot be undone. Your files and apps are not affected. " +
                "Windows will ask for your permission.", "system"));
        if (junk)
            rungs.Add(new("storage_settings", "ui.open_settings_page", 0, StorageSenseSay, "storagesense"));

        var offers = StartupOffers(s);
        for (var i = 0; i < offers.Count; i++)
        {
            var name = StartupApps.DisplayName(offers[i]);
            rungs.Add(new($"startup_off_{i + 1}", "startup.disable", 1,
                $"\"{name}\" starts automatically every time you sign in to Windows. AutoFix Scout can stop it from starting automatically. " +
                "The app is not removed, and you can turn it on again in \"Startup apps\" at any time. " +
                "The change takes effect the next time you sign in.", offers[i].Name));
        }

        if (codes.Contains(FindingCode.ManyStartupApps) || codes.Contains(FindingCode.HighMemoryPressure)
            || codes.Contains(FindingCode.LongUptime) || codes.Contains(FindingCode.LooksNormal))
            rungs.Add(new("startup_apps", "ui.open_settings_page", 0,
                "AutoFix Scout will open the \"Startup apps\" settings. Please turn off the apps that you do not need right after you sign in. " +
                "You can turn them on again at any time.", "startupapps"));

        if (codes.Contains(FindingCode.LongUptime))
            rungs.Add(new("restart_pc", "ui.human_step", 0,
                $"Your computer has been running for {(int)s.Uptime!.Value.TotalDays} days without a restart. " +
                "Please save your work, then select \"Start\", then \"Power\", then \"Restart\". " +
                "After the restart, please open AutoFix Scout again."));
        return rungs;
    }

    public static bool ProbablyHardware(StorageState s) => s.ProbeErrors.Count == 0 && s.ProblemDevices is { Count: > 0 };

    /// <summary>The honest "not fixed" sentence.</summary>
    public static string? NotFixedHint(IReadOnlyList<Finding> after)
    {
        var codes = after.Select(x => x.Code).ToHashSet();
        if (codes.Contains(FindingCode.StorageDeviceError))
            return "A disk or storage controller reports a problem in \"Device Manager\". AutoFix Scout does not change disks or " +
                   "storage controllers, because a fault there can stop Windows from starting. Please back up your files, " +
                   "and ask the person helping you to check the disk.";
        if (codes.Contains(FindingCode.SystemDriveCritical) || codes.Contains(FindingCode.SystemDriveLow))
            return "The drive Windows is installed on is still nearly full. Moving photos, videos or other large files to an external " +
                   "drive or to cloud storage frees space. The person helping you can advise which files to move.";
        if (codes.Contains(FindingCode.HeavyProcessCpu) || codes.Contains(FindingCode.HeavyProcessMemory))
            return "A program is still using a large part of the processor or memory. If closing it did not help, restarting the computer " +
                   "usually stops it. If it happens again, the person helping you can check whether the program needs an update.";
        if (codes.Contains(FindingCode.LooksNormal) || codes.Contains(FindingCode.ManyStartupApps) || codes.Contains(FindingCode.HighMemoryPressure)
            || codes.Contains(FindingCode.StartupAppsOptional))
            return "If the computer is still slow, it may have too little memory for the apps in use, or its drive may be getting old. " +
                   "The person helping you can check this.";
        return null;
    }
}
