using System.Windows;

namespace AutoFixScout.UI;

/// <summary>
/// Application entry point (section 5).
/// </summary>
public partial class App : Application
{
    public App()
    {
        // No global exception handlers yet — let errors surface for debugging
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var orchestrator = new Orchestrator(new ProbesReal());  // read-only probes with real system access

        var mainWindow = new MainWindow(orchestrator);
        mainWindow.Show();
    }
}
