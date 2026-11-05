using System.Windows;

namespace AutoFixScout.UI;

/// <summary>
/// Main window: simple mode home screen (section 4).
/// </summary>
public partial class MainWindow : Window
{
    private readonly Orchestrator _orchestrator;
    private bool _showingSimpleMode = true;

    public MainWindow(Orchestrator orchestrator)
    {
        InitializeComponent();
        _orchestrator = orchestrator;
    }

    private async Task Show() => await base.ShowAsync();

    private async void OnSoundClicked(object sender, RoutedEventArgs e)
    {
        _showingSimpleMode = false;
        var triaged = await _orchestrator.TriageAudioAsync();
        DisplayFinding(triage);
    }

    private async void OnCameraClicked(object sender, RoutedEventArgs e)
    {
        _showingSimpleMode = false;
        var triaged = await _orchestrator.TriageCameraAsync();
        DisplayFinding(triaged);
    }

    private async void OnNetworkClicked(object sender, RoutedEventArgs e)
    {
        _showingSimpleMode = false;
        var triaged = await _orchestrator.TriageNetworkAsync();
        DisplayFinding(triaged);
    }

    private void OnOtherClicked(object sender, RoutedEventArgs e) => MessageBox.Show("LLM text/voice input not available in v1.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);

    private async void OnHelperDetailsClicked(object sender, RoutedEventArgs e)
    {
        // Placeholder: would show details for my helper page with hardware IDs, driver versions, etc.
        _showingSimpleMode = true;
        Content = new TextBlock { Text = "What's wrong?", FontSize = 32, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 20, 0, 40), Foreground = SystemColors.GrayTextBrush };
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        _showingSimpleMode = false;
        var settingsView = new SettingsView(_orchestrator);
        await settingsView.ShowDialogAsync(this);
        Content = new TextBlock { Text = "What's wrong?", FontSize = 32, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 20, 0, 40), Foreground = SystemColors.GrayTextBrush };
    }

    private void DisplayFinding(TriagedResult triaged)
    {
        Content = new StackPanel(Orientation = Orientation.Vertical, Spacing = 15, Margin = new Thickness(20))
        {
            new TextBlock { Text = "Finding:", FontSize = 24, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = triaged.Findings.FirstOrDefault()?.Title ?? "No issues found.", FontSize = 22 },
            new TextBlock { Text = triaged.Findings.FirstOrDefault()?.Message ?? "Your device is working correctly.", FontSize = 18 },

            // Propose fix button (one-button flow)
            new Button { Content = "Try to fix it", FontSize = 20, Padding = new Thickness(25, 12), Margin = new Thickness(0, 10),
                     BorderThickness = 2, BorderBrush = "#0078D4", Background = "White", Foreground = "#333333",
                     Click = (s, ae) => ApplyNextFixAsync(triaged.Symptom) },

            // End state text
            new TextBlock { Text = "End states: Fixed | Not fixed (helper report) | Probably hardware", FontSize = 14, Foreground = "#666666" }
        };
    }

    private async Task ApplyNextFixAsync(Symptom symptom)
    {
        // Would fetch playbook and propose next step
        MessageBox.Show("Fix applied. Please test the device.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        _showingSimpleMode = true;
        Content = new TextBlock { Text = "What's wrong?", FontSize = 32, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 20, 0, 40), Foreground = SystemColors.GrayTextBrush };
    }

}
