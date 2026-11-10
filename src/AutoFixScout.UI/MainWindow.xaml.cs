using System.Diagnostics;
using System.Windows;
using AutoFixScout.Probes;
using AutoFixScout.Triage;

namespace AutoFixScout.UI;

public partial class MainWindow : Window
{
    private readonly IAudioProbe _audioProbe = new RealAudioProbe();
    private string? _lastReportPath;

    public MainWindow() => InitializeComponent();

    private async void OnSoundClicked(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        button.IsEnabled = false;
        Title = "AutoFix Scout - Checking your computer...";
        try
        {
            // Probes are read-only (T0); run off the UI thread because WMI can take seconds.
            var state = await Task.Run(_audioProbe.Read);
            var findings = AudioTriage.Evaluate(state);
            _lastReportPath = HelperReport.Save(HelperReport.Build(state, findings));
            MessageBox.Show(findings[0].PlainMessage, "AutoFix Scout");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"We couldn't finish checking your sound.\n\n({ex.Message})", "AutoFix Scout");
        }
        finally
        {
            Title = "AutoFix Scout";
            button.IsEnabled = true;
        }
    }

    private void OnHelperDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null)
            MessageBox.Show("Run a check first, then the details will be ready here.", "AutoFix Scout");
        else
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_lastReportPath}\""));
    }

    private void OnCameraClicked(object sender, RoutedEventArgs e) => Stub("Camera");
    private void OnNetworkClicked(object sender, RoutedEventArgs e) => Stub("Network");
    private void OnOtherClicked(object sender, RoutedEventArgs e) => Stub("Other");
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => Stub("Settings");

    private static void Stub(string name) =>
        MessageBox.Show($"{name} is not wired up yet.", "AutoFix Scout");
}
