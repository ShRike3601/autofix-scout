using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AutoFixScout;

namespace AutoFixScout.UI;

/// <summary>
/// "What AutoFix Scout is doing": one quiet line under the step screen with the current step (from Activity.Reported),
/// which unfolds into the full list for anyone who knows IT. Folded by default so it never crowds the screen.
/// While a check runs the line shows the live step; afterwards it reads "What AutoFix Scout checked · N steps".
/// </summary>
public partial class MainWindow
{
    private const int ActivityKeep = 200;
    private StackPanel? _activityPanel, _activityLog;
    private TextBlock? _activityLine, _activityChevron;
    private ScaleTransform? _activityScale;
    private Border? _activityHolder;
    private ScrollViewer? _activityScroll;
    private bool _activityOpen, _activityBusy;
    private int _activitySteps;

    private void EnsureActivityPanel()
    {
        if (_activityPanel is not null) return;
        _activityLine = new TextBlock
        {
            Style = (Style)FindResource("CaptionText"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetLiveSetting(_activityLine, AutomationLiveSetting.Off); // the steps would flood a screen reader
        _activityChevron = new TextBlock
        {
            Text = ((char)0xE70D).ToString(), FontSize = 9, Style = (Style)FindResource("Icon"), Margin = new Thickness(10, 0, 0, 0),
            Foreground = (Brush)FindResource("TextSecondaryBrush"), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90),
        };
        var icon = new TextBlock
        {
            Text = ((char)0xE9D9).ToString(), FontSize = 12, Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 10, 0),
            Foreground = (Brush)FindResource("AccentBrush"),
        };
        var head = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(_activityChevron, Dock.Right);
        head.Children.Add(icon);
        head.Children.Add(_activityChevron);
        head.Children.Add(_activityLine);
        var toggle = new Button { Style = (Style)FindResource("SectionHeader"), Content = head, MinHeight = 32 };
        AutomationProperties.SetName(toggle, "Show what AutoFix Scout is doing");
        toggle.Click += (_, _) => SetActivityOpen(!_activityOpen);

        _activityLog = new StackPanel { Margin = new Thickness(12, 6, 8, 4) };
        _activityScroll = new ScrollViewer { Content = _activityLog, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        _activityScale = new ScaleTransform(1, 0);
        _activityHolder = new Border { Child = _activityScroll, LayoutTransform = _activityScale, Opacity = 0, ClipToBounds = true };

        _activityPanel = new StackPanel { Margin = new Thickness(0, 20, 0, 0), Visibility = Visibility.Collapsed };
        var frame = new Border { CornerRadius = (CornerRadius)FindResource("CardRadius"), BorderThickness = new Thickness(1), Padding = new Thickness(2) };
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        var inner = new StackPanel();
        inner.Children.Add(toggle);
        inner.Children.Add(_activityHolder);
        frame.Child = inner;
        _activityPanel.Children.Add(frame);
        StepContent.Children.Insert(StepContent.Children.IndexOf(ChecksPanel) + 1, _activityPanel);

        foreach (var item in Activity.Recent().TakeLast(30)) AddActivity(item, live: false);
        Activity.Reported += item => Dispatcher.BeginInvoke(() => AddActivity(item, live: true));
    }

    /// <summary>A new check starts: the list begins empty and folded.</summary>
    private void ResetActivity()
    {
        _activitySteps = 0;
        _activityLog?.Children.Clear();
        EnsureActivityPanel();
        SetActivityOpen(ThemeManager.Settings.ActivityOpen);
    }

    /// <summary>Called by PresentStep: shown on busy and result screens of a check, hidden elsewhere.</summary>
    private void UpdateActivityPanel(bool busy)
    {
        EnsureActivityPanel();
        _activityBusy = busy;
        _activityPanel!.Visibility = _topic is null || (_activitySteps == 0 && !busy) ? Visibility.Collapsed : Visibility.Visible;
        if (!busy) _activityLine!.Text = $"What AutoFix Scout checked  ·  {_activitySteps} steps";
    }

    private void AddActivity(ActivityItem item, bool live)
    {
        if (_activityLog is null) return;
        _activitySteps++;
        // Time in its own column so wrapped steps stay aligned.
        var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var secondary = (Brush)FindResource("TextSecondaryBrush");
        row.Children.Add(new TextBlock { Text = item.Time.ToLocalTime().ToString("HH:mm:ss"), FontSize = 11, FontFamily = mono, Foreground = secondary, Opacity = 0.7 });
        var step = new TextBlock { Text = item.Text, FontSize = 11, FontFamily = mono, Foreground = secondary, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(step, 1);
        row.Children.Add(step);
        _activityLog.Children.Add(row);
        while (_activityLog.Children.Count > ActivityKeep) _activityLog.Children.RemoveAt(0);
        if (live && _activityOpen) Motion.FromTo(row, OpacityProperty, 0, 1, 220);
        _activityScroll?.ScrollToEnd();
        if (_activityBusy && _activityLine is not null)
        {
            _activityLine.Text = item.Text;
            if (live) Motion.FromTo(_activityLine, OpacityProperty, 0.35, 1, 260);
        }
        if (live && _activityPanel?.Visibility == Visibility.Collapsed && _topic is not null && _activityBusy) _activityPanel.Visibility = Visibility.Visible;
    }

    private void SetActivityOpen(bool open)
    {
        if (_activityScale is null || _activityHolder is null || _activityChevron is null) return;
        _activityOpen = open;
        Motion.To(_activityScale, ScaleTransform.ScaleYProperty, open ? 1 : 0, open ? 380 : 240, Motion.EaseOutStrong);
        Motion.To(_activityHolder, OpacityProperty, open ? 1 : 0, open ? 300 : 160);
        Motion.To((RotateTransform)_activityChevron.RenderTransform, RotateTransform.AngleProperty, open ? 0 : -90, 260, Motion.EaseOutStrong);
        if (open) _activityScroll?.ScrollToEnd();
    }
}
