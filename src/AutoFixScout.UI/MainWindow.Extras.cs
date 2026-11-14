using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AutoFixScout.Areas;
using AutoFixScout.Probes;
using AutoFixScout.Stress;
using AutoFixScout.Triage;
using AutoFixScout.Updates;
using AutoFixScout.Watching;

namespace AutoFixScout.UI;

/// <summary>
/// Screens around the checks (owned by the UI work, like MainWindow.Visuals.cs): the short load test, the update banner,
/// the "What is new" card, and the "History" page (changes with Undo, recent checks) and the Settings panel.
/// </summary>
public partial class MainWindow
{
    // ---- short load test: consent, progress with Stop, then the normal result screens ----

    private Diagnosis? _stressLast;
    private bool _stressFresh; // the last run found nothing and has not yet been used as the "does it work" answer

    private async void OnStressClicked(object sender, RoutedEventArgs e)
    {
        SetTopic(StressFlow.Topic);
        var bullets = string.Join("\n", StressFlow.ConsentBullets.Select(b => "•  " + b));
        var start = await ShowStepAsync(
            "AutoFix Scout will put the computer under heavy load and measure whether it slows down.\n\n" +
            bullets + "\n\nYou can stop the test at any time. The test does not change any setting.", "Start test", "Not now");
        if (!start) { ShowHome(); return; }
        _stressLast = null;
        _stressFresh = false;
        // The flow asks for a diagnosis again after an unsuccessful fix: the re-test below has already produced it.
        await RunFlowAsync(StressFlow.Topic, StressFlow.What, async () =>
        {
            if (_stressLast is null) await RunStressTestAsync();
            var d = _stressLast!;
            _stressLast = null;
            return d;
        }, async () =>
        {
            // "Does it work now" is answered by the test itself. Straight after a clean first run that answer exists already.
            if (_stressFresh && _stressLast is null) { _stressFresh = false; return true; }
            _stressFresh = false;
            await RunStressTestAsync();
            return Normal(_stressLast!);
        });
    }

    private static bool Normal(Diagnosis d) => d.Findings.Count > 0 && d.Findings.All(f => LadderRunner.IsAllClear(f.Code));

    /// <summary>Runs the load test once (about 2 minutes) behind a progress screen with a "Stop" button. Sets _stressLast.</summary>
    private async Task RunStressTestAsync()
    {
        using var cts = new CancellationTokenSource();
        var live = true;
        const string note = "Please do not unplug the charger.";

        var stop = ShowStepAsync("Measuring the idle state", null, "Stop", Look.Busy);
        StepButtons.Visibility = Visibility.Visible; // a busy screen normally has no buttons
        StepDetail.TextAlignment = TextAlignment.Center;
        StepDetail.Text = note;
        StepDetail.Visibility = Visibility.Visible;
        _ = stop.ContinueWith(_ =>
        {
            if (!live) return;
            cts.Cancel();
            StepText.Text = "Stopping the test";
            SecondaryButton.Visibility = Visibility.Collapsed;
        }, TaskScheduler.FromCurrentSynchronizationContext());

        var progress = new Progress<StressSample>(s =>
        {
            if (!live || cts.IsCancellationRequested) return; // a late reading must not overwrite the next screen
            StepText.Text = s.Phase switch
            {
                StressPhase.Cpu => "Loading the processor",
                StressPhase.Gpu => "Loading the graphics card",
                StressPhase.Cooldown => "Cooling down",
                _ => "Measuring the idle state",
            };
            StepDetail.Text =
                $"{Math.Min(s.Seconds, 105):0} of about 105 seconds\n" +
                $"Processor speed limit: {(s.CpuLimitPercent is { } c ? $"{c:0} %" : "n/a")}\n" +
                $"Graphics card temperature: {(s.GpuTemperatureC is { } t ? $"{t} °C" : "n/a")}\n\n{note}";
        });

        try { _stressLast = await StressFlow.DiagnoseAsync(progress, cts.Token); }
        finally { live = false; }
        _stressFresh = Normal(_stressLast);
    }

    // ---- start-up notices on the home page: what is new (once per version) and a newer version ----

    private readonly UpdateService _updates = new();
    private UpdateInfo? _update;

