using System.Diagnostics;
using System.Text.RegularExpressions;
using AutoFixScout.Probes;
using NAudio.CoreAudioApi;

namespace AutoFixScout;

/// <summary>Per-app sound sessions of the default output (the "Volume mixer"). Read and change need no admin.</summary>
public static partial class AppSound
{
    /// <summary>A session volume below this percentage counts as "very low".</summary>
    public const int LowPercent = 5;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex NamePattern();

    /// <summary>Process names the action accepts: letters, digits, dot, dash and underscore only.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Reads the sessions of the default render device. The system sounds session is kept only when it is muted.</summary>
    public static List<AudioSessionInfo> ReadSessions(MMDevice device)
    {
        var result = new List<AudioSessionInfo>();
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var muted = s.SimpleAudioVolume.Mute;
            if (s.IsSystemSoundsSession && !muted) continue;
            var process = ProcessName(s);
            if (process is null && !s.IsSystemSoundsSession) continue;
            var display = s.IsSystemSoundsSession ? "System sounds" : s.DisplayName;
            result.Add(new AudioSessionInfo(process ?? "System sounds", string.IsNullOrWhiteSpace(display) || display.StartsWith('@') ? null : display,
                (int)Math.Round(s.SimpleAudioVolume.Volume * 100), muted, s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive));
        }
        return result;
    }

    private static string? ProcessName(AudioSessionControl s)
    {
        try { return Process.GetProcessById((int)s.GetProcessID).ProcessName; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// audio.unmute_app: unmutes every session of the named process on the default output and lifts a volume below
    /// <see cref="LowPercent"/> % to 100 %. Returns the previous values for the report. Fails when no session matches.
    /// </summary>
    public static (bool Ok, string Detail) Unmute(string? processName)
    {
        if (!IsValidName(processName)) return (false, "invalid application name");
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = dev.AudioSessionManager.Sessions;
        var before = new List<string>();
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.IsSystemSoundsSession || !string.Equals(ProcessName(s), processName, StringComparison.OrdinalIgnoreCase)) continue;
            var v = s.SimpleAudioVolume;
            before.Add($"muted={v.Mute}, level={v.Volume * 100:0}%");
            v.Mute = false;
            if (v.Volume * 100 < LowPercent) v.Volume = 1f;
        }
        return before.Count == 0
            ? (false, $"no current sound session for {processName}")
            : (true, $"{processName} before: " + string.Join("; ", before));
    }
}
