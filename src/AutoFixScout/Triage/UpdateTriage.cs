namespace AutoFixScout.Triage;

/// <summary>
/// "Windows Update is not working". Pure local logic. Never deletes the update cache and never runs DISM or SFC.
/// wuauserv and BITS start on demand, so "stopped" is normal for them; only "disabled" is a fault. CryptSvc normally runs.
/// </summary>
public static class UpdateTriage
{
    public static readonly TimeSpan OutOfDate = TimeSpan.FromDays(45);

    private const string Incomplete = "AutoFix Scout could not complete the Windows Update check.";

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    /// <summary>The names Windows shows in "Services".</summary>
    public static string DisplayName(string service) => service.ToLowerInvariant() switch
    {
        "wuauserv" => "Windows Update",
        "bits" => "Background Intelligent Transfer Service",
        "cryptsvc" => "Cryptographic Services",
        _ => service,
    };

    private static UpdateServiceInfo? Crypt(UpdateState s) =>
        s.Services.FirstOrDefault(x => x.Name.Equals("CryptSvc", StringComparison.OrdinalIgnoreCase));

    /// <summary>Findings, most important first; always at least one.</summary>
    public static IReadOnlyList<Finding> Evaluate(UpdateState s, DateTime? nowUtc = null)
    {
        if (s.ProbeErrors.Count > 0)
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete, string.Join("; ", s.ProbeErrors)) };
        var now = nowUtc ?? DateTime.UtcNow;
        var f = new List<Finding>();

        var disabled = s.Services.Where(x => x.Disabled).ToList();
        if (disabled.Count > 0)
            f.Add(F(FindingCode.UpdateServiceDisabled, "A part of Windows that Windows Update needs is switched off.",
                "Disabled: " + string.Join(", ", disabled.Select(x => x.Name))));
        if (Crypt(s) is { Status: "Stopped" } c && !c.Disabled)
            f.Add(F(FindingCode.UpdateServiceStopped, "A part of Windows that Windows Update needs is not running.", "CryptSvc stopped"));
        if (s.RebootPending == Tri.Yes)
            f.Add(F(FindingCode.UpdateRebootPending, "Windows is waiting for a restart to finish installing updates.", "RebootRequired/RebootPending key present"));
        if (StorageTriage.Space(s.FreeBytes, s.TotalBytes) is SpaceLevel.Low or SpaceLevel.Critical)
            f.Add(F(FindingCode.UpdateLowDiskSpace, "The drive Windows is installed on is nearly full, so updates may not have room to install.",
                $"{StorageTriage.Size(s.FreeBytes!.Value)} free of {StorageTriage.Size(s.TotalBytes!.Value)}"));
        if (s.LastInstallUtc is { } last && now - last > OutOfDate)
            f.Add(F(FindingCode.UpdatesOutOfDate, $"Windows has not installed any updates for {(int)(now - last).TotalDays} days.",
                $"last successful install {last:yyyy-MM-dd} UTC"));
        if (f.Count > 0) return f;

        // Fail closed: an unread service or restart flag is not "fine". An unknown last install date only goes into the
        // detail: the ladder for a normal-looking check still lets the person check for updates.
        if (s.RebootPending == Tri.Unknown || s.Services.Count == 0 || s.Services.Any(x => x.Status == "Unknown"))
            return new[] { F(FindingCode.ProbeIncomplete, Incomplete,
                $"reboot={s.RebootPending}; services={string.Join(", ", s.Services.Select(x => $"{x.Name}:{x.Status}/{x.StartType}"))}") };
        return new[] { F(FindingCode.LooksNormal, "The parts of Windows that Windows Update needs are ready, and no restart is waiting.",
            $"last install {(s.LastInstallUtc is { } d ? d.ToString("yyyy-MM-dd") : "unknown")}; " +
            string.Join(", ", s.Services.Select(x => $"{x.Name}:{x.Status}/{x.StartType}"))) };
    }

    /// <summary>Least risky first. No cache deletion, no DISM or SFC.</summary>
    public static IReadOnlyList<Rung> Ladder(UpdateState s, IReadOnlyList<Finding> findings)
    {
        var codes = findings.Select(x => x.Code).ToHashSet();
        var rungs = new List<Rung>();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return rungs;

        foreach (var d in s.Services.Where(x => x.Disabled))
            rungs.Add(new($"enable_{d.Name.ToLowerInvariant()}", "service.set_manual", 1,
                $"The \"{DisplayName(d.Name)}\" service is switched off. AutoFix Scout can set it to start when Windows needs it, " +
                "and start it now. Windows will ask for your permission.", d.Name));
        if (codes.Contains(FindingCode.UpdateServiceStopped))
            rungs.Add(new("start_cryptsvc", "service.restart", 1,
                "AutoFix Scout can start the \"Cryptographic Services\" service, which Windows Update uses to check downloads. " +
                "Windows will ask for your permission.", "CryptSvc"));

        bool Present(string n) => s.Services.Any(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && x.Status != "NotFound");
        if (Present("wuauserv") && Present("BITS"))
            rungs.Add(new("restart_update_services", "service.restart", 1,
                "AutoFix Scout can restart the parts of Windows that download and install updates. Windows will ask for your permission.", "wuauserv"));

        if (codes.Contains(FindingCode.UpdateRebootPending))
            rungs.Add(new("restart_pc", "ui.human_step", 0,
                "Windows needs a restart to finish installing updates. Please save your work, then select \"Start\", then \"Power\", " +
                "then \"Update and restart\" or \"Restart\". After the restart, please open AutoFix Scout again."));
        if (codes.Contains(FindingCode.UpdateLowDiskSpace))
            rungs.Add(new("free_space", "ui.open_settings_page", 0,
                "Windows Update needs free space to install updates. AutoFix Scout will open the \"Storage\" settings. " +
                "Please select \"Temporary files\" or \"Cleanup recommendations\" to free space.", "storagesense"));
        rungs.Add(new("open_windows_update", "ui.open_settings_page", 0,
            "AutoFix Scout will open the \"Windows Update\" settings. Please select \"Check for updates\" and wait until the check has finished.",
            "windowsupdate"));
        rungs.Add(new("update_troubleshooter", "ui.open_settings_page", 0,
            "AutoFix Scout will open the \"Troubleshoot\" settings. Please select \"Other troubleshooters\", then select \"Run\" next to \"Windows Update\".",
            "troubleshoot"));
        return rungs;
    }

    /// <summary>Windows Update problems are software problems.</summary>
    public static bool ProbablyHardware(UpdateState s) => false;

    public static string? NotFixedHint(IReadOnlyList<Finding> after)
    {
        var codes = after.Select(x => x.Code).ToHashSet();
        if (codes.Contains(FindingCode.ProbeIncomplete)) return null;
        if (codes.Contains(FindingCode.UpdateRebootPending))
            return "Windows is still waiting for a restart. Please restart the computer, and then check for updates again.";
        return "Windows Update could still not install updates. The \"Windows Update\" settings page shows an error code, " +
               "which the person helping you can use to find the cause.";
    }

    /// <summary>Automatic re-check: nothing switched off, "Cryptographic Services" running, and no restart waiting.</summary>
    public static bool Healthy(UpdateState s) =>
        s.ProbeErrors.Count == 0 && s.RebootPending == Tri.No && s.Services.Count > 0
        && s.Services.All(x => !x.Disabled && x.Status is not ("Unknown" or "NotFound"))
        && Crypt(s) is { Status: "Running" };
}
