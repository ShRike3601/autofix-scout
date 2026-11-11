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
    private readonly Dictionary<string, string> _savePoints = new(); // rung id -> open save point id

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            CloseUnfinishedSessions();
            await RecoverOpenSavePointsAsync();
        };
    }

    /// <summary>
    /// A save point still open at startup means a driver change was never confirmed (crash, restart, app closed).
    /// Put it back, as promised before the change.
    /// </summary>
    private async Task RecoverOpenSavePointsAsync()
    {
        var open = FixActions.OpenSavePoints();
        if (open.Count == 0) return;
        var restore = await ShowStepAsync(
            "Last time I changed a driver, I didn't get to finish checking it. To be safe, I'd like to put the old driver back. " +
            "Windows will ask for permission.", "Put it back", "Not now");
        if (!restore) { ShowHome(); return; }
        ShowBusy("Putting your driver back. This can take a minute...");
        var failed = 0;
        foreach (var id in open)
            if (!await FixActions.SettleSavePointAsync(id, keep: false)) failed++;
        await ShowStepAsync(failed == 0
            ? "Done. Your driver is back the way it was."
            : "I couldn't put the driver back. Please ask your helper; the save point is kept so they can do it.", "OK",
            look: failed == 0 ? Look.Fixed : Look.Problem);
        ShowHome();
    }

    // ---- screens ----

    /// <summary>Shows one sentence and up to two buttons; completes with true for the first button.</summary>
    /// <param name="look">Only changes the picture above the sentence (see MainWindow.Visuals.cs).</param>
    private Task<bool> ShowStepAsync(string text, string? primary, string? secondary = null, Look look = Look.Step)
    {
        var from = CaptureScreen();
        HomePanel.Visibility = Visibility.Collapsed;
        StepPanel.Visibility = Visibility.Visible;
        StepText.Text = text;
        PrimaryButton.Content = primary;
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        PresentStep(from, primary is null ? Look.Busy : look);
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
        PresentHome();
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
        SetTopic(topic);
        var sessionId = StartSession(topic == "sound" ? "audio" : topic);
        try
        {
            ShowBusy("Checking your computer...");
            var before = await diagnose();
            await ShowStepAsync(before.Findings[0].PlainMessage, "Next");

            var log = new List<StepLog>();
            var after = before;
            var outcome = await LadderRunner.NoFaultAsync(before.Findings, test) ?? Outcome.NotFixed;
            if (outcome != Outcome.NoFaultFound && before.Ladder.Count > 0)
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
            var logError = FinishSession(sessionId, log, outcome);
            if (logError != null) report += $"\nSession log unavailable: {logError}\n";
            _lastReportPath = HelperReport.Save(topic, report);
            var end = outcome == Outcome.NotFixed && after.NotFixedHint is { } hint ? hint : EndMessage(outcome, what);
            await ShowStepAsync(end, "Done", look: EndLook(outcome));
        }
        catch (Exception ex)
        {
            FinishSession(sessionId, new List<StepLog>(), Outcome.Abandoned);
            await ShowStepAsync($"I couldn't finish the check.\n\n({ex.Message})", "Done", look: Look.Problem);
        }
        ShowHome();
    }

    // A broken log must not stop the person getting help, so session-log failures are reported, never thrown.
    // shortcut: steps are written at the end, so a flow cut off mid-way is logged as abandoned without its steps;
    // write per step if pilots crash.

    /// <summary>Opens the session row when the flow starts. Null if the log can't be written.</summary>
    private static string? StartSession(string symptom)
    {
        try
        {
            using var db = new SessionLog(SessionLog.DefaultPath);
            return db.Start(symptom, DateTime.UtcNow);
        }
        catch (Exception) { return null; }
    }

    private static string? FinishSession(string? sessionId, List<StepLog> steps, Outcome outcome)
    {
        if (sessionId is null) return "could not open session.db";
        try
        {
            using var db = new SessionLog(SessionLog.DefaultPath);
            db.AddSteps(sessionId, steps);
            db.End(sessionId, outcome);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Sessions left open by a flow that was cut off (app closed, crash) become "abandoned".</summary>
    private static void CloseUnfinishedSessions()
    {
        try
        {
            using var db = new SessionLog(SessionLog.DefaultPath);
            db.CloseUnfinished();
        }
        catch (Exception) { }
    }

    private static string EndMessage(Outcome outcome, string what) => outcome switch
    {
        Outcome.Fixed => $"Your {what} is working again.",
        Outcome.NoFaultFound => $"Your {what} seems to be working fine, so I didn't change anything. " +
                                "If the problem comes back, run the check again while it's happening.",
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

    // T2 needs an explicit "I understand" step in Simple mode (spec 6).
    Task<bool> IFixHost.ConfirmAsync(Rung rung) =>
        ShowStepAsync(rung.Say, rung.Tier >= 2 ? "I understand, go ahead" : "Yes, try it", "Not now");

    async Task<(bool Ok, string Detail)> IFixHost.RunAsync(Rung rung)
    {
        if (FixActions.IsDriverChange(rung.ActionId))
        {
            ShowBusy(rung.ActionId == "driver.rollback"
                ? "Making a save point and changing the driver. This can take a minute..."
                : "Making a save point and asking Windows Update for a driver. This can take a few minutes...");
            var (ok, detail, savePoint) = await FixActions.ChangeDriverAsync(rung.ActionId, rung.Arg!);
            if (savePoint != null) _savePoints[rung.Id] = savePoint;
            return (ok, detail);
        }
        ShowBusy("Working on it...");
        var result = await FixActions.RunAsync(rung);
        // Steps the person does themselves: wait until they say they're done before testing.
        if (rung.ActionId is "ui.open_settings_page" or "ui.human_step" && result.Ok)
            await ShowStepAsync("When you've done that, come back here and press Next.", "Next");
        return result;
    }

    Task<bool> IFixHost.TestAsync() => _test();

    async Task<bool> IFixHost.SettleAsync(Rung rung, bool keep)
    {
        if (!_savePoints.TryGetValue(rung.Id, out var id)) return true; // nothing was changed
        if (!keep) ShowBusy("That didn't help, so I'm putting the old driver back...");
        var settled = await FixActions.SettleSavePointAsync(id, keep);
        if (settled) _savePoints.Remove(rung.Id);
        return settled;
    }

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
