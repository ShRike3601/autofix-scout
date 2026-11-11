using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AutoFixScout.Triage;

namespace AutoFixScout.UI;

/// <summary>
/// Presentation only: theme hookup, the picture above each sentence, page transitions. Nothing in here decides
/// what the flow does; MainWindow.xaml.cs calls PresentStep / PresentHome after it has set the screen's content.
/// </summary>
public partial class MainWindow
{
    /// <summary>The picture shown above a step sentence.</summary>
    internal enum Look { Step, Busy, Fixed, AllGood, NotFixed, Hardware, Problem, Neutral }

    private readonly record struct Screen(bool Home, Look Look, string Text);

    private Look _look = Look.Step;
    private string? _topicTitle;
    private (string Tile, string Glyph, string Icon)? _topicArt;
    private bool _spinning;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        // Fit small or highly scaled screens (e.g. 1366x768 at 125%).
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 32);
        Width = Math.Min(Width, area.Width - 32);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.Attach(this);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
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

    private void SetTopic(string topic)
    {
        (_topicTitle, _topicArt) = topic switch
        {
            "sound" => (SoundTitle.Text, ("SoundTileBrush", "SoundGlyphBrush", "")),
            "camera" => (CameraTitle.Text, ("CameraTileBrush", "CameraGlyphBrush", "")),
            "network" => (NetworkTitle.Text, ("NetworkTileBrush", "NetworkGlyphBrush", "")),
            _ => ((string?)null, ((string, string, string)?)null),
        };
    }

    private Screen CaptureScreen() => new(HomePanel.Visibility == Visibility.Visible, _look, StepText.Text);

    private void PresentStep(Screen from, Look look)
    {
        var textChanged = from.Text != StepText.Text;
        var stillBusy = !from.Home && from.Look == Look.Busy && look == Look.Busy;
        _look = look;

        StepTopic.Text = _topicTitle ?? "";
        StepTopic.Visibility = _topicTitle is null ? Visibility.Collapsed : Visibility.Visible;
        StepText.Margin = new Thickness(0, 0, 0, look == Look.Busy ? 4 : 28);
        HighlightHelper(look is Look.NotFixed or Look.Hardware);

        if (stillBusy)
        {
            // "Checking the connection..." repeats every few seconds: keep the ring turning, only refresh the text.
            if (textChanged) Motion.Enter(StepText, distance: 6);
        }
        else
        {
            ShowPicture(look);
            if (from.Home) Motion.Enter(StepPanel, distance: 18);
            else Motion.Enter(StepContent, distance: 10);
        }

        if (textChanged && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(StepText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void PresentHome()
    {
        _look = Look.Step;
        _topicTitle = null;
        _topicArt = null;
        SetSpin(false);
        HighlightHelper(false);
        AnimateHome();
    }

    private void AnimateHome()
    {
        FrameworkElement[] parts = [HomeHeader, SoundCardHost, CameraCardHost, NetworkCardHost, OtherHost];
        for (var i = 0; i < parts.Length; i++) Motion.Enter(parts[i], delayMs: i * 25, distance: 16);
    }

    private void ShowPicture(Look look)
    {
        SetSpin(look == Look.Busy);
        BusyRing.Visibility = look == Look.Busy ? Visibility.Visible : Visibility.Collapsed;
        var check = look is Look.Fixed or Look.AllGood;
        CheckMark.Visibility = check ? Visibility.Visible : Visibility.Collapsed;
        StepTile.Visibility = look == Look.Busy || check ? Visibility.Collapsed : Visibility.Visible;

        if (check)
        {
            var (tile, stroke) = look == Look.Fixed ? ("SuccessTileBrush", "SuccessBrush") : ("InfoTileBrush", "InfoBrush");
            CheckCircle.SetResourceReference(Shape.FillProperty, tile);
            CheckPath.SetResourceReference(Shape.StrokeProperty, stroke);
            Motion.FromTo(CheckScale, ScaleTransform.ScaleXProperty, 0.55, 1, 380, Motion.Overshoot);
            Motion.FromTo(CheckScale, ScaleTransform.ScaleYProperty, 0.55, 1, 380, Motion.Overshoot);
            Motion.FromTo(CheckPath, Shape.StrokeDashOffsetProperty, 9, 0, 300, Motion.EaseOut, delayMs: 150);
            return;
        }
        if (look == Look.Busy) return;

        var (tileKey, glyphKey, icon) = look switch
        {
            Look.NotFixed => ("CautionTileBrush", "CautionBrush", ""),
            Look.Hardware => ("CautionTileBrush", "CautionBrush", ""),
            Look.Problem => ("ProblemTileBrush", "ProblemBrush", ""),
            Look.Neutral => ("NeutralTileBrush", "NeutralGlyphBrush", ""),
            _ => _topicArt ?? ("NeutralTileBrush", "NeutralGlyphBrush", ""),
        };
        StepTile.SetResourceReference(Border.BackgroundProperty, tileKey);
        StepGlyph.SetResourceReference(TextBlock.ForegroundProperty, glyphKey);
        StepGlyph.Text = icon;
        var pop = StepTile.RenderTransform as ScaleTransform;
        if (pop is null || pop.IsFrozen) StepTile.RenderTransform = pop = new ScaleTransform();
        if (look != Look.Step)
        {
            Motion.FromTo(pop, ScaleTransform.ScaleXProperty, 0.7, 1, 320, Motion.Overshoot);
            Motion.FromTo(pop, ScaleTransform.ScaleYProperty, 0.7, 1, 320, Motion.Overshoot);
        }
    }

    private void SetSpin(bool on)
    {
        if (on == _spinning) return;
        _spinning = on;
        Motion.Spin(BusyRotate, on);
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
