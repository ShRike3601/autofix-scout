using System.Windows;

namespace AutoFixScout.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // For screenshots on machines whose GPU path is unavailable (remote sessions, broken display driver).
        if (Environment.GetEnvironmentVariable("AUTOFIXSCOUT_SOFTWARE_RENDER") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        ThemeManager.ApplyPalette(); // before the window is built, so it never flashes the wrong theme
        base.OnStartup(e);
    }
}
