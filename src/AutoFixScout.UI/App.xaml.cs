using System.Windows;

namespace AutoFixScout.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.ApplyPalette(); // before the window is built, so it never flashes the wrong theme
        base.OnStartup(e);
    }
}
