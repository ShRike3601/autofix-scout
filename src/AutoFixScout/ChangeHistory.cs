using System.Text.RegularExpressions;
using AutoFixScout.Repair;

namespace AutoFixScout;

/// <summary>One line of the "Changes made by AutoFix Scout" list.</summary>
/// <param name="StepId">Pass to <see cref="ChangeHistory.UndoAsync"/>.</param>
/// <param name="CanUndo">True only when an undo path really exists right now.</param>
/// <param name="Undone">The change has already been put back.</param>
/// <param name="Hint">When the change cannot be undone automatically: what the person can do instead. Otherwise null.</param>
public sealed record ChangeEntry(long StepId, DateTime WhenUtc, string Description, bool CanUndo, bool Undone, string? Hint);

public sealed record UndoResult(bool Ok, string Message);

/// <summary>
/// Every change AutoFix Scout made (from the session log), newest first, and an Undo for the ones that can be put back:
/// driver changes (the elevated executor restores the save point; Windows asks for permission) and start-up apps that
/// were switched off. Everything else is listed with a hint, never with a button.
/// </summary>
public sealed class ChangeHistory
{
    public const string DriverKind = "driver", StartupKind = "startup";

    private readonly string _dbPath;
    private readonly string _savePointRoot;
    private readonly Func<string, string, Task<(bool Ok, string Detail)>> _runUndo;

    /// <param name="runUndo">(kind, payload) to the action that puts the change back. Tests pass a fake.</param>
    public ChangeHistory(string? dbPath = null, string? savePointRoot = null, Func<string, string, Task<(bool Ok, string Detail)>>? runUndo = null)
    {
        _dbPath = dbPath ?? SessionLog.DefaultPath;
        _savePointRoot = savePointRoot ?? DefaultSavePointRoot;
        _runUndo = runUndo ?? RunRealUndoAsync;
    }

    /// <summary>Where the elevated executor keeps save points.</summary>
    public static string DefaultSavePointRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoFixScout", "savepoints");

    // ---- which changes can be undone ----

    private static readonly Regex SavePointId = new(@"save point ([0-9a-f]{32})", RegexOptions.Compiled);
    private static readonly Regex StartupEntry = new(@"startup entry '(.+)' disabled in StartupApproved\\(Run|StartupFolder)\b", RegexOptions.Compiled);

    /// <summary>The undo record to store for a step that succeeded, or null when nothing can put it back.</summary>
    public static (string Kind, string Payload)? UndoFor(string actionId, string? detail)
    {
        if (detail is null) return null;
        if (FixActions.IsDriverChange(actionId) && SavePointId.Match(detail) is { Success: true } sp)
            return (DriverKind, sp.Groups[1].Value);
        if (actionId == "startup.disable" && StartupEntry.Match(detail) is { Success: true } st && StartupApps.IsValidName(st.Groups[1].Value))
            return (StartupKind, st.Groups[2].Value + "|" + st.Groups[1].Value);
        return null;
    }

    /// <summary>Steps that are not changes to the computer: questions to the person, tests and scans.</summary>
    public static bool IsChange(string actionId) =>
        !actionId.StartsWith("ui.", StringComparison.Ordinal)
        && actionId is not ("audio.play_test_tone" or "security.quick_scan" or "security.full_scan" or "disk.scan");

    // ---- wording ----

