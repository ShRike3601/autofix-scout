using System.Windows;

namespace AutoFixScout.UI;

public partial class App : Application
{
    private bool _quiet; // the scheduled weekly check has no window, so no message box
    private int _boxes;

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallProblemLog();
        _quiet = e.Args.Contains(AutoFixScout.Weekly.WeeklyCheckupSetup.Argument, StringComparer.OrdinalIgnoreCase)
                 || e.Args.Contains("--weekly-restore", StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Writes unexpected errors to the local problem log (AppLog) so that testers can send something useful.</summary>
    private void InstallProblemLog()
    {
        AppLog.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            AppLog.Error("unhandled", a.ExceptionObject as Exception ?? new InvalidOperationException("A problem without details occurred."));
        TaskScheduler.UnobservedTaskException += (_, a) => { AppLog.Error("task", a.Exception); a.SetObserved(); };
        DispatcherUnhandledException += (_, a) =>
        {
            AppLog.Error("screen", a.Exception);
            a.Handled = true; // the app stays open where it safely can
            if (_quiet || Interlocked.Increment(ref _boxes) > 3) return; // never a flood of boxes
            try
            {
                MessageBox.Show("AutoFix Scout ran into a problem and will continue. The details were saved for the person helping you.",
                    "AutoFix Scout", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception) { }
        };
    }

    private void RunWeeklyCheckup()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Last resort: whatever happens, the process is gone within two minutes.
        var watchdog = new System.Threading.Timer(_ => Environment.Exit(0), null, TimeSpan.FromSeconds(110), Timeout.InfiniteTimeSpan);
        _ = Task.Run(async () =>
        {
            try { await AutoFixScout.Weekly.WeeklyCheckup.RunAsync(WeeklyToast.Show); }
            catch (Exception ex) { AppLog.Error("weekly", ex); }
            await Task.Delay(1500); // lets Windows take the notification before the process ends
            watchdog.Dispose();
            Environment.Exit(0);
        });
    }
}
