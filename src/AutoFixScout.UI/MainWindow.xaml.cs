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
    private bool _historyOpen; // the "History and reports" page is showing
    private TaskCompletionSource<bool>? _choice;
    private Func<Task<bool>> _test = () => Task.FromResult(false);
    private IReadOnlyList<Finding> _before = Array.Empty<Finding>(); // findings of the current flow, for its real test
    private readonly Dictionary<string, string> _savePoints = new(); // rung id -> open save point id

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        RestorePlacement();
        DecideTour();
        Loaded += async (_, _) =>
        {
            CloseUnfinishedSessions();
            _ = BuildSidebarAsync();
            ShowTourIfNeeded();
            // For screenshots and testing: AUTOFIXSCOUT_OPEN=settings opens the settings panel at start.
            // "graphics" starts the read-only graphics check; "graphics-log" also unfolds the activity list.
            switch (Environment.GetEnvironmentVariable("AUTOFIXSCOUT_OPEN"))
            {
                case "settings": SetSettingsOpen(true); break;
                case "settings-end": SetSettingsOpen(true); _ = Dispatcher.InvokeAsync(() => (SettingsBody.Parent as System.Windows.Controls.ScrollViewer)?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded); break;
                case "graphics": OnGraphicsClicked(this, new RoutedEventArgs()); break;
                case "netquality": OnConnectionQualityClicked(this, new RoutedEventArgs()); break;
                case "graphics-log": OnGraphicsClicked(this, new RoutedEventArgs()); EnsureActivityPanel(); SetActivityOpen(true); break;
            }
            await RecoverOpenSavePointsAsync();
            await ShowStartupNoticesAsync();
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
        SetTopic("driver");
        var restore = await ShowStepAsync(
            "The last driver change made by AutoFix Scout was not confirmed as working. To keep your computer safe, " +
            "AutoFix Scout recommends restoring the previous driver. Windows will ask for your permission.", "Restore driver", "Not now");
        if (!restore) { ShowHome(); return; }
        ShowBusy("Restoring the previous driver. This can take a minute.");
        var failed = 0;
        foreach (var id in open)
            if (!await FixActions.SettleSavePointAsync(id, keep: false)) failed++;
        await ShowStepAsync(failed == 0
            ? "The previous driver has been restored."
            : "AutoFix Scout could not restore the previous driver. The save point has been kept so that the person helping you can restore it.",
            "Continue", look: failed == 0 ? Look.Fixed : Look.Problem);
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
        PresentStep(from, primary is null ? (look == Look.Scan ? Look.Scan : Look.Busy) : look);
        if (primary is not null) PrimaryButton.Focus();
        _choice = new TaskCompletionSource<bool>();
        return _choice.Task;
    }

    /// <summary>A screen with no buttons, for "Checking your computer" and "Applying the fix".</summary>
    /// <param name="scan">Also lists what the check looks at (only for the read-only check itself).</param>
    private void ShowBusy(string text, bool scan = false) => _ = ShowStepAsync(text, null, look: scan ? Look.Scan : Look.Busy);

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
        Journey(Stage.Check);
        var sessionId = StartSession(topic == "sound" ? "audio" : topic);
        try
        {
            ShowBusy("Checking your computer", scan: true);
            var before = await diagnose();
            _before = before.Findings;
            Journey(Stage.Diagnosis);
            await ShowStepAsync(before.Findings[0].PlainMessage, "Continue", look: FindingLook(before.Findings[0].Code));

            var log = new List<StepLog>();
            var after = before;
            var outcome = await LadderRunner.NoFaultAsync(before.Findings, test) ?? Outcome.NotFixed;
            if (outcome != Outcome.NoFaultFound && before.Ladder.Count > 0)
            {
                _test = test;
                outcome = await LadderRunner.RunAsync(before.Ladder, this, log);
                if (outcome == Outcome.NotFixed)
                {
                    ShowBusy("Checking your computer again", scan: true);
                    after = await diagnose();
                }
            }
            if (outcome == Outcome.NotFixed && after.ProbablyHardware) outcome = Outcome.ProbableHardware;

            var report = HelperReport.Build(topic, before.State, before.Findings, log.Select(l => l.ToString()), outcome);
            var logError = FinishSession(sessionId, log, outcome);
            if (logError != null) report += $"\nSession log unavailable: {logError}\n";
            HelperReport.Save(topic, report); // the plain text copy stays for anyone who prefers it
            try { _lastReportPath = HelperReport.SaveHtml(HelperReport.BuildHtmlNow(topic, before.Findings, log.Select(l => l.ToString()), outcome, AppVersion, _checkStarted)); }
            catch (Exception) { _lastReportPath = HelperReport.Save(topic, report); }
            var end = outcome == Outcome.NotFixed && after.NotFixedHint is { } hint ? hint : EndMessage(outcome, what);
            Journey(Stage.Done, warn: outcome is not (Outcome.Fixed or Outcome.NoFaultFound));
            await ShowStepAsync(end, "Finish", look: EndLook(outcome));
        }
        catch (Exception ex)
        {
            FinishSession(sessionId, new List<StepLog>(), Outcome.Abandoned);
            Journey(Stage.Done, warn: true);
            await ShowStepAsync($"AutoFix Scout could not complete the check.\n\nTechnical detail: {ex.Message}", "Finish", look: Look.Problem);
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
        Outcome.Fixed => $"The problem has been resolved. Your {what} is working again.",
        Outcome.NoFaultFound => $"No problem was found with your {what}, so no changes were made. " +
                                "If the problem returns, please run the check again while it is happening.",
        Outcome.ProbableHardware => "The problem is most likely caused by the hardware. A repair service can examine the device. " +
                                    "Please select \"Report for your helper\" to view a report that you can show them.",
        Outcome.Abandoned => "No further changes were made. You can return to AutoFix Scout at any time.",
        _ => "AutoFix Scout could not resolve this problem. A report has been prepared for the person helping you. " +
             "Please select \"Report for your helper\" to view it.",
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
        Journey(Stage.Confirm);
        await ShowStepAsync("AutoFix Scout will now play a short test sound. Please listen carefully.", "Play test sound");
        ShowBusy("Playing the test sound");
        try { await Task.Run(FixActions.PlayTestTone); }
        catch (Exception) { return false; } // no usable output device: counts as not fixed
        return await ShowStepAsync("Did you hear the test sound?", "Yes, I heard it", "No, I did not");
    }

    // ---- can't get online ----

    private async void OnNetworkClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync("network", "internet connection", DiagnoseNetworkAsync, ConnectionTestAsync);

    private async Task<Diagnosis> DiagnoseNetworkAsync()
    {
        var s = await Task.Run(_networkProbe.ReadAsync);
        var f = NetworkTriage.Evaluate(s);
        var network = new Diagnosis(s, f, NetworkTriage.Ladder(s, f), NetworkTriage.ProbablyHardware(s), NetworkTriage.NotFixedHint(f));
        // Proxy, hosts file, clock and network add-ons: the software causes when the connection itself looks fine.
        try { return HealthTriage.CombineNetwork(network, await Task.Run(new RealHealthProbe().Read)); }
        catch (Exception) { return network; }
    }

    /// <summary>Real test: the same connectivity check Windows uses. Retries because Wi-Fi takes a few seconds to come up.</summary>
    private async Task<bool> ConnectionTestAsync()
    {
        Journey(Stage.Confirm);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ShowBusy("Testing the internet connection");
            if ((await Task.Run(_networkProbe.ReadAsync)).InternetReachable == Tri.Yes)
                return !_before.Any(x => x.Code.ToString().StartsWith("Health")) || await Areas.HealthFlow.AutoTestAsync(_before);
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
        Journey(Stage.Confirm);
        await ShowStepAsync("AutoFix Scout will now open the Camera app so that you can check the picture.", "Open the Camera app");
        var (ok, _) = await FixActions.RunAsync(new Rung("camera_test", "ui.open_settings_page", 0, "", "camera_app"));
        if (!ok) return false;
        return await ShowStepAsync("Can you see yourself in the Camera app? You may close the Camera app afterwards.",
            "Yes, I can see myself", "No, I cannot");
    }

    // ---- IFixHost ----

    // T2 needs an explicit "I understand" step in Simple mode (spec 6).
    Task<bool> IFixHost.ConfirmAsync(Rung rung)
    {
        Journey(Stage.Fix);
        return ShowStepAsync(rung.Say, rung.Tier >= 2 ? "I understand, apply fix" : "Apply fix", "Not now");
    }

    async Task<(bool Ok, string Detail)> IFixHost.RunAsync(Rung rung)
    {
        Journey(Stage.Fix);
        if (FixActions.IsDriverChange(rung.ActionId))
        {
            ShowBusy(rung.ActionId == "driver.rollback"
                ? "Creating a save point and changing the driver. This can take a minute."
                : "Creating a save point and requesting a driver from Windows Update. This can take several minutes.");
            var (ok, detail, savePoint) = await FixActions.ChangeDriverAsync(rung.ActionId, rung.Arg!);
            if (savePoint != null) _savePoints[rung.Id] = savePoint;
            return (ok, detail);
        }
        ShowBusy("Applying the fix");
        var result = await FixActions.RunAsync(rung);
        // Steps the person does themselves: wait until they say they're done before testing.
        if (rung.ActionId is "ui.open_settings_page" or "ui.human_step" && result.Ok)
            await ShowStepAsync("When you have finished, please return to this window and select Continue.", "Continue");
        return result;
    }

    Task<bool> IFixHost.TestAsync() => _test();

    async Task<bool> IFixHost.SettleAsync(Rung rung, bool keep)
    {
        if (!_savePoints.TryGetValue(rung.Id, out var id)) return true; // nothing was changed
        if (!keep) ShowBusy("The change did not resolve the problem, so AutoFix Scout is restoring the previous driver.");
        var settled = await FixActions.SettleSavePointAsync(id, keep);
        if (settled) _savePoints.Remove(rung.Id);
        return settled;
    }


    // ---- describe the problem ----

    private void OnSymptomTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        SymptomHint.Visibility = SymptomBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnSymptomKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        OnSymptomSearch(sender, e);
    }

    /// <summary>Suggests the best check for the description, then starts it if the person agrees.</summary>
    private async void OnSymptomSearch(object sender, RoutedEventArgs e)
    {
        var text = SymptomBox.Text.Trim();
        if (text.Length == 0) { SymptomBox.Focus(); return; }
        // The closest checks, best first (SymptomRouter.Rank); the full check-up is always offered as the way forward when none fits.
        var rank = Areas.SymptomRouter.Rank(text);
        var matched = rank.Candidates.Any(c => c.Score > 0);
        var areas = rank.Candidates.Select(c => c.Route.Area).ToList();
        var options = rank.Candidates.Select(c => new Choice(AreaGlyph(c.Route.Area), c.Route.Title, c.Covers)).ToList();
        if (!areas.Contains("health"))
        {
            areas.Add("health");
            options.Add(new Choice(AreaGlyph("health"), "None of these", "The full computer check-up examines the whole computer instead."));
        }
        SetTopic(null);
        _topicTitle = "Find a fix";
        var pick = await ShowChoicesAsync(
            !matched ? "AutoFix Scout could not match your description to a specific check. The full computer check-up is the best place to start."
            : rank.NoGoodMatch ? "AutoFix Scout is not certain which check fits your description. Please select the nearest one, or run the full computer check-up."
            : "These checks are the closest match to your description. Please select the one that fits best.",
            options, "Back");
        SymptomBox.Text = "";
        if (pick < 0) { ShowHome(); return; }
        Action<object, RoutedEventArgs> run = areas[pick] switch
        {
            "sound" => OnSoundClicked,
            "camera" => OnCameraClicked,
            "network" => OnNetworkClicked,
            "microphone" => OnMicrophoneClicked,
            "bluetooth" => OnBluetoothClicked,
            "shell" => OnShellClicked,
            "store" => OnStoreClicked,
            "security" => OnSecurityClicked,
            "power" => OnPowerClicked,
            "input" => OnInputClicked,
            "office" => OnOfficeClicked,
            "onedrive" => OnOneDriveClicked,
            "install" => OnInstallClicked,
            "notifications" => OnNotificationsClicked,
            "program" => OnProgramClosingClicked,
            "storage" => OnStorageClicked,
            "update" => OnUpdateClicked,
            "printer" => OnPrinterClicked,
            "scanner" => OnScannerClicked,
            "display" => OnDisplayClicked,
            "performance" => OnPerfClicked,
            "graphics" => OnGraphicsClicked,
            "netquality" => OnConnectionQualityClicked,
            _ => OnHealthClicked,
        };
        run(sender, e);
    }
    // ---- more areas (home screen "Other problems") ----

    /// <summary>The person's own answer is the real test where no automatic check exists.</summary>
    private Task<bool> AskAsync(string question)
    {
        Journey(Stage.Confirm);
        return ShowStepAsync(question, "Yes", "No");
    }

    private async Task<bool> AutoCheckAsync(Func<Task<bool>> check)
    {
        Journey(Stage.Confirm);
        ShowBusy("Checking whether the problem has been resolved");
        return await check();
    }

    private async void OnHealthClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.HealthFlow.Topic, Areas.HealthFlow.What, Areas.HealthFlow.DiagnoseAsync,
            async () => Areas.HealthFlow.UsesAutoTest(_before)
                ? await AutoCheckAsync(() => Areas.HealthFlow.AutoTestAsync(_before)) && await AskAsync(Areas.HealthFlow.TestQuestion)
                : await AskAsync(Areas.HealthFlow.TestQuestion));


    /// <summary>
    /// "A program keeps closing": the full check-up, but first the person confirms which recorded program it is, so the
    /// fix steps target that program. Asked once per flow (the re-check after the fixes keeps the answer).
    /// </summary>
    private async void OnProgramClosingClicked(object sender, RoutedEventArgs e)
    {
        string? chosen = null;
        var asked = false;
        async Task<Diagnosis> DiagnoseProgramAsync()
        {
            var d = await Areas.HealthFlow.DiagnoseAsync();
            var s = (HealthState)d.State;
            if (!asked)
            {
                asked = true;
                var candidates = HealthTriage.CrashCandidates(s);
                if (candidates.Count == 0)
                    await ShowStepAsync(Areas.HealthFlow.NoCrashRecorded, "Continue");
                foreach (var c in candidates)
                    if (await ShowStepAsync(Areas.HealthFlow.ProgramQuestion(c), "Yes, this program", "No")) { chosen = c.App; break; }
                ShowBusy("Preparing the next steps");
            }
            s.ChosenApp = chosen;
            return Areas.HealthFlow.Diagnose(s);
        }
        await RunFlowAsync(Areas.HealthFlow.Topic, Areas.HealthFlow.What, DiagnoseProgramAsync,
            () => AskAsync("Does the program work without closing now?"));
    }
    private async void OnBluetoothClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.BluetoothFlow.Topic, Areas.BluetoothFlow.What, Areas.BluetoothFlow.DiagnoseAsync,
            async () => await AutoCheckAsync(Areas.BluetoothFlow.AutoTestAsync) && await AskAsync(Areas.BluetoothFlow.TestQuestion));

    private async void OnShellClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.ShellFlow.Topic, Areas.ShellFlow.What, Areas.ShellFlow.DiagnoseAsync,
            () => AskAsync(Areas.ShellFlow.TestQuestion));

    private async Task RunAppsAsync(AppsFocus focus) =>
        await RunFlowAsync(Areas.AppsFlow.Topic, Areas.AppsFlow.What, () => Areas.AppsFlow.DiagnoseAsync(focus),
            () => AskAsync(Areas.AppsFlow.TestQuestionFor(focus)));

    private async void OnStoreClicked(object sender, RoutedEventArgs e) => await RunAppsAsync(AppsFocus.Store);
    private async void OnOfficeClicked(object sender, RoutedEventArgs e) => await RunAppsAsync(AppsFocus.Office);
    private async void OnOneDriveClicked(object sender, RoutedEventArgs e) => await RunAppsAsync(AppsFocus.OneDrive);
