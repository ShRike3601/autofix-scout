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
                    return await Task.Run(() => RunElevated(rung.ActionId, rung.Arg!));
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
    private static (bool Ok, string Detail) RunElevated(string actionId, string arg)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "AutoFixScout.Executor.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add(actionId);
        psi.ArgumentList.Add(arg);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return (p.ExitCode == 0, $"executor exit code {p.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (false, "person declined the Windows permission prompt"); }
    }
}