    private async Task ShowStartupNoticesAsync()
    {
        try
        {
            if (new WhatsNew().TakeOnce() is { } news)
            {
                WhatsNewTitle.Text = $"What is new in version {news.Version}";
                // The notes open with their own "What is new in ..." line; the card already has that heading.
                var lines = news.Text.Split('\n')
                    .SkipWhile(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("What is new", StringComparison.OrdinalIgnoreCase));
                WhatsNewText.Text = string.Join("\n", lines).Trim();
                WhatsNewCard.Visibility = Visibility.Visible;
            }
            _update = await Task.Run(() => _updates.CheckAsync()); // null when offline, switched off, or a development build
            if (_update is null) return;
            UpdateText.Text = $"Version {_update.Version} is ready.";
            UpdateBar.Visibility = Visibility.Visible;
        }
        catch (Exception) { /* a notice is never worth an error at start-up */ }
    }

    private void OnWhatsNewCloseClicked(object sender, RoutedEventArgs e) => WhatsNewCard.Visibility = Visibility.Collapsed;

    private void OnAppUpdateLaterClicked(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    /// <summary>Downloads the update and starts its installer. Only ever runs from the click.</summary>
    private async void OnAppUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        UpdateButton.IsEnabled = false;
        UpdateLaterButton.Visibility = Visibility.Collapsed;
        UpdateText.Text = "Downloading the update: 0%";
        string? path = null;
        try
        {
            path = await _updates.DownloadAsync(_update, new Progress<double>(p => UpdateText.Text = $"Downloading the update: {p * 100:0}%"));
        }
        catch (Exception) { }
        if (path is null)
        {
            UpdateFailed("The update could not be downloaded. Please try again later.");
            return;
        }
        UpdateText.Text = "Windows will ask for your permission to install the update.";
        if (_updates.StartSetup(path)) Application.Current.Shutdown();
        else UpdateFailed("The update was not installed.");
    }

    private void UpdateFailed(string message)
    {
        UpdateText.Text = message;
        UpdateButton.IsEnabled = true;
        UpdateLaterButton.Visibility = Visibility.Visible;
    }

    // ---- History page ----

    private void ShowHistory(string? message = null)
    {
        SetTopic(null);
        _historyOpen = true;
        HomePanel.Visibility = Visibility.Collapsed;
        StepPanel.Visibility = Visibility.Collapsed;
        HeaderHome.Visibility = Visibility.Collapsed;
        HeaderStep.Visibility = Visibility.Visible;
        HeaderTitle.Text = "History";
        HeaderTitle.Visibility = Visibility.Visible;
        BuildHistory(message);
        if (HistoryPanel.Visibility != Visibility.Visible)
        {
            HistoryPanel.Visibility = Visibility.Visible;
            Motion.Enter(HistoryPanel, distance: 12);
        }
    }

    private void BuildHistory(string? message)
    {
        HistoryPanel.Children.Clear();

        // Changes AutoFix Scout made, newest first, each with Undo where that is still possible.
        HistoryPanel.Children.Add(Section("Changes made by AutoFix Scout", first: true));
        var changes = new StackPanel();
        if (message is not null)
        {
            var note = Text(message, "BodyText");
            note.Margin = new Thickness(0, 0, 0, 10);
            AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
            changes.Children.Add(note);
        }
        var entries = new ChangeHistory().List();
        if (entries.Count == 0)
            changes.Children.Add(Text("AutoFix Scout has not changed anything on this computer yet.", "BodyText"));
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            FrameworkElement side;
            if (entry.Undone) side = Text("Undone", "CaptionText");
            else if (entry.CanUndo)
            {
                var undo = SmallButton("Undo");
                AutomationProperties.SetName(undo, $"Undo: {entry.Description}");
                undo.Click += async (_, _) =>
                {
                    undo.IsEnabled = false;
                    var result = await new ChangeHistory().UndoAsync(entry.StepId);
                    if (_historyOpen) ShowHistory(result.Message);
                };
                side = undo;
            }
            else side = new Border();
            var caption = $"{entry.WhenUtc.ToLocalTime():d MMM yyyy, HH:mm}";
            if (!entry.Undone && !entry.CanUndo && entry.Hint is { Length: > 0 } hint) caption += $"  ·  {hint}";
            changes.Children.Add(Row(entry.Description, caption, side, divider: i > 0));
        }
        if (entries.Any(en => en.CanUndo && !en.Undone))
        {
            var driverNote = Text("When a driver change is undone, Windows asks for your permission and the previous driver is restored.", "CaptionText");
            driverNote.Margin = new Thickness(0, 12, 0, 0);
            changes.Children.Add(driverNote);
        }
        HistoryPanel.Children.Add(Card(changes));

