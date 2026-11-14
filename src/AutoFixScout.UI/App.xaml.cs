using System.Windows;

namespace AutoFixScout.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Started by the installer after an update that found the weekly task switched on: put the task back and exit.
        if (e.Args.Contains("--weekly-restore", StringComparer.OrdinalIgnoreCase))
        {
            try { AutoFixScout.Weekly.WeeklyCheckupSetup.On(); } catch (Exception) { }
            Environment.Exit(0);
        }
        // Started by the scheduled task: no window, no theme, no single-instance check. Exits when the check is done.
        if (e.Args.Contains(AutoFixScout.Weekly.WeeklyCheckupSetup.Argument, StringComparer.OrdinalIgnoreCase))
        {
            RunWeeklyCheckup();
            return; // base.OnStartup is not called, so StartupUri never opens the main window
        }
        // For screenshots on machines whose GPU path is unavailable (remote sessions, broken display driver).
        if (Environment.GetEnvironmentVariable("AUTOFIXSCOUT_SOFTWARE_RENDER") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        ThemeManager.ApplyPalette(); // before the window is built, so it never flashes the wrong theme
        base.OnStartup(e);
    }

    private void RunWeeklyCheckup()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Last resort: whatever happens, the process is gone within two minutes.
        var watchdog = new System.Threading.Timer(_ => Environment.Exit(0), null, TimeSpan.FromSeconds(110), Timeout.InfiniteTimeSpan);
        _ = Task.Run(async () =>
        {
            try { await AutoFixScout.Weekly.WeeklyCheckup.RunAsync(WeeklyToast.Show); }
            catch (Exception) { }
            await Task.Delay(1500); // lets Windows take the notification before the process ends
            watchdog.Dispose();
            Environment.Exit(0);
        });
    }
}