private async void OnInstallClicked(object sender, RoutedEventArgs e) => await RunAppsAsync(AppsFocus.Install);    private async void OnNotificationsClicked(object sender, RoutedEventArgs e) =>        await RunFlowAsync(Areas.ShellFlow.Topic, "notifications", () => Areas.ShellFlow.DiagnoseAsync(notificationFocus: true),            () => AskAsync(Areas.ShellFlow.NotificationTestQuestion));

    private async void OnSecurityClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.SecurityFlow.Topic, Areas.SecurityFlow.What, Areas.SecurityFlow.DiagnoseAsync,
            () => AskAsync(Areas.SecurityFlow.TestQuestion));

    private async void OnPowerClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.PowerFlow.Topic, Areas.PowerFlow.What, Areas.PowerFlow.DiagnoseAsync,
            () => AskAsync(Areas.PowerFlow.TestQuestion));

    private async void OnInputClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.InputFlow.Topic, Areas.InputFlow.What, Areas.InputFlow.DiagnoseAsync,
            () => AskAsync(Areas.InputFlow.TestQuestion));

    /// <summary>The same check with the keyboard layout findings first (the person reported wrong characters).</summary>
    private async void OnPerfClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.PerfFlow.Topic, Areas.PerfFlow.What, Areas.PerfFlow.DiagnoseAsync,
            () => AskAsync(Areas.PerfFlow.TestQuestion));

    private async void OnConnectionQualityClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.ConnectionQualityFlow.Topic, Areas.ConnectionQualityFlow.What, () => Areas.ConnectionQualityFlow.DiagnoseAsync(),
            () => AskAsync(Areas.ConnectionQualityFlow.TestQuestion));

    private async void OnGraphicsClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.GraphicsFlow.Topic, Areas.GraphicsFlow.What, Areas.GraphicsFlow.DiagnoseAsync,
            () => AskAsync(Areas.GraphicsFlow.TestQuestion));

    private async void OnTypingClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.InputFlow.Topic, Areas.InputFlow.What, () => Areas.InputFlow.DiagnoseAsync(typingFocus: true),
            () => AskAsync(Areas.InputFlow.TestQuestion));

    private async void OnMicrophoneClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.MicrophoneFlow.Topic, Areas.MicrophoneFlow.What, Areas.MicrophoneFlow.DiagnoseAsync, async () =>
        {
            Journey(Stage.Confirm);
            await ShowStepAsync(Areas.MicrophoneFlow.TestPrompt, "Start the test");
            ShowBusy("Listening to the microphone");
            return await Areas.MicrophoneFlow.AutoTestAsync();
        });

    private async void OnStorageClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.StorageFlow.Topic, Areas.StorageFlow.What, Areas.StorageFlow.DiagnoseAsync,
            async () => Areas.StorageFlow.UsesAutoTest(_before)
                ? await AutoCheckAsync(Areas.StorageFlow.AutoTestAsync)
                : await AskAsync(Areas.StorageFlow.TestQuestion));

    private async void OnUpdateClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.UpdateFlow.Topic, Areas.UpdateFlow.What, Areas.UpdateFlow.DiagnoseAsync,
            async () => await AutoCheckAsync(Areas.UpdateFlow.AutoTestAsync) && await AskAsync(Areas.UpdateFlow.TestQuestion));

    private async void OnPrinterClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.PrinterFlow.Topic, Areas.PrinterFlow.What, () => Areas.PrinterFlow.DiagnoseAsync(),
            () => AskAsync(Areas.PrinterFlow.TestQuestion));
private async void OnScannerClicked(object sender, RoutedEventArgs e) =>        await RunFlowAsync(Areas.PrinterFlow.Topic, "scanner", () => Areas.PrinterFlow.DiagnoseAsync(scanFocus: true),            () => AskAsync(Areas.PrinterFlow.ScanTestQuestion));

    private async void OnDisplayClicked(object sender, RoutedEventArgs e) =>
        await RunFlowAsync(Areas.DisplayFlow.Topic, Areas.DisplayFlow.What, Areas.DisplayFlow.DiagnoseAsync,
            () => AskAsync(Areas.DisplayFlow.TestQuestion));

    // ---- other ----

    private void OnHelperDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null)
            MessageBox.Show(this, "A report will be available here after a check has been completed.", AppName);
        else
            Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true }); // .html opens in the default browser
    }

    /// <summary>"History" in the sidebar: changes with Undo and recent checks (MainWindow.Extras.cs).</summary>
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (!_historyOpen) ShowHistory();
    }

}
