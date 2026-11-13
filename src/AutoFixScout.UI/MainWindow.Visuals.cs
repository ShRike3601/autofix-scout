using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AutoFixScout.Triage;

namespace AutoFixScout.UI;

/// <summary>
/// Presentation only: theme hookup, title bar, sidebar, page header, journey indicator, the icon and banner around each sentence, the
/// checks list on the busy screen, page transitions. Nothing in here decides what the flow does; MainWindow.xaml.cs
/// calls PresentStep / PresentHome after it has set the screen's content, and Journey to say where the flow is.
/// </summary>
public partial class MainWindow
{
    /// <summary>The picture (and banner) around a step sentence.</summary>
    internal enum Look
    {
        Step, Busy, Scan,
        FindingOk, FindingInfo, FindingWarning, FindingError,
        Fixed, AllGood, NotFixed, Hardware, Problem, Neutral,
    }

    /// <summary>Where the person is in the journey shown in the header.</summary>
    internal enum Stage { None, Check, Diagnosis, Fix, Confirm, Done }

    private readonly record struct Screen(bool Home, Look Look, string Text);
    private sealed record JourneyStep(Ellipse Halo, Ellipse Circle, TextBlock Number, Path Tick, TextBlock Label, ScaleTransform? LineFill);
    private sealed record CheckRow(Ellipse Ring, Grid Spinner, RotateTransform SpinRotate, Grid Done, TextBlock Label);

    private static readonly string[] StageNames = ["Check", "Diagnosis", "Fix", "Confirm"];

