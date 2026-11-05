using System.Windows;

namespace AutoFixScout.UI;

/// <summary>
/// Settings view: telemetry opt-in, LLM toggle, T2 approval (section 15).
/// </summary>
public partial class SettingsView : Window
{
    private readonly Orchestrator _orchestrator;

    public SettingsView(Orchestrator orchestrator)
    {
        InitializeComponent();
        _orchestrator = orchestrator;
    }

    public async Task ShowDialogAsync(Window owner)
    {
        await base.ShowDialogAsync(owner);
        SaveSettings();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        // Save settings (telemetry opt-in, LLM key)
        var telemetryOptIn = TelemetryOptIn.IsChecked ?? false;
        
        _orchestrator.Dispose();

        this.Close();
        await System.Windows.Application.Current?.ShutdownAsync();
    }
}