        // Recent checks and their outcome.
        HistoryPanel.Children.Add(Section("Recent checks"));
        IReadOnlyList<SessionLog.SessionSummary> recent;
        try { using var db = new SessionLog(SessionLog.DefaultPath); recent = db.Recent(8); }
        catch (Exception) { recent = Array.Empty<SessionLog.SessionSummary>(); }
        var checks = new StackPanel();
        if (recent.Count == 0) checks.Children.Add(Text("No checks have been carried out on this computer yet.", "BodyText"));
        for (var i = 0; i < recent.Count; i++)
            checks.Children.Add(Row(HistoryText.Symptom(recent[i].Symptom), $"{recent[i].StartedUtc.ToLocalTime():d MMM yyyy, HH:mm}",
                Text(Sentence(HistoryText.Outcome(recent[i].Outcome)), "CaptionText"), divider: i > 0));
        var folder = SmallButton("Open the reports folder");
        folder.HorizontalAlignment = HorizontalAlignment.Left;
        folder.Margin = new Thickness(0, 14, 0, 0);
        folder.Click += (_, _) =>
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFixScout");
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\""));
        };
        checks.Children.Add(folder);
        var kept = Text("Reports for the person helping you, and copies of the settings AutoFix Scout changed, are kept on this computer only.", "CaptionText");
        kept.Margin = new Thickness(0, 8, 0, 0);
        checks.Children.Add(kept);
        HistoryPanel.Children.Add(Card(checks));
    }

    // ---- Settings panel (opens over any page, also during a check) ----

    private void BuildSettings(string? message = null)
    {
        SettingsBody.Children.Clear();
        if (message is not null)
        {
            var note = Text(message, "BodyText");
            note.Margin = new Thickness(0, 0, 0, 12);
            AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
            SettingsBody.Children.Add(note);
        }
        AddAppearanceSettings();
        SettingsBody.Children.Add(Section("Updates and Wi-Fi"));
        var settings = new StackPanel();
        var appSettings = AppSettings.Load();
        var updates = Check("Check for updates when AutoFix Scout starts", appSettings.CheckForUpdates);
        updates.Click += (_, _) =>
        {
            var s = AppSettings.Load();
            s.CheckForUpdates = updates.IsChecked == true;
            s.Save();
        };
        settings.Children.Add(updates);
        settings.Children.Add(Helper("AutoFix Scout asks github.com once at start whether a newer version exists. No personal data is sent."));

        Tri watcher;
        try { watcher = WifiWatcherSetup.ReadState(); } catch (Exception) { watcher = Tri.Unknown; }
        settings.Children.Add(Divider());
        if (watcher == Tri.Yes)
        {
            var off = SmallButton("Switch off");
            AutomationProperties.SetName(off, "Switch off the Wi-Fi watcher");
            off.Click += async (_, _) =>
            {
                off.IsEnabled = false;
                var (ok, _) = await FixActions.RunAsync(new Rung("wifi_watch_off", "network.wifi_watch_off", 1, ""));
                BuildSettings(ok ? "The Wi-Fi watcher has been switched off." : "The Wi-Fi watcher could not be switched off.");
            };
            var summary = "It reconnects your Wi-Fi automatically.";
            try
            {
                if (new WifiWatchStore().Summarize(DateTime.UtcNow) is { } w)
                    summary += $" In the last {w.Days} days: {w.Drops} dropped connections, {w.ReconnectedByWatcher} reconnected by the watcher.";
            }
            catch (Exception) { }
            settings.Children.Add(Row("The Wi-Fi watcher is on.", summary, off, divider: false));
            var notify = Check("Show a notification when AutoFix Scout reconnects Wi-Fi", WifiWatcherSetup.GetNotify());
            notify.Margin = new Thickness(0, 12, 0, 0);
            notify.Click += (_, _) => WifiWatcherSetup.SetNotify(notify.IsChecked == true);
            settings.Children.Add(notify);
        }
        else
            settings.Children.Add(Row("The Wi-Fi watcher is off.",
                "It can be switched on from the internet check when Wi-Fi keeps dropping.", new Border(), divider: false));
        AddWeeklyCheckupRow(settings);
        SettingsBody.Children.Add(Card(settings));
        AddDataSettings();

        var version = Text($"AutoFix Scout version {AppVersion}", "CaptionText");
        version.Margin = new Thickness(2, 14, 0, 0);
        SettingsBody.Children.Add(version);
    }

    private bool _settingsOpen;

    /// <summary>Esc closes the settings panel first (otherwise it would press the step screen's cancel button underneath).</summary>
    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_settingsOpen && e.Key == System.Windows.Input.Key.Escape) { SetSettingsOpen(false); e.Handled = true; return; }
        // Shortcuts: Ctrl+, settings, Ctrl+K the "describe the problem" box (home page), Ctrl+H history (not during a check).
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.OemComma:
                SetSettingsOpen(!_settingsOpen); e.Handled = true; break;
            case System.Windows.Input.Key.K when HomePanel.Visibility == Visibility.Visible:
                if (_settingsOpen) SetSettingsOpen(false);
                SymptomBox.Focus(); SymptomBox.SelectAll(); e.Handled = true; break;
            case System.Windows.Input.Key.H when NavHistory.IsEnabled:
                if (_settingsOpen) SetSettingsOpen(false);
                NavHistory.IsChecked = true; e.Handled = true; break;
        }
    }

    private void OnSettingsButtonClicked(object sender, RoutedEventArgs e) => SetSettingsOpen(!_settingsOpen);
    private void OnSettingsCloseClicked(object sender, RoutedEventArgs e) => SetSettingsOpen(false);
    private void OnSettingsScrimClicked(object sender, System.Windows.Input.MouseButtonEventArgs e) => SetSettingsOpen(false);

    /// <summary>Slides the settings panel in from the right (and the scrim fades in), or back out. The page below keeps its state.</summary>
    private void SetSettingsOpen(bool open)
    {
        _settingsOpen = open;
        if (open)
        {
            BuildSettings();
            SettingsScrim.Visibility = SettingsFlyout.Visibility = Visibility.Visible;
            Motion.FromTo(SettingsShift, System.Windows.Media.TranslateTransform.XProperty, 60, 0, 420, Motion.EaseOutStrong);
            Motion.FromTo(SettingsFlyout, OpacityProperty, 0, 1, 260);
            Motion.FromTo(SettingsScrim, OpacityProperty, 0, 1, 260);
            SettingsFlyout.Focus();
            System.Windows.Input.Keyboard.Focus(SettingsFlyout);
            return;
        }
        Motion.To(SettingsShift, System.Windows.Media.TranslateTransform.XProperty, 40, 200, Motion.EaseOut);
        Motion.To(SettingsScrim, OpacityProperty, 0, 200);
        Motion.To(SettingsFlyout, OpacityProperty, 0, 180);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(Motion.Enabled ? 210 : 0);
            if (!_settingsOpen) SettingsScrim.Visibility = SettingsFlyout.Visibility = Visibility.Collapsed;
        });
        SettingsButton.Focus();
    }

    private static string Sentence(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private TextBlock Text(string text, string style) => new() { Text = text, Style = (Style)FindResource(style) };

    private TextBlock Section(string text, bool first = false)
    {
        var label = Text(text, "SectionLabel");
        if (first) label.Margin = new Thickness(label.Margin.Left, 0, label.Margin.Right, label.Margin.Bottom);
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        return label;
    }

    private Border Card(UIElement child)
    {
        var card = new Border
        {
            CornerRadius = (CornerRadius)FindResource("CardRadius"), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14), Child = child,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        return card;
    }

    private Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        return line;
    }

    /// <summary>A list row: title with a muted line under it, and one element (button or status) on the right.</summary>
    private FrameworkElement Row(string title, string caption, FrameworkElement side, bool divider)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        var sub = Text(caption, "CaptionText");
        sub.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(sub);
        side.VerticalAlignment = VerticalAlignment.Center;
        side.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(side, 1);
        grid.Children.Add(text);
        grid.Children.Add(side);
        if (!divider) return grid;
        var stack = new StackPanel();
        var line = Divider();
        line.Margin = new Thickness(0, 10, 0, 10);
        stack.Children.Add(line);
        stack.Children.Add(grid);
        return stack;
    }

    private Button SmallButton(string text) => new()
    {
        Content = text, Style = (Style)FindResource("StandardButton"), MinWidth = 0, MinHeight = 30, Padding = new Thickness(14, 4, 14, 4),
    };

    private CheckBox Check(string text, bool on) => new() { Content = text, IsChecked = on, Style = (Style)FindResource("Check") };

    private TextBlock Helper(string text)
    {
        var helper = Text(text, "CaptionText");
        helper.Margin = new Thickness(28, 4, 0, 0);
        return helper;
    }
}
