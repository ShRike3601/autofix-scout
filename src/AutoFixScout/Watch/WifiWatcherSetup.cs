using System.Diagnostics;
using AutoFixScout.Probes;
using Microsoft.Win32;

namespace AutoFixScout.Watching;

/// <summary>
/// Switches the Wi-Fi watcher on and off for the signed-in person: one HKCU Run value (so it starts with Windows and appears
/// in Settings, Apps, Startup) and the process itself. No administrator rights.
/// </summary>
public static class WifiWatcherSetup
{
    public const string ValueName = "AutoFixScout Wi-Fi watcher";
    public const string ExeName = "AutoFixScout.Watch.exe";
    public const string ProcessName = "AutoFixScout.Watch";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string ExePath(string? baseDir = null) => Path.Combine(baseDir ?? AppContext.BaseDirectory, ExeName);

    /// <summary>The Run value: the full path in quotes, nothing else.</summary>
    public static string RunCommand(string exePath) => "\"" + exePath.Trim('"') + "\"";

    /// <summary>Yes = registered to start with Windows; No = not registered but the program is installed; Unknown = program not found.</summary>
    public static Tri ReadState(RegistryKey hkcu, string exePath)
    {
        using var k = hkcu.OpenSubKey(RunKey);
        if (k?.GetValue(ValueName) is string { Length: > 0 }) return Tri.Yes;
        return File.Exists(exePath) ? Tri.No : Tri.Unknown;
    }

    public static Tri ReadState() => ReadState(Registry.CurrentUser, ExePath());

    public static (bool Ok, string Detail) TurnOn(RegistryKey hkcu, string exePath, bool start)
    {
        if (!File.Exists(exePath)) return (false, $"{ExeName} was not found next to the app");
        using var k = hkcu.CreateSubKey(RunKey);
        k.SetValue(ValueName, RunCommand(exePath), RegistryValueKind.String);
        // A second copy exits at once (single instance), so starting again is harmless.
        if (start)
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exePath)! })?.Dispose();
        return (true, "Wi-Fi watcher registered to start with Windows" + (start ? " and started" : ""));
    }

    public static (bool Ok, string Detail) TurnOff(RegistryKey hkcu, bool stop)
    {
        using (var k = hkcu.OpenSubKey(RunKey, writable: true)) k?.DeleteValue(ValueName, throwOnMissingValue: false);
        var stopped = 0;
        if (stop)
            foreach (var p in Process.GetProcessesByName(ProcessName))
                using (p)
                {
                    try { p.Kill(); p.WaitForExit(3000); stopped++; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
        return (true, $"Wi-Fi watcher removed from start-up; {stopped} running copy stopped");
    }

    public static (bool Ok, string Detail) TurnOn() => TurnOn(Registry.CurrentUser, ExePath(), start: true);
    public static (bool Ok, string Detail) TurnOff() => TurnOff(Registry.CurrentUser, stop: true);
}
