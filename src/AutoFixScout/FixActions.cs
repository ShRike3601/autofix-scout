using System.ComponentModel;
using System.Diagnostics;
using AutoFixScout.Triage;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AutoFixScout;

/// <summary>Windows side of the fix ladders. Non-admin actions run here; admin ones go to the elevated executor.</summary>
public static class FixActions
{
    /// <summary>Actions that go to the elevated executor with one argument (one Windows permission prompt each).</summary>
    public static readonly HashSet<string> ElevatedActions = new()
    {
        "service.restart", "device.disable_enable", "device.enable", "network.reset_dns", "network.renew_address", "device.rescan", "service.set_manual", "printer.clear_queue", "printer.set_online",
        "repair.dism", "repair.sfc", "time.resync", "network.reset_winhttp_proxy", "network.clean_hosts", "service.restore_default",
        "system.repair_path", "network.reset_winsock", "update.reset_components", "search.rebuild_index", "runtime.install_vcredist", "disk.scan",
        "storage.cleanup_components", "office.quick_repair", "network.wifi_stability", "security.firewall_on", "security.uac_on", "security.remove_browser_policies", "system.pagefile_auto",
        // Read-only: no argument; the result comes back as a file the app validates and deletes (see ElevatedDiagnostics).
        Probes.DiagResults.BootAction, Probes.DiagResults.CrashAction, Probes.DiagResults.DriveAction,
    };

    public static async Task<(bool Ok, string Detail)> RunAsync(Rung rung)
    {
        if (rung.ActionId == "ui.human_step") return await RunCoreAsync(rung);
        // Only the allowlisted action id is reported, never the argument (it can name a device or a program).
        if (!ElevatedActions.Contains(rung.ActionId) && rung.ActionId != "ui.open_settings_page")
            Activity.Report(ActivityKind.Change, $"Running fix step: {rung.ActionId} (no administrator rights needed)");
        var result = await RunCoreAsync(rung);
        Activity.Report(ActivityKind.Change, result.Ok ? $"Fix step {rung.ActionId} finished" : $"Fix step {rung.ActionId} did not succeed");
        return result;
    }

