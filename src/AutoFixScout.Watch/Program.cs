using AutoFixScout.Watching;

namespace AutoFixScout.Watching;

/// <summary>
/// AutoFix Scout Wi-Fi watcher. No window, no network access, no administrator rights. Started with Windows by an HKCU Run
/// value that the person switches on in AutoFix Scout; see WifiWatcher.cs for what it does.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        // One copy per signed-in person; a second start (the Run value plus the app's own start) exits at once.
        using var mutex = new Mutex(true, @"Local\AutoFixScout.WifiWatcher", out var first);
        if (!first) return 0;
        using var stop = new ManualResetEventSlim(false);
        using var watcher = new WifiWatcher(new WifiWatchStore());
        watcher.Start();
        // Runs until the person switches it off (the app stops the process) or signs out.
        stop.Wait();
        return 0;
    }
}