    public static string Describe(string actionId, string? detail) => actionId switch
    {
        "audio.unmute_and_set_volume" => "The sound output was turned on and the volume was raised.",
        "audio.unmute_microphone" => "The microphone was turned on and its level was raised.",
        "audio.unmute_app" => "The sound of an app was turned on.",
        "audio.ducking_off" => "The Windows setting that lowers other sounds during calls was turned off.",
        "service.restart" => "A Windows service was restarted.",
        "service.set_manual" => "A Windows service was set to start when needed and was started.",
        "service.restore_default" => "A Windows service was set back to its default start type.",
        "device.disable_enable" => "A device was switched off and on again.",
        "device.enable" => "A device was switched on.",
        "device.rescan" => "Windows was asked to look for hardware changes.",
        "driver.rollback" => "The driver of a device was replaced by the previous version.",
        "driver.install_from_windows_update" => "The driver of a device was replaced by the version offered by Windows Update.",
        "network.reset_dns" => "The DNS settings of a network adapter were reset.",
        "network.renew_address" => "The network address of a network adapter was renewed.",
        "network.flush_dns" => "The stored internet addresses (DNS cache) were cleared.",
        "network.reset_winhttp_proxy" => "The Windows proxy setting for system services was reset.",
        "network.reset_user_proxy" => "The proxy setting of your account was reset.",
        "network.clean_hosts" => "Unusual entries were removed from the Windows hosts file.",
        "network.reset_winsock" => "The Windows network components (Winsock) were reset.",
        "network.wifi_stability" => "Settings of the Wi-Fi adapter were adjusted for a more stable connection.",
        "display.extend" => "The screen was set to extend to the second display.",
        "display.set_brightness" => "The screen brightness was raised.",
        "printer.clear_queue" => "The print queue was cleared.",
        "printer.set_online" => "A printer was set to online.",
        "repair.dism" => "Windows system files were repaired with the DISM tool.",
        "repair.sfc" => "Windows system files were checked and repaired with the System File Checker.",
        "time.resync" => "The clock was synchronised with a time server.",
        "system.repair_path" => "The Windows search path (PATH) was repaired.",
        "system.repair_temp" => "The temporary folder of your account was repaired.",
        "system.pagefile_auto" => "The paging file was set to be managed by Windows.",
        "update.reset_components" => "The Windows Update components were reset.",
        "search.rebuild_index" => "The Windows search index was rebuilt.",
        "runtime.install_vcredist" => "The Microsoft Visual C++ runtime was installed or repaired.",
        "storage.cleanup_components" => "Old Windows update files were cleaned up.",
        "storage.clear_user_temp" => "Temporary files of your account were deleted.",
        "storage.empty_recycle_bin" => "The Recycle Bin was emptied.",
        "office.quick_repair" => "Microsoft Office was repaired (Quick Repair).",
        "apps.reregister" => "An app was registered with Windows again.",
        "apps.reset_store_cache" => "The cache of the Microsoft Store was reset.",
        "apps.start_onedrive" => "OneDrive was started.",
        "apps.reset_onedrive" => "OneDrive was reset.",
        "power.balanced_plan" => "The Windows power plan was set to \"Balanced\".",
        "shell.notifications_on" => "Windows notifications were turned on.",
        "shell.restart_start_menu" => "The Start menu was restarted.",
        "shell.restart_explorer" => "Windows Explorer was restarted.",
        "shell.rebuild_icon_cache" => "The icon cache was rebuilt.",
        "startup.disable" => "An app was prevented from starting when you sign in.",
        "security.update_signatures" => "The virus definitions of Microsoft Defender were updated.",
        "security.firewall_on" => "Windows Firewall was turned on.",
        "security.uac_on" => "User Account Control was turned on.",
        "security.remove_browser_policies" or "security.remove_browser_policies_user" => "Browser policies that changed the home page or search were removed.",
        "input.touchpad_on" => "The touchpad was turned on.",
        "input.accessibility_keys_off" => "The shortcut for Sticky Keys and Filter Keys was turned off.",
        _ => "AutoFix Scout made a change to Windows.",
    };

