using System.Diagnostics;
using System.Windows;
using AutoFixScout.Probes;
using AutoFixScout.Triage;

namespace AutoFixScout.UI;

public partial class MainWindow : Window, IFixHost
{
    private const string AppName = "AutoFix Scout";
    private readonly IAudioProbe _audioProbe = new RealAudioProbe();
    private readonly INetworkProbe _networkProbe = new RealNetworkProbe();
    private readonly ICameraProbe _cameraProbe = new RealCameraProbe();
    private string? _lastReportPath;
    private TaskCompletionSource<bool>? _choice;
    private Func<Task<bool>> _test = () => Task.FromResult(false);

    public MainWindow() => InitializeComponent();

    // ---- screens ----

    /// <summary>Shows one sentence and up to two buttons; completes with true for the first button.</summary>
    private Task<bool> ShowStepAsync(string text, string? primary, string? secondary = null)
    {
        HomePanel.Visibility = Visibility.Collapsed;
        StepPanel.Visibility = Visibility.Visible;
        StepText.Text = text;
        PrimaryButton.Content = primary;
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        if (primary is not null) PrimaryButton.Focus();
        _choice = new TaskCompletionSource<bool>();
        return _choice.Task;
    }

    /// <summary>A screen with no buttons, for "Checking..." and "Working on it...".</summary>
    private void ShowBusy(string text) => _ = ShowStepAsync(text, null);

    private void ShowHome()
    {
        StepPanel.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
    }

    private void OnPrimaryClicked(object sender, RoutedEventArgs e) => _choice?.TrySetResult(true);

    // Esc lands here too (IsCancel); ignore it when the screen has no second choice.
    private void OnSecondaryClicked(object sender, RoutedEventArgs e)
    {
        if (SecondaryButton.Visibility == Visibility.Visible) _choice?.TrySetResult(false);
    }

    // ---- one flow for every symptom (spec 4.1) ----

    private async Task RunFlowAsync(string topic, string what, Func<Task<Diagnosis>> diagnose, Func<Task<bool>> test)
    {
        try
        {
            ShowBusy("Checking your computer...");
            var before = await diagnose();
            await ShowStepAsync(before.Findings[0].PlainMessage, "Next");

            var log = new List<StepLog>();
            var after = before;
            var outcome = Outcome.NotFixed;
            if (before.Ladder.Count > 0)
            {
                _test = test;
                outcome = await LadderRunner.RunAsync(before.Ladder, this, log);
                if (outcome == Outcome.NotFixed)
                {
                    ShowBusy("Checking your computer again...");
                    after = await diagnose();
                }
            }
            if (outcome == Outcome.NotFixed && after.ProbablyHardware) outcome = Outcome.ProbableHardware;

            var report = HelperReport.Build(topic, before.State, before.Findings, log.Select(l => l.ToString()), outcome);
            var logError = SaveSession(topic == "sound" ? "audio" : topic, log, outcome);
            if (logError != null) report += $"\nSession log unavailable: {logError}\n";
            _lastReportPath = HelperReport.Save(topic, report);
            var end = outcome == Outcome.NotFixed && after.NotFixedHint is { } hint ? hint : EndMessage(outcome, what);
            await ShowStepAsync(end, "Done");
        }
        catch (Exception ex)
        {
            await ShowStepAsync($"I couldn't finish the check.\n\n({ex.Message})", "Done");
        }
        ShowHome();
    }