    private static async Task<(bool Ok, string Detail)> RunCoreAsync(Rung rung)
    {
        try
        {
            switch (rung.ActionId)
            {
                case "ui.open_settings_page":
                    // Only fixed pages; the person flips the setting themselves (spec 8.1).
                    if (rung.Arg == "sound_classic")
                    {
                        // Fixed command, no argument from outside; only the classic panel has "Set as Default Communication Device".
                        Activity.Report(ActivityKind.Change, "Opening the classic Sound panel (control.exe mmsys.cpl)");
                        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "control.exe"), "mmsys.cpl") { UseShellExecute = false });
                        return (true, "control.exe mmsys.cpl");
                    }
                    var uri = rung.Arg switch
                    {
                        "sound" => "ms-settings:sound",
                        "volume_mixer" => "ms-settings:apps-volume",
                        "camera" => "ms-settings:privacy-webcam",
                        "airplane" => "ms-settings:network-airplanemode",
                        "wifi" => "ms-settings:network-wifi",
                        "camera_app" => "microsoft.windows.camera:",
                        "storagesense" => "ms-settings:storagesense",
                        "startupapps" => "ms-settings:startupapps",
                        "windowsupdate" => "ms-settings:windowsupdate",
                        "troubleshoot" => "ms-settings:troubleshoot",
                        "microphone" => "ms-settings:privacy-microphone",
                        "sound_devices" => "ms-settings:sound-devices",
                        "printers" => "ms-settings:printers",
                        "scanners" => "ms-settings:printers",
                        "display" => "ms-settings:display",
                        "graphics" => "ms-settings:display-advancedgraphics",
                        "gaming" => "ms-settings:gaming-gamemode",
                        "capture" => "ms-settings:gaming-gamedvr",
                        "apps" => "ms-settings:appsfeatures",
                        "updatehistory" => "ms-settings:windowsupdate-history",
                        "backup" => "ms-settings:backup",
                        "dateandtime" => "ms-settings:dateandtime",
                        "bluetooth" => "ms-settings:bluetooth",
                        "powersleep" => "ms-settings:powersleep",
                        "batterysaver" => "ms-settings:batterysaver",
                        "touchpad" => "ms-settings:devices-touchpad",
                        "language" => "ms-settings:regionlanguage",
                        "typing" => "ms-settings:typing",
                        "keyboard_access" => "ms-settings:easeofaccess-keyboard",
                        "mouse" => "ms-settings:mousetouchpad",
                        "notifications" => "ms-settings:notifications",
                        "windowsdefender" => "windowsdefender:",
                        "windowsdefender_appbrowser" => "windowsdefender://appbrowser",
                        _ => throw new ArgumentException(rung.Arg),
                    };
                    Activity.Report(ActivityKind.Change, $"Opening the Windows page {uri}");
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                    return (true, uri);
                case "ui.human_step":
                    return (true, "person was asked to do it");
                case "audio.unmute_and_set_volume":
                    return (true, Unmute());
                case "audio.ducking_off":
                    return await Task.Run(CallSound.DuckingOff);
                case "audio.unmute_app":
                    return await Task.Run(() => AppSound.Unmute(rung.Arg));
                case "audio.unmute_microphone":
                    return (true, Unmute(DataFlow.Capture, Role.Communications));
                case "network.flush_dns":
                    // Flushing the resolver cache doesn't need admin, so no UAC prompt.
                    return await Task.Run(() => RunFixed("ipconfig.exe", "/flushdns"));
                case "display.extend":
                    // Fixed argument, no admin. The person can undo it with Windows+P.
                    return await Task.Run(() => { var r = RunFixed("DisplaySwitch.exe", "/extend"); Thread.Sleep(3000); return r; });
                case "display.set_brightness":
                    return await Task.Run(SetBrightness);
                case Probes.DiagResults.BootAction or Probes.DiagResults.CrashAction or Probes.DiagResults.DriveAction:
                    return await RunDiagAsync(rung.ActionId);
                case var elevated when ElevatedActions.Contains(elevated):
                    var code = await Task.Run(() => RunElevated(rung.ActionId, rung.Arg!));
                    return (code == 0, code is null ? Declined : $"executor exit code {code}");
                case "storage.clear_user_temp":
                    // The person's own temp folder only, files older than a day; no admin needed.
                    var (freed, deleted, skipped) = await Task.Run(() =>
                        Areas.StorageCleanup.ClearTemp(Path.GetTempPath(), DateTime.UtcNow.AddHours(-24)));
                    return (true, $"temporary files removed: {deleted} ({freed / 1048576} MB), skipped (in use): {skipped}");
                case "network.reset_user_proxy":
                    return await Task.Run(Repair.UserRepairs.ResetUserProxy);
                case "system.repair_temp":
                    return await Task.Run(Repair.UserRepairs.RepairTemp);
                case "apps.reregister":
                    return await Task.Run(() => Repair.UserRepairs.ReregisterApp(rung.Arg ?? ""));
                case "apps.reset_store_cache":
                    return await Task.Run(Repair.AppRepairs.ResetStoreCache);
                case "apps.start_onedrive":
                    return await Task.Run(Repair.AppRepairs.StartOneDrive);
                case "apps.reset_onedrive":
                    // The OneDrive program found on this computer; the rung carries no path.
                    return await Task.Run(Repair.AppRepairs.ResetOneDrive);
                case "power.balanced_plan":
                    // Fixed Balanced GUID; powercfg /setactive works for the signed-in user without admin.
                    return await Task.Run(() => RunFixed("powercfg.exe", "/setactive " + PowerTriage.BalancedGuid));
                case "shell.notifications_on":
                    return await Task.Run(Repair.NotificationRepairs.NotificationsOn);
                case "shell.restart_start_menu":
                    return await Task.Run(Repair.UserRepairs.RestartStartMenu);
                case "shell.restart_explorer":
                    return await Task.Run(Repair.UserRepairs.RestartExplorer);
                case "shell.rebuild_icon_cache":
                    return await Task.Run(Repair.UserRepairs.RebuildIconCache);
                case "startup.disable":
                    // The person's own Run key or Startup folder only, written like Task Manager does; no admin.
                    return await Task.Run(() => Repair.StartupApps.Disable(rung.Arg ?? ""));
                case "security.update_signatures":
                    // MpCmdRun with fixed arguments; Defender allows this without admin rights.
                    return await Task.Run(Repair.UserRepairs.UpdateDefenderSignatures);
                case "security.remove_browser_policies_user":
                    return await Task.Run(Repair.UserRepairs.RemoveBrowserPoliciesUser);
                case "security.quick_scan":
                    return await Task.Run(Repair.UserRepairs.DefenderQuickScan);
                case "security.full_scan":
                    return await Task.Run(Repair.UserRepairs.DefenderFullScan);
                case "input.touchpad_on":
                    return await Task.Run(Repair.InputRepairs.TouchpadOn);
                case "input.accessibility_keys_off":
                    return await Task.Run(Repair.InputRepairs.AccessibilityKeysOff);
                case "network.wifi_watch_on":
                    // The person's own Run key and a process of their own; no admin. Reversible with wifi_watch_off.
                    return await Task.Run(Watching.WifiWatcherSetup.TurnOn);
                case "network.wifi_watch_off":
                    return await Task.Run(Watching.WifiWatcherSetup.TurnOff);
                case Repair.GameFixes.GameModeAction:
                    // The person's own HKCU game settings only; the previous value is kept for Undo in History. No admin.
                    return await Task.Run(Repair.GameFixes.GameModeOn);
                case Repair.GameFixes.GraphicsCardAction:
                    return await Task.Run(() => Repair.GameFixes.UseGraphicsCard(rung.Arg));
                case Repair.GameFixes.RecordingAction:
                    return await Task.Run(Repair.GameFixes.BackgroundRecordingOff);
                case "storage.empty_recycle_bin":
                    return await Task.Run(Areas.StorageCleanup.EmptyRecycleBin);
                default:
                    return (false, $"action not allowed: {rung.ActionId}");
            }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static (bool Ok, string Detail) RunFixed(string exe, string arg)
    {
        Commands.Running(exe, arg);
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), arg) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return (p.ExitCode == 0, $"{exe} {arg} exit code {p.ExitCode}");
    }

    /// <summary>Returns the prior state so the report records what to restore (spec 7.1 undo).</summary>
    /// <summary>Unmutes the default output (or, for capture, the default microphone) and lifts a very low level. Non-admin.</summary>
    private static string Unmute(DataFlow flow = DataFlow.Render, Role role = Role.Multimedia)
    {
        Activity.Report(ActivityKind.Change, flow == DataFlow.Capture ? "Unmuting the default microphone and raising a very low level" : "Unmuting the default sound output and raising a very low volume");
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(flow, role);
        var v = dev.AudioEndpointVolume;
        var before = $"before: muted={v.Mute}, level={v.MasterVolumeLevelScalar * 100:0}%";
        v.Mute = false;
        if (v.MasterVolumeLevelScalar < 0.3f) v.MasterVolumeLevelScalar = flow == DataFlow.Capture ? 0.7f : 0.5f;
        return before;
    }

    /// <summary>
    /// display.set_brightness: built-in screen to 70 %, only when it is below DisplayTriage.LowBrightness (no admin;
    /// WmiMonitorBrightnessMethods). Returns the prior value for the report. Fails honestly where Windows refuses it.
    /// </summary>
    private static (bool Ok, string Detail) SetBrightness()
    {
        using var read = Wmi.Searcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
        var before = read.Get().Cast<System.Management.ManagementBaseObject>().Select(m => (int?)Convert.ToInt32(m["CurrentBrightness"])).FirstOrDefault();
        if (before is null) return (false, "no built-in screen brightness control");
        if (before >= DisplayTriage.LowBrightness) return (true, $"brightness already {before}%, nothing changed");
        using var q = Wmi.Searcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
        foreach (System.Management.ManagementObject m in q.Get())
        {
            Activity.Report(ActivityKind.Change, "Setting the built-in screen brightness to 70 percent (WmiMonitorBrightnessMethods)");
            m.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)70 });
            return (true, $"before: brightness={before}%, set to 70%");
        }
        return (false, "no brightness control found");
    }

    /// <summary>audio.play_test_tone: 1.5 s, 440 Hz on the default output.</summary>
    public static void PlayTestTone()
    {
        Activity.Report(ActivityKind.Change, "Playing a 1.5 s test tone (440 Hz) on the default sound output");
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var mix = dev.AudioClient.MixFormat;
        var tone = new SignalGenerator(mix.SampleRate, mix.Channels) { Frequency = 440, Gain = 0.2, Type = SignalGeneratorType.Sin }
            .Take(TimeSpan.FromSeconds(1.5));
        using var output = new WasapiOut(dev, AudioClientShareMode.Shared, false, 200);
        output.Init(tone);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(50);
    }

    /// <summary>
    /// Starts the executor through UAC with an action id and one argument only (spec 5).
    /// shortcut: one UAC prompt per admin action, switch to a long-lived executor on a named pipe if prompts annoy pilot users.
    /// </summary>
    /// <returns>The executor's exit code, or null if the person declined the Windows permission prompt.</returns>
    private static int? RunElevated(params string[] args)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "AutoFixScout.Executor.exe");
        Activity.Report(ActivityKind.Change, $"Running elevated helper: {args[0]} (Windows asks for permission)");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Activity.Report(ActivityKind.Change, p.ExitCode == 0 ? $"The elevated helper finished {args[0]} successfully" : $"The elevated helper finished {args[0]} with exit code {p.ExitCode}");
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Activity.Report(ActivityKind.Change, "The Windows permission prompt was declined, so nothing was changed");
            return null;
        }
    }

    /// <summary>The read-only helper actions take no argument. Exit 0 = data written, 3 = nothing to report (both are a success).</summary>
    private static async Task<(bool Ok, string Detail)> RunDiagAsync(string actionId)
    {
        var code = await Task.Run(() => RunElevated(actionId));
        if (code is null) return (false, Declined);
        var (ok, detail) = ElevatedDiagnostics.Finish(actionId, code.Value);
        return (ok, code is 0 or 3 ? detail : $"executor exit code {code}; {detail}");
    }

    private const string Declined = "The Windows permission prompt was declined, so nothing was changed.";

    // ---- save points (T2) ----
    // The executor keeps the save point itself (admin-only folder). The app keeps an "open" marker per save point
    // until the person confirms the fix; a marker found at startup means the app never got that far, so it restores.

    private static string PendingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout", "pending-savepoints");

    public static IReadOnlyList<string> OpenSavePoints() =>
        Directory.Exists(PendingDir) ? Directory.GetFiles(PendingDir).Select(Path.GetFileName).OfType<string>().ToList() : Array.Empty<string>();

    public static bool IsDriverChange(string actionId) => actionId is "driver.rollback" or "driver.install_from_windows_update";

    /// <summary>Save point, change, post-check, auto-undo, all inside one elevated run (one permission prompt).</summary>
    public static async Task<(bool Ok, string Detail, string? SavePointId)> ChangeDriverAsync(string actionId, string instanceId)
    {
        if (!IsDriverChange(actionId)) return (false, $"action not allowed: {actionId}", null);
        var id = Guid.NewGuid().ToString("N");
        Activity.Report(ActivityKind.Driver, $"Changing a driver with a save point: {actionId} (restored automatically if the device does not start)");
        Directory.CreateDirectory(PendingDir);
        // Written before the change starts, so a crash or forced restart mid-change is caught next time.
        File.WriteAllText(Path.Combine(PendingDir, id), instanceId);
        var code = await Task.Run(() => RunElevated(actionId, instanceId, id));
        var change = actionId == "driver.rollback" ? "rolled back" : "installed the Windows Update driver";
        var detail = code switch
        {
            null => Declined,
            0 => $"{change}; save point {id}",
            3 => actionId == "driver.rollback"
                ? "no previous driver to restore; nothing was changed"
                : "Windows Update had no driver for this device, or could not be reached; nothing was changed",
            4 => $"{change}, but the device did not start correctly, so the save point was restored automatically (save point {id})",
            6 => $"{change}, but the device did not start correctly, and restoring the save point FAILED. Save point {id} is kept for a helper.",
            _ => $"could not create a save point (exit code {code}); nothing was changed",
        };
        Activity.Report(ActivityKind.Driver, code switch
        {
            0 => "The driver change finished and the device started correctly",
            4 => "The device did not start correctly, so the save point was restored automatically",
            6 => "The device did not start correctly and the save point could not be restored; it is kept for a helper",
            3 => "No driver change was possible; nothing was changed",
            null => "The driver change was not started because the permission prompt was declined",
            _ => "The driver change could not start; nothing was changed",
        });
        // Only a live change (0) or a failed restore (6) leaves something to settle.
        if (code is not (0 or 6)) File.Delete(Path.Combine(PendingDir, id));
        return (code == 0, detail, code is 0 or 6 ? id : null);
    }

    /// <summary>keep: the person confirmed the fix, close the save point. Otherwise restore it. True when the system is settled.</summary>
    public static async Task<bool> SettleSavePointAsync(string id, bool keep)
    {
        Activity.Report(ActivityKind.Driver, keep ? "Closing the driver save point because the change was confirmed" : "Restoring driver save point");
        var marker = Path.Combine(PendingDir, id);

        // 0 = put back (the executor then deletes the save point), 3 = nothing had changed. Anything else keeps the marker.
        if (!keep && await Task.Run(() => RunElevated("driver.restore", id)) is not (0 or 3)) return false;
        File.Delete(marker);
        return true;
    }
}
