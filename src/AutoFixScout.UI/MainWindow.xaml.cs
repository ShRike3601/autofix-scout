using System.Diagnostics;
using System.Windows;
using AutoFixScout.Probes;
using AutoFixScout.Triage;

namespace AutoFixScout.UI;

public partial class MainWindow : Window, IFixHost
{
    private const string AppName = "AutoFix Scout";
    private readonly IAudioProbe _audioProbe = new RealAudioProbe();
    private string? _lastReportPath;

    public MainWindow() => InitializeComponent();

    private async void OnSoundClicked(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        button.IsEnabled = false;
        try
        {
            Title = $"{AppName} - Checking your computer...";
            // Probes are read-only (T0); run off the UI thread because WMI can take seconds.
            var state = await Task.Run(_audioProbe.Read);
            var findings = AudioTriage.Evaluate(state);
            Title = AppName;
            MessageBox.Show(this, findings[0].PlainMessage, AppName);

            var log = new List<string>();
            var outcome = await LadderRunner.RunAsync(AudioLadder.For(state, findings), this, log);

            var after = outcome == Outcome.Fixed ? state : await Task.Run(_audioProbe.Read);
            if (outcome == Outcome.NotFixed && AudioLadder.ProbablyHardware(after)) outcome = Outcome.ProbableHardware;

            var report = HelperReport.Build(state, findings) + "\nWhat was tried:\n" +
                         string.Join("\n", log.Select(l => "  " + l)) + $"\n\nOutcome: {outcome}\n";
            _lastReportPath = HelperReport.Save(report);
            MessageBox.Show(this, EndMessage(outcome), AppName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"We couldn't finish checking your sound.\n\n({ex.Message})", AppName);
        }
        finally
        {
            Title = AppName;
            button.IsEnabled = true;
        }
    }

    private static string EndMessage(Outcome outcome) => outcome switch
    {
        Outcome.Fixed => "Your sound is working again.",
        Outcome.ProbableHardware => "This is probably a hardware fault. A repair shop can check it. " +
                                    "Press \"Details for my helper\" to see a report you can show them.",
        Outcome.Abandoned => "Okay, nothing else was changed. You can come back any time.",
        _ => "I couldn't fix it this time. Press \"Details for my helper\" for a report you can send to someone who can help.",
    };

    // IFixHost: one decision per message, plain language, Yes = "try it".
    Task<bool> IFixHost.ConfirmAsync(Rung rung) => Task.FromResult(
        MessageBox.Show(this, rung.Say + "\n\nPress Yes to try it, or No for not now.", AppName, MessageBoxButton.YesNo) == MessageBoxResult.Yes);

    Task<(bool Ok, string Detail)> IFixHost.RunAsync(Rung rung) => AudioActions.RunAsync(rung);

    async Task<bool> IFixHost.TestAsync()
    {
        MessageBox.Show(this, "Now I'll play a short beep. Press OK when you're ready to listen.", AppName);
        try { await Task.Run(AudioActions.PlayTestTone); }
        catch (Exception) { return false; } // no usable output device: counts as not fixed
        return MessageBox.Show(this, "Did you hear the beep?", AppName, MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }

    private void OnHelperDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null)
            MessageBox.Show(this, "Run a check first, then the details will be ready here.", AppName);
        else
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_lastReportPath}\""));
    }

    private void OnCameraClicked(object sender, RoutedEventArgs e) => Stub("Camera");
    private void OnNetworkClicked(object sender, RoutedEventArgs e) => Stub("Network");
    private void OnOtherClicked(object sender, RoutedEventArgs e) => Stub("Other");
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => Stub("Settings");

    private static void Stub(string name) =>
        MessageBox.Show($"{name} is not wired up yet.", AppName);
}