    /// <summary>
    /// Writes the session to SQLite. A broken log must not stop the person getting help, so failure is reported, not thrown.
    /// shortcut: written once at the end, so a crash mid-flow loses that session's steps; write per step if pilots crash.
    /// </summary>
    private static string? SaveSession(string symptom, List<StepLog> steps, Outcome outcome)
    {
        try
        {
            using var db = new SessionLog(SessionLog.DefaultPath);
            var id = db.Start(symptom);
            db.AddSteps(id, steps);
            db.End(id, outcome);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string EndMessage(Outcome outcome, string what) => outcome switch
    {
        Outcome.Fixed => $"Your {what} is working again.",
        Outcome.ProbableHardware => "This is probably a hardware fault. A repair shop can check it. " +
                                    "Press \"Details for my helper\" to see a report you can show them.",
        Outcome.Abandoned => "Okay, nothing else was changed. You can come back any time.",
        _ => "I couldn't fix it this time. Press \"Details for my helper\" for a report you can send to someone who can help.",
    };

    // ---- no sound ----

    private async void OnSoundClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync("sound", "sound", DiagnoseSoundAsync, BeepTestAsync);

    private async Task<Diagnosis> DiagnoseSoundAsync()
    {
        // Probes are read-only (T0); run off the UI thread because WMI can take seconds.
        var s = await Task.Run(_audioProbe.Read);
        var f = AudioTriage.Evaluate(s);
        return new Diagnosis(s, f, AudioLadder.For(s, f), AudioLadder.ProbablyHardware(s));
    }

    private async Task<bool> BeepTestAsync()
    {
        await ShowStepAsync("Now I'll play a short beep. Please listen.", "Play the beep");
        ShowBusy("Playing the beep...");
        try { await Task.Run(FixActions.PlayTestTone); }
        catch (Exception) { return false; } // no usable output device: counts as not fixed
        return await ShowStepAsync("Did you hear the beep?", "Yes, I heard it", "No");
    }

    // ---- can't get online ----

    private async void OnNetworkClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync("network", "internet connection", DiagnoseNetworkAsync, ConnectionTestAsync);

    private async Task<Diagnosis> DiagnoseNetworkAsync()
    {
        var s = await Task.Run(_networkProbe.ReadAsync);
        var f = NetworkTriage.Evaluate(s);
        return new Diagnosis(s, f, NetworkTriage.Ladder(s, f), NetworkTriage.ProbablyHardware(s), NetworkTriage.NotFixedHint(f));
    }

    /// <summary>Real test: the same connectivity check Windows uses. Retries because Wi-Fi takes a few seconds to come up.</summary>
    private async Task<bool> ConnectionTestAsync()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ShowBusy("Checking the connection...");
            if ((await Task.Run(_networkProbe.ReadAsync)).InternetReachable == Tri.Yes) return true;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        return false;
    }

    // ---- camera ----

    private async void OnCameraClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync("camera", "camera", DiagnoseCameraAsync, CameraTestAsync);

    private async Task<Diagnosis> DiagnoseCameraAsync()
    {
        var s = await Task.Run(_cameraProbe.Read);
        var f = CameraTriage.Evaluate(s);
        return new Diagnosis(s, f, CameraTriage.Ladder(s, f), CameraTriage.ProbablyHardware(s));
    }

    /// <summary>Real test: the built-in Camera app shows a live picture. shortcut: no in-app preview, add one with MediaCapture if pilots find the app switch confusing.</summary>
    private async Task<bool> CameraTestAsync()
    {
        await ShowStepAsync("Now I'll open the Camera app so you can check.", "Open the Camera app");
        var (ok, _) = await FixActions.RunAsync(new Rung("camera_test", "ui.open_settings_page", 0, "", "camera_app"));
        if (!ok) return false;
        return await ShowStepAsync("Can you see yourself in the Camera app? You can close it afterwards.", "Yes, I can see myself", "No");
    }

    // ---- IFixHost ----

    Task<bool> IFixHost.ConfirmAsync(Rung rung) => ShowStepAsync(rung.Say, "Yes, try it", "Not now");

    async Task<(bool Ok, string Detail)> IFixHost.RunAsync(Rung rung)
    {
        ShowBusy("Working on it...");
        var result = await FixActions.RunAsync(rung);
        // Steps the person does themselves: wait until they say they're done before testing.
        if (rung.ActionId is "ui.open_settings_page" or "ui.human_step" && result.Ok)
            await ShowStepAsync("When you've done that, come back here and press Next.", "Next");
        return result;
    }

    Task<bool> IFixHost.TestAsync() => _test();

    // ---- other ----

    private void OnHelperDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null)
            MessageBox.Show(this, "Run a check first, then the details will be ready here.", AppName);
        else
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_lastReportPath}\""));
    }

    private void OnOtherClicked(object sender, RoutedEventArgs e) => Stub("Other");
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => Stub("Settings");

    private void Stub(string name) => MessageBox.Show(this, $"{name} is not wired up yet.", AppName);
}
