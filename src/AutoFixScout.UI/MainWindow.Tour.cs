using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoFixScout.Updates;

namespace AutoFixScout.UI;

/// <summary>
/// First-run tour: three short cards over the home page, shown once to new users (not to people updating, who already
/// have a session log). Next / Back / Skip; Esc skips. Each card slides in; the dots show where you are.
/// </summary>
public partial class MainWindow
{
    private static readonly (int Glyph, string Title, string Text)[] TourCards =
    {
        (0xE721, "Describe the problem, or choose where it is",
            "Type what is wrong in your own words, or select the part of the computer it concerns. AutoFix Scout picks the right check."),
        (0xEA18, "Nothing changes without your permission",
            "AutoFix Scout checks first and explains what it found. A fix is only applied when you select it, and Windows asks for permission before larger changes."),
        (0xE713, "Help is always close",
            "On the left: what this computer is, your recent checks and the Undo for the last change. Settings and the report for the person helping you are at the bottom."),
    };

    /// <summary>Decided in the constructor, before the session log is first created.</summary>
    private bool _showTour;

    private void DecideTour()
    {
        var s = AppSettings.Load();
        var open = Environment.GetEnvironmentVariable("AUTOFIXSCOUT_OPEN");
        _showTour = open == "tour" || (open is null && !s.TourDone && !System.IO.File.Exists(SessionLog.DefaultPath));
    }

    private void ShowTourIfNeeded()
    {
        if (!_showTour) return;
        var index = 0;
        var scrim = new Border { Background = (Brush)FindResource("ScrimBrush") };
        Grid.SetColumnSpan(scrim, 2);
        var body = new StackPanel();
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var back = new Button { Content = "Back", Style = (Style)FindResource("StandardButton"), MinWidth = 0, Padding = new Thickness(16, 4, 16, 4) };
        var next = new Button { Content = "Next", Style = (Style)FindResource("AccentButton"), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        var skip = new Button { Content = "Skip", Style = (Style)FindResource("SubtleButton"), Tag = "", MinHeight = 30 };
        var buttons = new DockPanel { Margin = new Thickness(0, 26, 0, 0), LastChildFill = false };
        DockPanel.SetDock(dots, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(back, Dock.Right);
        DockPanel.SetDock(skip, Dock.Right);
        buttons.Children.Add(dots);
        buttons.Children.Add(next);
        buttons.Children.Add(back);
        buttons.Children.Add(skip);
        var stack = new StackPanel();
        stack.Children.Add(body);
        stack.Children.Add(buttons);
        var card = new Border
        {
            Width = 480, Padding = new Thickness(32, 30, 28, 24), CornerRadius = (CornerRadius)FindResource("CardRadius"),
            BorderThickness = new Thickness(1), Child = stack, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Direction = 270, Opacity = 0.35 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "FlyoutBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        AutomationProperties.SetName(card, "Welcome to AutoFix Scout");
        var layer = new Grid();
        Grid.SetColumnSpan(layer, 2);
        layer.Children.Add(scrim);
        layer.Children.Add(card);
        Root.Children.Add(layer);

        void Close()
        {
            var s = AppSettings.Load();
            s.TourDone = true;
            s.Save();
            Motion.To(layer, OpacityProperty, 0, 220);
            _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(Motion.Enabled ? 230 : 0); Root.Children.Remove(layer); SymptomBox.Focus(); });
        }

        void Show(int i, int direction)
        {
            index = i;
            var (glyph, title, text) = TourCards[i];
            body.Children.Clear();
            var tile = new Border { Style = (Style)FindResource("IconTile"), Width = 48, Height = 48, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 18) };
            tile.Child = new TextBlock { Text = ((char)glyph).ToString(), FontSize = 22, Style = (Style)FindResource("Icon"), Foreground = (Brush)FindResource("AccentBrush") };
            body.Children.Add(tile);
            body.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("TitleText") });
            body.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("BodyText"), Margin = new Thickness(0, 8, 0, 0) });
            var shift = new TranslateTransform();
            body.RenderTransform = shift;
            Motion.FromTo(shift, TranslateTransform.XProperty, 28 * direction, 0, 420, Motion.EaseOutStrong);
            Motion.FromTo(body, OpacityProperty, 0, 1, 260);
            dots.Children.Clear();
            for (var d = 0; d < TourCards.Length; d++)
            {
                var dot = new Border { Height = 6, Width = d == i ? 18 : 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) };
                dot.SetResourceReference(Border.BackgroundProperty, d == i ? "AccentBrush" : "StepTrackBrush");
                dots.Children.Add(dot);
            }
            back.Visibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;
            next.Content = i == TourCards.Length - 1 ? "Start" : "Next";
            AutomationProperties.SetName(card, $"Welcome to AutoFix Scout, step {i + 1} of {TourCards.Length}: {title}");
        }

        next.Click += (_, _) => { if (index == TourCards.Length - 1) Close(); else Show(index + 1, 1); };
        back.Click += (_, _) => { if (index > 0) Show(index - 1, -1); };
        skip.Click += (_, _) => Close();
        layer.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Show(0, 1);
        Motion.FromTo(layer, OpacityProperty, 0, 1, 300);
        Motion.Enter(card, delayMs: 60, distance: 18);
        next.Focus();
    }
}