    private Look _look = Look.Step;
    private string _shownText = "";
    private string? _topic;
    private string? _topicTitle;
    private Stage _stage = Stage.None;
    private bool _endWarn;
    private bool _spinning;
    private readonly List<JourneyStep> _journey = new();
    private readonly List<CheckRow> _checks = new();
    private int _checkIndex;
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        // Fit small or highly scaled screens (e.g. 1366x768 at 125%).
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 32);
        Width = Math.Min(Width, area.Width - 32);
        BuildJourney();
        _checkTimer.Tick += (_, _) => AdvanceChecks();
        StateChanged += (_, _) => OnWindowStateChanged();
        // Informational version carries the installer version (CI passes -p:Version); drop the "+commit" suffix.
        VersionText.Text = $"Version {AppVersion}";
    }

    internal static string AppVersion =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            System.Reflection.Assembly.GetExecutingAssembly())?.InformationalVersion.Split('+')[0] ?? "unknown";

    // ---- title bar and sidebar ----

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnCloseClicked(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnMaximizeClicked(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnWindowStateChanged()
    {
        var max = WindowState == WindowState.Maximized;
        // A maximised window with custom chrome extends past the screen by its resize border; pad the content back in.
        Root.Margin = max ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = ((char)(max ? 0xE923 : 0xE922)).ToString(); // Segoe Fluent Icons: ChromeRestore / ChromeMaximize
        AutomationProperties.SetName(MaximizeButton, max ? "Restore" : "Maximize");
    }

    /// <summary>"Troubleshoot" in the sidebar: closes the history page. During a check it is already the current page.</summary>
    private void OnNavHomeClicked(object sender, RoutedEventArgs e)
    {
        if (_historyOpen) _choice?.TrySetResult(false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.Attach(this);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Greeting.Text = DateTime.Now.Hour switch
        {
            < 5 or >= 18 => "Good evening",
            < 12 => "Good morning",
            _ => "Good afternoon",
        };
        if (HomePanel.Visibility == Visibility.Visible) AnimateHome();
    }

    private static Look EndLook(Outcome outcome) => outcome switch
    {
        Outcome.Fixed => Look.Fixed,
        Outcome.NoFaultFound => Look.AllGood,
        Outcome.ProbableHardware => Look.Hardware,
        Outcome.Abandoned => Look.Neutral,
        _ => Look.NotFixed,
    };

    /// <summary>Severity of a check result, for the banner on the result screen.</summary>
    private static Look FindingLook(FindingCode code) => code switch
    {
        FindingCode.LooksNormal => Look.FindingOk,
        FindingCode.ProbeIncomplete => Look.FindingInfo,
        FindingCode.Muted or FindingCode.VolumeVeryLow or FindingCode.DefaultIsHdmiOrBluetooth or FindingCode.AirplaneModeOn
            or FindingCode.NetAdapterDisabled or FindingCode.NotConnected or FindingCode.CameraDisabled
            or FindingCode.CameraPrivacyOff or FindingCode.CameraInUse => Look.FindingWarning,
        _ => Look.FindingError,
    };

    private void SetTopic(string? topic)
    {
        _topic = topic;
        _glyphOverride = null;
        _topicTitle = topic switch
        {
            "sound" => "Sound is not working",
            "camera" => "Camera is not working",
            "network" => "Cannot connect to the internet",
            "driver" => "Unfinished driver change",
            "health" => "Computer check-up",
            "microphone" => "Microphone",
            "bluetooth" => "Bluetooth",
            "shell" => "Start menu, taskbar and search",
            "apps" => "Apps and the Microsoft Store",
            "security" => "Security",
            "power" => "Battery, sleep and power",
            "input" => "Keyboard, mouse and touchpad",
            "storage" => "Slow or full computer",
            "update" => "Windows Update",
            "printer" => "Printer and scanner",
            "display" => "Screen and display",
            _ => null,
        };
    }

    // ---- journey: Check -> Diagnosis -> Fix -> Confirm ----

    private static readonly string[] StageNotes =
    [
        "AutoFix Scout examines your computer. Nothing is changed.",
        "The result of the check is explained.",
        "A fix is applied only with your permission.",
        "A test confirms whether the problem has been resolved.",
    ];

    /// <summary>Builds the vertical progress list in the panel beside the step card: dot, name and one line on what the stage does.</summary>
    private void BuildJourney()
    {
        for (var i = 0; i < StageNames.Length; i++)
        {
            var last = i == StageNames.Length - 1;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition());

            var dot = new Grid { Width = 28, Height = 28 };
            var halo = new Ellipse { StrokeThickness = 1.5, Opacity = 0.5 };
            halo.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            var circle = new Ellipse { StrokeThickness = 1, Margin = new Thickness(3) };
            var number = new TextBlock
            {
                Text = (i + 1).ToString(), FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var tick = new Path
            {
                Data = Geometry.Parse("M 9.8,14.3 L 12.7,17.2 L 18.3,11.4"), StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            tick.SetResourceReference(Shape.StrokeProperty, "OnAccentBrush");
            dot.Children.Add(halo);
            dot.Children.Add(circle);
            dot.Children.Add(number);
            dot.Children.Add(tick);
            row.Children.Add(dot);

            var text = new StackPanel { Margin = new Thickness(12, 4, 0, last ? 0 : 18) };
            var label = new TextBlock { Text = StageNames[i], FontSize = 13 };
            var note = new TextBlock { Text = StageNotes[i], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            text.Children.Add(label);
            text.Children.Add(note);
            Grid.SetColumn(text, 1);
            Grid.SetRowSpan(text, 2);
            row.Children.Add(text);

            ScaleTransform? fill = null;
            if (!last)
            {
                var track = new Border { Width = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Center };
                track.SetResourceReference(Border.BackgroundProperty, "StepTrackBrush");
                var bar = new Border { Width = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Center };
                bar.SetResourceReference(Border.BackgroundProperty, "AccentFillBrush");
                bar.RenderTransform = fill = new ScaleTransform(1, 0); // grows downwards from the dot
                Grid.SetRow(track, 1);
                Grid.SetRow(bar, 1);
                row.Children.Add(track);
                row.Children.Add(bar);
            }
            JourneyBar.Children.Add(row);
            _journey.Add(new JourneyStep(halo, circle, number, tick, label, fill));
        }
        UpdateJourney(animate: false);
    }

    /// <summary>Moves the journey indicator. Purely visual; <paramref name="warn"/> marks an ending that did not resolve the problem.</summary>
    private void Journey(Stage stage, bool warn = false)
    {
        _stage = stage;
        _endWarn = warn;
        UpdateJourney(animate: true);
    }

    private void UpdateJourney(bool animate)
    {
        JourneyRail.Visibility = _stage == Stage.None ? Visibility.Collapsed : Visibility.Visible;
        var current = _stage == Stage.Done ? StageNames.Length : (int)_stage - 1;
        for (var i = 0; i < _journey.Count; i++)
        {
            var s = _journey[i];
            var done = i < current;
            var active = i == current;
            var warnHere = _stage == Stage.Done && _endWarn && i == _journey.Count - 1;
            s.Halo.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            s.Tick.Visibility = done && !warnHere ? Visibility.Visible : Visibility.Collapsed;
            s.Number.Visibility = done && !warnHere ? Visibility.Collapsed : Visibility.Visible;
            s.Number.Text = warnHere ? "!" : (i + 1).ToString();
            s.Circle.SetResourceReference(Shape.FillProperty,
                warnHere ? "CautionBrush" : done || active ? "AccentFillBrush" : "StepPendingFillBrush");
            s.Circle.SetResourceReference(Shape.StrokeProperty,
                warnHere ? "CautionBrush" : done || active ? "AccentFillBrush" : "StepTrackBrush");
            s.Number.SetResourceReference(TextBlock.ForegroundProperty,
                warnHere ? "OnStatusBrush" : active ? "OnAccentBrush" : "TextSecondaryBrush");
            s.Label.SetResourceReference(TextBlock.ForegroundProperty, done || active ? "TextPrimaryBrush" : "TextSecondaryBrush");
            s.Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            if (s.LineFill is { } fill)
            {
                var to = i < current ? 1.0 : 0.0;
                if (animate) Motion.To(fill, ScaleTransform.ScaleYProperty, to, 320, Motion.EaseOut);
                else { fill.BeginAnimation(ScaleTransform.ScaleYProperty, null); fill.ScaleY = to; }
            }
        }
        if (_stage is not Stage.None and not Stage.Done)
            AutomationProperties.SetName(JourneyBar, $"Step {current + 1} of {StageNames.Length}: {StageNames[current]}");
        else
            AutomationProperties.SetName(JourneyBar, "All steps completed");
    }

    // ---- screens ----

    private Screen CaptureScreen() => new(HomePanel.Visibility == Visibility.Visible, _look, _shownText);

    private void PresentStep(Screen from, Look look)
    {
        var full = StepText.Text;
        var textChanged = from.Text != full;
        var busy = look is Look.Busy or Look.Scan;
        var stillBusy = !from.Home && (from.Look is Look.Busy or Look.Scan) && busy;
        var lookChanged = from.Look != look;
        _look = look;
        _shownText = full;

        // Sentence as a heading, any further sentences as supporting text.
        var (head, detail) = busy ? (full, null) : Split(full);
        StepText.Text = head;
        StepDetail.Text = detail ?? "";
        StepDetail.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        // Short supporting text sits centred under the heading; lists and long paragraphs read better left aligned.
        StepDetail.TextAlignment = detail is { } d && (d.Contains('\n') || d.Length > 220) ? TextAlignment.Left : TextAlignment.Center;
        AutomationProperties.SetName(StepText, full);
        StepButtons.Visibility = PrimaryButton.Visibility;

        HeaderTitle.Text = _topicTitle ?? "AutoFix Scout";
        HeaderTitle.Visibility = _historyOpen ? Visibility.Collapsed : Visibility.Visible; // the history card has its own heading
        NavHistory.IsEnabled = _historyOpen; // a check cannot be left half-way for the history page
        NavHistory.IsChecked = _historyOpen;
        NavHome.IsChecked = !_historyOpen;
        HighlightHelper(look is Look.NotFixed or Look.Hardware);

        if (from.Home)
        {
            HeaderHome.Visibility = Visibility.Collapsed;
            HeaderStep.Visibility = Visibility.Visible;
            Motion.FadeIn(HeaderStep);
        }

        if (stillBusy)
        {
            // "Testing the connection" repeats every few seconds: keep the scanner turning, only refresh the text.
            if (lookChanged) ShowChecks(look == Look.Scan);
            if (textChanged) Motion.Enter(StepText, distance: 6);
        }
        else
        {
            ShowPicture(look);
            ShowBanner(look);
            ShowChecks(look == Look.Scan);
            if (from.Home) Motion.Enter(StepPanel, distance: 14);
            else Motion.Enter(StepContent, distance: 8);
        }

        if (textChanged && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(StepText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>Splits off the first sentence when the text is long enough to benefit from a heading.</summary>
    private static (string Head, string? Detail) Split(string text)
    {
        var para = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (para > 0) return (text[..para].Trim(), text[(para + 2)..].Trim());
        if (text.Length < 70) return (text, null);
        for (var i = 15; i < text.Length - 2; i++)
        {
            if (text[i] is '.' or '?' && text[i + 1] == ' ' && (char.IsUpper(text[i + 2]) || text[i + 2] is '"' or '(' or (char)0x201C))
                return (text[..(i + 1)], text[(i + 2)..].Trim());
        }
        return (text, null);
    }

    private void PresentHome()
    {
        _look = Look.Step;
        _shownText = "";
        SetTopic(null);
        _stage = Stage.None;
        UpdateJourney(animate: false);
        SetSpin(false);
        ShowChecks(false);
        HighlightHelper(false);
        HeaderStep.Visibility = Visibility.Collapsed;
        HeaderHome.Visibility = Visibility.Visible;
        NavHistory.IsEnabled = true;
        NavHome.IsChecked = true;
        AnimateHome();
    }

    private void AnimateHome()
    {
        Motion.Enter(HeaderHome, distance: 8);
        if (CategoryGrid.Children.Count == 0) BuildCategories();
        for (var i = 0; i < CategoryGrid.Children.Count; i++)
            Motion.Enter((FrameworkElement)CategoryGrid.Children[i], delayMs: 40 + i / 3 * 40, distance: 12);
    }

    // ---- home categories: the part of the computer first, then the problems inside it ----

    /// <summary>Icon for the step tile when the screen has no topic (a category page).</summary>
    private int? _glyphOverride;

    /// <summary>One problem inside a category: the row shown on the category page and the check it starts.</summary>
    private sealed record Problem(int Glyph, string Title, string Caption, RoutedEventHandler Run);

    /// <summary>A home page card. The caption says what is inside, so the person can choose without opening it.</summary>
    private sealed record Category(int Glyph, string Title, string Caption, Problem[] Problems);

    private Category[] Categories() =>
    [
        new(0xE767, "Sound and microphone", "No sound, quiet calls, headphones, microphone",
        [
            new(0xE767, "Sound is not working", "Speakers, headphones, volume and sound that is quieter during calls.", OnSoundClicked),
            new(0xE720, "Microphone is not working", "Other people cannot hear you, or the microphone is very quiet.", OnMicrophoneClicked),
        ]),
        new(0xE774, "Internet and Bluetooth", "Wi-Fi, network cable, dropped connections, Bluetooth devices",
        [
            new(0xE774, "Cannot connect to the internet", "Wi-Fi or cable not connecting, connection dropping, or websites not opening.", OnNetworkClicked),
            new(0xE702, "Bluetooth is not working", "Headphones, mouse, keyboard or phone will not pair or keep disconnecting.", OnBluetoothClicked),
        ]),
        new(0xE7F4, "Display and camera", "Screen resolution, blurry or flickering picture, webcam",
        [
            new(0xE7F4, "Screen or display problem", "Blurry text, wrong resolution, flickering, or a second screen not showing.", OnDisplayClicked),
            new(0xE714, "Camera is not working", "Webcam not detected, black picture, or blocked by privacy settings.", OnCameraClicked),
        ]),
        new(0xE765, "Keyboard, mouse and touchpad", "Not responding, wrong characters, pointer problems",
        [
            new(0xE765, "Keyboard, mouse or touchpad not responding", "Keys, pointer or touchpad do nothing or behave erratically.", OnInputClicked),
            new(0xE8D2, "Keyboard types the wrong characters", "Symbols in the wrong place or a different language than expected.", OnTypingClicked),
        ]),
        new(0xE749, "Printer and scanner", "Printing, stuck print jobs, scanning",
        [
            new(0xE749, "Printer is not working", "Nothing prints, the printer shows as offline, or jobs are stuck.", OnPrinterClicked),
            new(0xE8FE, "Scanner is not working", "The scanner is not found or scanning does not start.", OnScannerClicked),
        ]),
        new(0xE83F, "Battery and performance", "Battery life, sleep, slow start-up, full disk",
        [
            new(0xE83F, "Battery, sleep and power", "Battery drains quickly, will not charge, or the computer will not sleep or wake.", OnPowerClicked),
            new(0xEDA2, "Slow or full computer", "Slow start-up, slow in use, or the disk is nearly full.", OnStorageClicked),
        ]),
        new(0xE71D, "Apps and programs", "Programs closing, installs, Microsoft Store, Office, OneDrive",
        [
            new(0xE783, "A program keeps closing", "A program crashes, freezes or will not open.", OnProgramClosingClicked),
            new(0xE896, "A program will not install", "An installer fails, is blocked or never finishes.", OnInstallClicked),
            new(0xE719, "Microsoft Store or app installs", "The Store will not open, or apps will not download or update.", OnStoreClicked),
            new(0xE8A5, "Word, Excel or Outlook", "An Office program will not start, crashes or asks to be activated.", OnOfficeClicked),
            new(0xE753, "OneDrive is not syncing", "Files are not uploading or sync is stuck.", OnOneDriveClicked),
        ]),
        new(0xE72E, "Windows and security", "Start menu, notifications, Windows Update, viruses",
        [
            new(0xE8FC, "Start menu, taskbar or search", "The Start menu will not open, the taskbar is frozen, or search finds nothing.", OnShellClicked),
            new(0xEA8F, "Notifications are missing", "Alerts from apps do not appear.", OnNotificationsClicked),
            new(0xE895, "Windows Update problems", "Updates fail, are stuck or keep being offered again.", OnUpdateClicked),
            new(0xE72E, "Security, viruses or pop-ups", "Unwanted pop-ups, security warnings, or protection that is switched off.", OnSecurityClicked),
        ]),
    ];

    private void BuildCategories()
    {
        foreach (var category in Categories())
        {
            var card = CategoryCard(category.Glyph, category.Title, category.Caption, accent: false);
            card.Click += (_, _) => OpenCategory(category);
            CategoryGrid.Children.Add(card);
        }
        // The ninth card is not a category: it is the way forward when the person cannot tell where the problem belongs.
        var checkUp = CategoryCard(0xE9D9, "Full computer check-up", "Not sure where the problem is? This examines the whole computer.", accent: true);
        checkUp.Click += OnHealthClicked;
        CategoryGrid.Children.Add(checkUp);
    }

    private Button CategoryCard(int glyph, string title, string caption, bool accent)
    {
        var icon = new TextBlock { Text = ((char)glyph).ToString(), FontSize = 17, Style = (Style)FindResource("Icon") };
        icon.SetResourceReference(TextBlock.ForegroundProperty, accent ? "OnAccentBrush" : "AccentBrush");
        var tile = new Border { Style = (Style)FindResource("IconTile"), Child = icon, HorizontalAlignment = HorizontalAlignment.Left };
        if (accent) tile.SetResourceReference(Border.BackgroundProperty, "AccentFillBrush");

        var chevron = new TextBlock
        {
            Text = ((char)0xE76C).ToString(), FontSize = 10, Style = (Style)FindResource("Icon"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0),
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var top = new Grid();
        top.Children.Add(tile);
        top.Children.Add(chevron);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });
        stack.Children.Add(new TextBlock { Text = caption, Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 2, 0, 0) });

        var card = new Button { Style = (Style)FindResource("CardButton"), Content = stack, Margin = new Thickness(4) };
        AutomationProperties.SetName(card, title);
        AutomationProperties.SetHelpText(card, caption);
        return card;
    }

    /// <summary>The category page: the problems inside it, and the full check-up for anything that is not listed.</summary>
    private async void OpenCategory(Category category)
    {
        var options = category.Problems.Select(p => new Choice(p.Glyph, p.Title, p.Caption)).ToList();
        options.Add(new Choice(0xE9D9, "Something else", "The full computer check-up examines the whole computer instead."));
        SetTopic(null);
        _topicTitle = category.Title;
        _glyphOverride = category.Glyph; // the icon tile shows the category, not the generic mark
        var pick = await ShowChoicesAsync("Please select the problem that is closest to yours.", options, "Back");
        if (pick < 0) { ShowHome(); return; }
        var args = new RoutedEventArgs();
        if (pick < category.Problems.Length) category.Problems[pick].Run(this, args);
        else OnHealthClicked(this, args);
    }

    // ---- choice screen: a sentence and a short list of checks to pick from ----

    /// <summary>One row on a choice screen: icon, title and one line on what it covers.</summary>
    internal sealed record Choice(int Glyph, string Title, string Caption);

    /// <summary>
    /// Shows a sentence with a list of options and one quiet button (<paramref name="back"/>, also Esc).
    /// Completes with the index of the selected option, or -1 for the button.
    /// </summary>
    private async Task<int> ShowChoicesAsync(string text, IReadOnlyList<Choice> options, string back)
    {
        var from = CaptureScreen();
        HomePanel.Visibility = Visibility.Collapsed;
        StepPanel.Visibility = Visibility.Visible;
        StepText.Text = text;
        PrimaryButton.Visibility = Visibility.Collapsed;
        SecondaryButton.Content = back;
        SecondaryButton.Visibility = Visibility.Visible;

        var pick = new TaskCompletionSource<int>();
        ChoicesPanel.Children.Clear();
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var row = ChoiceRow(options[i]);
            row.Click += (_, _) => pick.TrySetResult(index);
            ChoicesPanel.Children.Add(row);
        }
        ChoicesPanel.Visibility = Visibility.Visible;
        PresentStep(from, Look.Step);
        StepButtons.Visibility = Visibility.Visible; // PresentStep ties the row to the primary button
        if (ChoicesPanel.Children.Count > 0) ((Button)ChoicesPanel.Children[0]).Focus();

        _choice = new TaskCompletionSource<bool>();
        var first = await Task.WhenAny(pick.Task, _choice.Task);
        ChoicesPanel.Children.Clear();
        ChoicesPanel.Visibility = Visibility.Collapsed;
        return first == pick.Task ? pick.Task.Result : -1;
    }

    private Button ChoiceRow(Choice choice)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock { Text = ((char)choice.Glyph).ToString(), FontSize = 17, Style = (Style)FindResource("Icon") };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var tile = new Border { Style = (Style)FindResource("IconTile"), Child = glyph, VerticalAlignment = VerticalAlignment.Center };

        var title = new TextBlock { Text = choice.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var caption = new TextBlock { Text = choice.Caption, Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(caption);
        Grid.SetColumn(text, 1);

        var chevron = new TextBlock { Text = ((char)0xE76C).ToString(), FontSize = 10, Style = (Style)FindResource("Icon") };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(chevron, 2);

        grid.Children.Add(tile);
        grid.Children.Add(text);
        grid.Children.Add(chevron);

        var row = new Button
        {
            Style = (Style)FindResource("CardButton"), Content = grid, MinHeight = 0,
            Padding = new Thickness(12, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(row, choice.Title);
        AutomationProperties.SetHelpText(row, choice.Caption);
        return row;
    }

    /// <summary>Icon for a SymptomRouter area (areas are finer than topics: store, office and OneDrive share the "apps" topic).</summary>
    private static int AreaGlyph(string area) => area switch
    {
        "program" => 0xE783,
        "store" => 0xE719,
        "office" => 0xE8A5,
        "onedrive" => 0xE753,
        "scanner" => 0xE8FE,
        "install" => 0xE896,
        "notifications" => 0xEA8F,
        _ => TopicGlyph(area),
    };

    /// <summary>Segoe Fluent Icons / MDL2 Assets glyph for a topic (the icon tile on a step screen).</summary>
    private static int TopicGlyph(string? topic) => topic switch
    {
        "sound" => 0xE767,
        "camera" => 0xE714,
        "network" => 0xE774,
        "driver" => 0xE90F,
        "health" => 0xE9D9,
        "microphone" => 0xE720,
        "bluetooth" => 0xE702,
        "shell" => 0xE8FC,
        "apps" => 0xE719,
        "security" => 0xE72E,
        "power" => 0xE83F,
        "input" => 0xE765,
        "storage" => 0xEDA2,
        "update" => 0xE895,
        "printer" => 0xE749,
        "display" => 0xE7F4,
        _ => 0xE946, // Info
    };

    private void ShowPicture(Look look)
    {
        var busy = look is Look.Busy or Look.Scan;
        SetSpin(busy);
        ScanVisual.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StepArt.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (busy) return;

        var end = look is Look.Fixed or Look.AllGood or Look.NotFixed or Look.Hardware or Look.Problem or Look.Neutral;
        // Tile colour, icon colour, icon: CheckMark, Shield, Document, Component, ErrorBadge, Info, or the topic's icon.
        var (tile, brush, glyph) = look switch
        {
            Look.Fixed => ("SuccessTileBrush", "SuccessBrush", 0xE73E),
            Look.AllGood => ("InfoTileBrush", "InfoBrush", 0xEA18),
            Look.NotFixed => ("CautionTileBrush", "CautionBrush", 0xE8A5),
            Look.Hardware => ("CautionTileBrush", "CautionBrush", 0xE950),
            Look.Problem => ("ProblemTileBrush", "ProblemBrush", 0xEA39),
            Look.Neutral => ("NeutralTileBrush", "NeutralGlyphBrush", 0xE946),
            _ => ("AccentSoftBrush", "AccentBrush", _glyphOverride ?? TopicGlyph(_topic)),
        };
        StepArt.SetResourceReference(Border.BackgroundProperty, tile);
        StepArtGlyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
        StepArtGlyph.Text = ((char)glyph).ToString();

        var pop = StepArt.RenderTransform as ScaleTransform;
        if (pop is null || pop.IsFrozen) StepArt.RenderTransform = pop = new ScaleTransform();
        if (end)
        {
            Motion.FromTo(pop, ScaleTransform.ScaleXProperty, 0.7, 1, 300, Motion.Overshoot);
            Motion.FromTo(pop, ScaleTransform.ScaleYProperty, 0.7, 1, 300, Motion.Overshoot);
        }
        else
        {
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            pop.ScaleX = pop.ScaleY = 1;
        }
    }

    private void ShowBanner(Look look)
    {
        // Segoe Fluent Icons / MDL2 Assets: CheckMark, Info, Warning, Cancel.
        string Ok = Glyph(0xE73E), Info = Glyph(0xE946), Warning = Glyph(0xE7BA), Error = Glyph(0xE711);
        static string Glyph(int code) => ((char)code).ToString();
        (string Tile, string Brush, string Glyph, string Label)? banner = look switch
        {
            Look.FindingOk => ("SuccessTileBrush", "SuccessBrush", Ok, "Check complete: no fault was found"),
            Look.FindingInfo => ("InfoTileBrush", "InfoBrush", Info, "Check complete: some information is missing"),
            Look.FindingWarning => ("CautionTileBrush", "CautionBrush", Warning, "Check complete: a setting needs attention"),
            Look.FindingError => ("ProblemTileBrush", "ProblemBrush", Error, "Check complete: a problem was found"),
            Look.Fixed => ("SuccessTileBrush", "SuccessBrush", Ok, "Completed successfully"),
            Look.AllGood => ("InfoTileBrush", "InfoBrush", Ok, "No fault found"),
            Look.NotFixed => ("CautionTileBrush", "CautionBrush", Warning, "Not resolved"),
            Look.Hardware => ("CautionTileBrush", "CautionBrush", Warning, "Possible hardware fault"),
            Look.Problem => ("ProblemTileBrush", "ProblemBrush", Error, "Not completed"),
            Look.Neutral => ("NeutralTileBrush", "NeutralGlyphBrush", Info, "Stopped at your request"),
            _ => null,
        };
        if (banner is not { } b) { StatusBanner.Visibility = Visibility.Collapsed; return; }
        StatusBanner.Visibility = Visibility.Visible;
        StatusBanner.SetResourceReference(Border.BackgroundProperty, b.Tile);
        StatusBanner.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        StatusGlyph.SetResourceReference(TextBlock.ForegroundProperty, b.Brush);
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, b.Brush == "NeutralGlyphBrush" ? "TextPrimaryBrush" : b.Brush);
        StatusGlyph.Text = b.Glyph;
        StatusLabel.Text = b.Label;
        AutomationProperties.SetName(StatusBanner, b.Label);
    }

    // ---- checks list on the busy screen of a check ----

    /// <summary>
    /// What the check looks at, per topic. The list advances on a timer (the probe reports everything at once), so it
    /// only ever says "in progress" / "looked at", never a result; the last item stays in progress until the probe returns.
    /// </summary>
    private static string[] ChecksFor(string? topic) => topic switch
    {
        "sound" => ["Windows Audio services", "Sound devices and drivers", "Speakers and headphones", "Volume and mute"],
        "camera" => ["Camera devices and drivers", "Camera privacy settings", "Apps using the camera"],
        "network" => ["Network adapters and Airplane mode", "Wi-Fi or cable connection", "Connection to your router",
                      "Website address lookup (DNS)", "Internet access"],
        _ => [],
    };

    private void ShowChecks(bool on)
    {
        _checkTimer.Stop();
        foreach (var row in _checks) Motion.Spin(row.SpinRotate, false);
        _checks.Clear();
        ChecksList.Children.Clear();
        var labels = on ? ChecksFor(_topic) : [];
        ChecksPanel.Visibility = labels.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (labels.Length == 0) return;

        foreach (var text in labels)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var ring = new Ellipse { Width = 20, Height = 20, StrokeThickness = 2 };
            ring.SetResourceReference(Shape.StrokeProperty, "RingTrackBrush");

            var spinner = new Grid { Width = 20, Height = 20 };
            var track = new Ellipse { StrokeThickness = 2.5 };
            track.SetResourceReference(Shape.StrokeProperty, "RingTrackBrush");
            var rotate = new RotateTransform(0, 10, 10);
            var arc = new Path
            {
                Data = Geometry.Parse("M 10,1.25 A 8.75,8.75 0 0 1 18.75,10"), StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = rotate,
            };
            arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            spinner.Children.Add(track);
            spinner.Children.Add(arc);

            var done = new Grid { Width = 20, Height = 20 };
            var fill = new Ellipse();
            fill.SetResourceReference(Shape.FillProperty, "AccentFillBrush");
            var tick = new Path
            {
                Data = Geometry.Parse("M 5.8,10.2 L 8.6,13 L 14.2,7.4"), StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            tick.SetResourceReference(Shape.StrokeProperty, "OnAccentBrush");
            done.Children.Add(fill);
            done.Children.Add(tick);

            var indicator = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            indicator.Children.Add(ring);
            indicator.Children.Add(spinner);
            indicator.Children.Add(done);
            var label = new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(indicator);
            row.Children.Add(label);
            ChecksList.Children.Add(row);
            _checks.Add(new CheckRow(ring, spinner, rotate, done, label));
        }
        _checkIndex = 0;
        UpdateChecks();
        _checkTimer.Start();
    }

    private void AdvanceChecks()
    {
        if (_checkIndex >= _checks.Count - 1) { _checkTimer.Stop(); return; } // the last one stays "in progress"
        _checkIndex++;
        UpdateChecks();
        if (_checkIndex < _checks.Count) Motion.FromTo(_checks[_checkIndex - 1].Done, OpacityProperty, 0.2, 1, 200);
    }

    private void UpdateChecks()
    {
        for (var i = 0; i < _checks.Count; i++)
        {
            var c = _checks[i];
            var done = i < _checkIndex;
            var active = i == _checkIndex;
            c.Ring.Visibility = !done && !active ? Visibility.Visible : Visibility.Collapsed;
            c.Spinner.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            c.Done.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            c.Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            c.Label.SetResourceReference(TextBlock.ForegroundProperty, active || done ? "TextPrimaryBrush" : "TextSecondaryBrush");
            c.Label.Opacity = !done && !active ? 0.8 : 1;
            Motion.Spin(c.SpinRotate, active, periodMs: 900, linear: true);
            AutomationProperties.SetName(c.Label, $"{c.Label.Text}: {(done ? "checked" : active ? "in progress" : "waiting")}");
        }
    }

    private void SetSpin(bool on)
    {
        if (on == _spinning) return;
        _spinning = on;
        Motion.Spin(ScanRotate, on, periodMs: 1400, linear: true);
        Motion.Pulse(Pulse1, Pulse1Scale, 0, on);
        Motion.Pulse(Pulse2, Pulse2Scale, 1200, on);
    }

    /// <summary>On "not fixed" and "probably hardware" endings, point at the report button.</summary>
    private void HighlightHelper(bool on)
    {
        if (on)
        {
            HelperButton.SetResourceReference(BorderBrushProperty, "AccentBrush");
            HelperButton.SetResourceReference(ForegroundProperty, "AccentBrush");
            HelperButton.BorderThickness = new Thickness(1);
        }
        else
        {
            HelperButton.ClearValue(BorderBrushProperty);
            HelperButton.ClearValue(ForegroundProperty);
            HelperButton.ClearValue(BorderThicknessProperty);
        }
    }
}
