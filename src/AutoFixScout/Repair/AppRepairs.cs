using System.Diagnostics;

namespace AutoFixScout.Repair;

/// <summary>
/// Microsoft Store and OneDrive repairs that run as the person, so no admin rights and no permission prompt. Each runs
/// a fixed program with fixed arguments; the OneDrive program is the one found on this computer, never a path passed in.
/// </summary>
public static class AppRepairs
{
    /// <summary>OneDrive.exe: the per-user install first (the usual one), then the per-machine installs.</summary>
    public static string? FindOneDrive()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe"),
        };
        return candidates.FirstOrDefault(p => p.Length > 0 && Path.IsPathFullyQualified(p) && File.Exists(p));
    }

    public static bool IsPerUser(string oneDrivePath) =>
        oneDrivePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);

    public static bool OneDriveRunning()
    {
        var ps = Process.GetProcessesByName("OneDrive");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>
    /// WSReset.exe without arguments: clears the Microsoft Store cache, then opens the Store. Waits up to 60 seconds.
    /// </summary>
    public static (bool Ok, string Detail) ResetStoreCache()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "WSReset.exe");
        if (!File.Exists(exe)) return (false, "WSReset.exe not found");
        Commands.Running(exe, "", "clears the Microsoft Store cache");
        using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true })!;
        if (!p.WaitForExit(60_000))
 return (true, "WSReset still running after 60 s; the Store opens when it has finished");
        return (p.ExitCode == 0, $"WSReset exit code {p.ExitCode}");
    }

    /// <summary>Starts OneDrive when it is installed and not running.</summary>
    public static (bool Ok, string Detail) StartOneDrive()
    {
        if (FindOneDrive() is not { } exe) return (false, "OneDrive not found");
        if (OneDriveRunning()) return (true, "OneDrive was already running");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
        Thread.Sleep(5000);
        var running = OneDriveRunning();
        return (running, $"OneDrive started: {running}");
    }

    /// <summary>
    /// OneDrive.exe /reset (Microsoft's documented sync reset: settings and sync state are rebuilt, files are kept), then
    /// starts OneDrive again after about 10 seconds if it did not restart by itself.
    /// </summary>
    public static (bool Ok, string Detail) ResetOneDrive()
    {
        if (FindOneDrive() is not { } exe) return (false, "OneDrive not found");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        Commands.Running(exe, "/reset", "OneDrive sync reset, files are kept");
        psi.ArgumentList.Add("/reset");
        using (var p = Process.Start(psi)!)
            if (!p.WaitForExit(60_000)) return (false, "OneDrive /reset did not finish within 60 s");
        Thread.Sleep(10_000);
        if (!OneDriveRunning())
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
            Thread.Sleep(5000);
        }
        var running = OneDriveRunning();
        return (running, $"OneDrive reset; running again: {running}");
    }
}
