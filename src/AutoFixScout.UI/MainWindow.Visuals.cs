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
/// Presentation only: theme hookup, header, journey indicator, the picture and banner around each sentence, the
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
        // Ambient header movement: very slow, and none at all when Windows animation effects are off.
        Motion.Drift(Glow1Shift, 70, 26, 13);
        Motion.Drift(Glow2Shift, -80, 34, 17);
        Motion.Drift(Glow3Shift, 60, -24, 21);
        Motion.Spin(HeroRingsRotate, true, periodMs: 90_000, linear: true);
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
        _topicTitle = topic switch
        {
            "sound" => SoundTitle.Text,
            "camera" => CameraTitle.Text,
            "network" => NetworkTitle.Text,
            "driver" => "Unfinished driver change",
            "health" => "Computer check-up",
            "microphone" => "Microphone",
            "bluetooth" => "Bluetooth",
            "storage" => "Slow or full computer",
            "update" => "Windows Update",
            "printer" => "Printer",
            "display" => "Screen and display",
            _ => null,
        };
    }

    // ---- journey: Check -> Diagnosis -> Fix -> Confirm ----

    private void BuildJourney()
    {
        for (var i = 0; i < StageNames.Length; i++)
        {
            JourneyBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (i < StageNames.Length - 1) JourneyBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 28 });
        }
        JourneyBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        JourneyBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < StageNames.Length; i++)
        {
            var dot = new Grid { Width = 42, Height = 42, HorizontalAlignment = HorizontalAlignment.Center };
            var halo = new Ellipse { StrokeThickness = 2, Opacity = 0.45 };
            halo.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            var circle = new Ellipse { StrokeThickness = 1.5, Margin = new Thickness(4) };
            var number = new TextBlock
            {
                Text = (i + 1).ToString(), FontSize = 15, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var tick = new Path
            {
                Data = Geometry.Parse("M 14.5,21.5 L 19,26 L 27.5,17"), StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            tick.SetResourceReference(Shape.StrokeProperty, "OnAccentBrush");
            dot.Children.Add(halo);
            dot.Children.Add(circle);
            dot.Children.Add(number);
            dot.Children.Add(tick);
            Grid.SetColumn(dot, i * 2);
            JourneyBar.Children.Add(dot);

            var label = new TextBlock { Text = StageNames[i], FontSize = 14, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(label, i * 2);
            Grid.SetRow(label, 1);
            JourneyBar.Children.Add(label);

            ScaleTransform? fill = null;
            if (i < StageNames.Length - 1)
            {
                var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                track.SetResourceReference(Border.BackgroundProperty, "StepTrackBrush");
                var bar = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                bar.RenderTransform = fill = new ScaleTransform(0, 1);
                Grid.SetColumn(track, i * 2 + 1);
                Grid.SetColumn(bar, i * 2 + 1);
                JourneyBar.Children.Add(track);
                JourneyBar.Children.Add(bar);
            }
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
        JourneyBar.Visibility = _stage == Stage.None ? Visibility.Collapsed : Visibility.Visible;
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
                warnHere ? "CautionBrush" : done || active ? "AccentBrush" : "StepPendingFillBrush");
            s.Circle.SetResourceReference(Shape.StrokeProperty,
                warnHere ? "CautionBrush" : done || active ? "AccentBrush" : "StepTrackBrush");
            s.Number.SetResourceReference(TextBlock.ForegroundProperty,
                warnHere ? "OnStatusBrush" : active ? "OnAccentBrush" : "HeroSubtextBrush");
            s.Label.SetResourceReference(TextBlock.ForegroundProperty, done || active ? "HeroTextBrush" : "HeroSubtextBrush");
            s.Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            if (s.LineFill is { } fill)
            {
                var to = i < current ? 1.0 : 0.0;
                if (animate) Motion.To(fill, ScaleTransform.ScaleXProperty, to, 320, Motion.EaseOut);
                else { fill.BeginAnimation(ScaleTransform.ScaleXProperty, null); fill.ScaleX = to; }
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
        AutomationProperties.SetName(StepText, full);
        StepButtons.Visibility = PrimaryButton.Visibility;

        HeroTitle.Text = _topicTitle ?? "AutoFix Scout";
        var overline = busy || _stage is Stage.None or Stage.Done ? null : StageNames[(int)_stage - 1];
        StepOverline.Text = overline is null ? "" : $"Step {(int)_stage} of {StageNames.Length}: {overline}";
        StepOverline.Visibility = overline is null ? Visibility.Collapsed : Visibility.Visible;
        HighlightHelper(look is Look.NotFixed or Look.Hardware);

        if (from.Home)
        {
            HeroHome.Visibility = Visibility.Collapsed;
            HeroStep.Visibility = Visibility.Visible;
            Motion.FadeIn(HeroStep);
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
            if (look == Look.Fixed) Motion.Confetti(ConfettiLayer);
            if (from.Home) Motion.Enter(StepPanel, distance: 22);
            else Motion.Enter(StepContent, distance: 12);
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
        HeroStep.Visibility = Visibility.Collapsed;
        HeroHome.Visibility = Visibility.Visible;
        AnimateHome();
    }

    private void AnimateHome()
    {
        Motion.Enter(HeroHome, distance: 10);
        FrameworkElement[] parts = [SoundCardHost, CameraCardHost, NetworkCardHost, OtherHost];
        for (var i = 0; i < parts.Length; i++) Motion.Enter(parts[i], delayMs: 60 + i * 55, distance: 20);
    }

    private void ShowPicture(Look look)
    {
        var busy = look is Look.Busy or Look.Scan;
        SetSpin(busy);
        ScanVisual.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StepArt.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (busy) return;

        var end = look is Look.Fixed or Look.AllGood or Look.NotFixed or Look.Hardware or Look.Problem or Look.Neutral;
        var art = look switch
        {
            Look.Fixed => "ArtFixed",
            Look.AllGood => "ArtAllGood",
            Look.NotFixed => "ArtNotFixed",
            Look.Hardware => "ArtHardware",
            Look.Problem => "ArtProblem",
            Look.Neutral => "ArtNeutral",
            _ => _topic switch
            {
                "sound" => "ArtSound",
                "camera" => "ArtCamera",
                "network" => "ArtNetwork",
                _ => "ArtNeutral",
            },
        };
        StepArt.Template = (ControlTemplate)FindResource(art);
        StepArt.Width = StepArt.Height = end ? 96 : 72;
        StepArt.ApplyTemplate();

        var pop = StepArt.RenderTransform as ScaleTransform;
        if (pop is null || pop.IsFrozen) StepArt.RenderTransform = pop = new ScaleTransform();
        if (end)
        {
            Motion.FromTo(pop, ScaleTransform.ScaleXProperty, 0.6, 1, 340, Motion.Overshoot);
            Motion.FromTo(pop, ScaleTransform.ScaleYProperty, 0.6, 1, 340, Motion.Overshoot);
            if (StepArt.Template.FindName("Check", StepArt) is Path check)
                Motion.FromTo(check, Shape.StrokeDashOffsetProperty, 7, 0, 320, Motion.EaseOut, delayMs: 160);
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
        StatusBanner.SetResourceReference(Border.BorderBrushProperty, b.Brush);
        StatusDot.SetResourceReference(Border.BackgroundProperty, b.Brush);
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
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
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
            fill.SetResourceReference(Shape.FillProperty, "AccentBrush");
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
            var label = new TextBlock { Text = text, FontSize = 17, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
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
            HelperButton.BorderThickness = new Thickness(2);
        }
        else
        {
            HelperButton.ClearValue(BorderBrushProperty);
            HelperButton.ClearValue(ForegroundProperty);
            HelperButton.ClearValue(BorderThicknessProperty);
        }
    }
}
