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
    public static async Task<(bool Ok, string Detail)> RunAsync(Rung rung)
    {
        try
        {
            switch (rung.ActionId)
            {
                case "ui.open_settings_page":
                    // Only fixed pages; the person flips the setting themselves (spec 8.1).
                    var uri = rung.Arg switch
                    {
                        "sound" => "ms-settings:sound",
                        "camera" => "ms-settings:privacy-webcam",
                        "airplane" => "ms-settings:network-airplanemode",
                        "wifi" => "ms-settings:network-wifi",
                        "camera_app" => "microsoft.windows.camera:",
                        _ => throw new ArgumentException(rung.Arg),
                    };
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                    return (true, uri);
                case "ui.human_step":
                    return (true, "person was asked to do it");
                case "audio.unmute_and_set_volume":
                    return (true, Unmute());
                case "network.flush_dns":
                    // Flushing the resolver cache doesn't need admin, so no UAC prompt.
                    return await Task.Run(() => RunFixed("ipconfig.exe", "/flushdns"));
                case "service.restart":
                case "device.disable_enable":
                case "device.enable":
                case "network.reset_dns":
                case "device.rescan":
                    var code =await Task.Run(() => RunElevated(rung.ActionId, rung.Arg!));
                    return (code == 0, code is null ? Declined : $"executor exit code {code}");
                default:
                    return (false, $"action not allowed: {rung.ActionId}");
            }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static (bool Ok, string Detail) RunFixed(string exe, string arg)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), arg) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return (p.ExitCode == 0, $"{exe} {arg} exit code {p.ExitCode}");
    }

    /// <summary>Returns the prior state so the report records what to restore (spec 7.1 undo).</summary>
    private static string Unmute()
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var v = dev.AudioEndpointVolume;
        var before = $"before: muted={v.Mute}, volume={v.MasterVolumeLevelScalar * 100:0}%";
        v.Mute = false;
        if (v.MasterVolumeLevelScalar < 0.3f) v.MasterVolumeLevelScalar = 0.5f;
        return before;
    }

    /// <summary>audio.play_test_tone: 1.5 s, 440 Hz on the default output.</summary>
    public static void PlayTestTone()
    {
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
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; }
    }

    private const string Declined = "person declined the Windows permission prompt";

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
                ? "no previous driver to go back to; nothing changed"
                : "Windows Update had no driver for this device, or couldn't be reached; nothing changed",
            4 => $"{change}, but the device didn't come up right, so it was put back automatically (save point {id})",
            6 => $"{change}, the device didn't come up right, and putting it back FAILED. Save point {id} is kept for a helper.",
            _ => $"couldn't make a save point (exit code {code}); nothing changed",
        };
        // Only a live change (0) or a failed restore (6) leaves something to settle.
        if (code is not (0 or 6)) File.Delete(Path.Combine(PendingDir, id));
        return (code == 0, detail, code is 0 or 6 ? id : null);
    }

    /// <summary>keep: the person confirmed the fix, close the save point. Otherwise restore it. True when the system is settled.</summary>
    public static async Task<bool> SettleSavePointAsync(string id, bool keep)
    {
        var marker = Path.Combine(PendingDir, id);
        if (!keep && await Task.Run(() => RunElevated("driver.restore", id)) != 0) return false;
        File.Delete(marker);
        return true;
    }
}