    /// <summary>What the person can do when there is no automatic undo. Formal, no promises.</summary>
    public static string HintFor(string actionId, string kind = "") => actionId switch
    {
        "driver.rollback" or "driver.install_from_windows_update" =>
            "The saved copy of the previous driver is no longer available. In \"Device Manager\", open the device, select \"Properties\", then \"Driver\" and \"Roll Back Driver\".",
        "audio.unmute_and_set_volume" or "audio.unmute_microphone" or "audio.unmute_app" or "audio.ducking_off" =>
            "The volume and sound settings can be changed again in the Windows sound settings.",
        "display.set_brightness" or "display.extend" => "The screen settings can be changed again in the Windows display settings.",
        "power.balanced_plan" => "The power plan can be changed in the Windows power settings.",
        "startup.disable" => "The app can be switched on again under \"Startup apps\" in the Windows settings.",
        "service.restart" or "device.disable_enable" or "device.enable" or "device.rescan" or "network.flush_dns"
            or "network.renew_address" or "shell.restart_start_menu" or "shell.restart_explorer" or "shell.rebuild_icon_cache"
            or "apps.start_onedrive" or "security.update_signatures" =>
            "This change cannot be undone automatically. It does not leave a setting behind that needs to be put back.",
        "storage.clear_user_temp" or "storage.empty_recycle_bin" or "storage.cleanup_components" =>
            "Deleted files cannot be restored automatically.",
        _ => "This change cannot be undone automatically. If a problem remains, please ask the person helping you.",
    };

    // ---- list and undo ----

    /// <summary>The changes AutoFix Scout made, newest first. Empty when the log cannot be read.</summary>
    public IReadOnlyList<ChangeEntry> List(int max = 200)
    {
        try
        {
            using var log = new SessionLog(_dbPath);
            var list = new List<ChangeEntry>();
            foreach (var row in log.ChangeRows())
            {
                if (!IsChange(row.ActionId)) continue;
                var undone = row.Result == "undone" || row.UndoApplied;
                var description = Describe(row.ActionId, row.Detail);
                if (undone) { list.Add(new ChangeEntry(row.StepId, row.WhenUtc, description, false, true, null)); }
                else if (row.UndoId is not null && IsStillPossible(row.UndoKind, row.UndoPayload))
                    list.Add(new ChangeEntry(row.StepId, row.WhenUtc, description, true, false, null));
                else
                    list.Add(new ChangeEntry(row.StepId, row.WhenUtc, description, false, false, HintFor(row.ActionId, row.UndoKind ?? "")));
                if (list.Count >= max) break;
            }
            return list;
        }
        catch (Exception) { return Array.Empty<ChangeEntry>(); }
    }

    private bool IsStillPossible(string? kind, string? payload) => kind switch
    {
        // The executor keeps the save point until it is used or 30 days old.
        DriverKind => payload is { Length: 32 } && Directory.Exists(Path.Combine(_savePointRoot, payload)),
        StartupKind => payload is not null && payload.Contains('|'),
        _ => false,
    };

    /// <summary>Puts one change back. Never throws. The Windows permission prompt appears for driver changes.</summary>
    public async Task<UndoResult> UndoAsync(long stepId)
    {
        try
        {
            using var log = new SessionLog(_dbPath);
            var row = log.ChangeRows().FirstOrDefault(r => r.StepId == stepId);
            if (row is null || row.UndoId is null || row.UndoApplied || row.Result == "undone" || !IsStillPossible(row.UndoKind, row.UndoPayload))
                return new UndoResult(false, "This change cannot be undone automatically.");
            var (ok, _) = await _runUndo(row.UndoKind!, row.UndoPayload!);
            if (!ok)
                return new UndoResult(false, row.UndoKind == DriverKind
                    ? "The change was not undone. The Windows permission prompt may have been declined, or the previous driver could not be restored."
                    : "The change was not undone.");
            log.MarkUndone(stepId);
            return new UndoResult(true, "The change has been undone.");
        }
        catch (Exception) { return new UndoResult(false, "The change was not undone."); }
    }

    private static async Task<(bool Ok, string Detail)> RunRealUndoAsync(string kind, string payload)
    {
        switch (kind)
        {
            case DriverKind:
                // The executor restores the save point (exit code 0, or 3 when nothing had changed); 4 is the
                // automatic undo of a failed change and never reaches here. One Windows permission prompt.
                var ok = await FixActions.SettleSavePointAsync(payload, keep: false);
                return (ok, ok ? "driver restored" : "restore failed or declined");
            case StartupKind:
                var parts = payload.Split('|', 2);
                return await Task.Run(() => StartupApps.Enable(parts[1], parts[0]));
            default:
                return (false, "no undo for " + kind);
        }
    }
}
