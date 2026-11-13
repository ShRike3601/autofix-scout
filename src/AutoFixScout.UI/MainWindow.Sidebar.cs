using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AutoFixScout;

namespace AutoFixScout.UI;

/// <summary>
/// The sidebar below the navigation: "This computer" (facts an IT person checks first, copyable for a helper) and
/// "Recent activity" (last checks, and Undo for the latest change that can be undone). Both fold open and closed.
/// </summary>
public partial class MainWindow
{
    private StackPanel? _recentBody;
    private bool _sidebarBuilt;

    private async Task BuildSidebarAsync()
    {
        if (_sidebarBuilt) return;
        _sidebarBuilt = true;
        IReadOnlyList<SummaryLine> facts;
        try { facts = await Task.Run(SystemSummary.Read); } catch (Exception) { facts = Array.Empty<SummaryLine>(); }

        var computer = new StackPanel();
        foreach (var f in facts)
        {
            var line = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
            line.Children.Add(new TextBlock { Text = f.Label, Style = (Style)FindResource("CaptionText"), FontSize = 11 });
            line.Children.Add(new TextBlock { Text = f.Value, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextPrimaryBrush") });
            computer.Children.Add(line);
        }
        if (facts.Count > 0)
        {
            var copy = new Button { Style = (Style)FindResource("SubtleButton"), Content = "Copy details", Tag = ((char)0xE8C8).ToString(), MinHeight = 30, Margin = new Thickness(-11, 0, 0, 0) };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(SystemSummary.AsText(facts)); copy.Content = "Copied"; }
                catch (Exception) { copy.Content = "Could not copy"; }
            };
            computer.Children.Add(copy);
        }
        SidebarSections.Children.Add(Foldable("This computer", computer, open: true));

        _recentBody = new StackPanel();
        SidebarSections.Children.Add(Foldable("Recent activity", _recentBody, open: true));
        RefreshRecentActivity();
        for (var i = 0; i < SidebarSections.Children.Count; i++) Motion.Enter((FrameworkElement)SidebarSections.Children[i], delayMs: 80 + i * 70, distance: 8);
    }

    /// <summary>Last three checks and the latest change that can still be undone. Called again when the home page shows.</summary>
    private void RefreshRecentActivity()
    {
        if (_recentBody is null) return;
        _recentBody.Children.Clear();
        IReadOnlyList<SessionLog.SessionSummary> recent;
        try { using var db = new SessionLog(SessionLog.DefaultPath); recent = db.Recent(3); }
        catch (Exception) { recent = Array.Empty<SessionLog.SessionSummary>(); }
        if (recent.Count == 0)
            _recentBody.Children.Add(new TextBlock { Text = "No checks yet.", Style = (Style)FindResource("CaptionText") });
        foreach (var r in recent)
        {
            var line = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
            line.Children.Add(new TextBlock { Text = HistoryText.Symptom(r.Symptom), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextPrimaryBrush") });
            line.Children.Add(new TextBlock { Text = $"{HistoryText.Outcome(r.Outcome)}  ·  {r.StartedUtc.ToLocalTime():d MMM}", Style = (Style)FindResource("CaptionText"), FontSize = 11 });
            _recentBody.Children.Add(line);
        }

        ChangeEntry? last = null;
        try { last = new ChangeHistory().List(20).FirstOrDefault(e => e.CanUndo && !e.Undone); } catch (Exception) { }
        if (last is not null)
        {
            var undo = new Button { Style = (Style)FindResource("SubtleButton"), Content = "Undo the last change", Tag = ((char)0xE7A7).ToString(), MinHeight = 30, Margin = new Thickness(-11, 2, 0, 0) };
            undo.ToolTip = last.Description;
            AutomationProperties.SetName(undo, $"Undo: {last.Description}");
            undo.Click += async (_, _) =>
            {
                undo.IsEnabled = false;
                var result = await new ChangeHistory().UndoAsync(last.StepId);
                undo.Content = result.Ok ? "Undone" : "Could not undo";
                undo.ToolTip = result.Message;
            };
            _recentBody.Children.Add(undo);
        }
        var all = new Button { Style = (Style)FindResource("SubtleButton"), Content = "Show all", Tag = ((char)0xE81C).ToString(), MinHeight = 30, Margin = new Thickness(-11, 0, 0, 0) };
        all.Click += (_, _) => NavHistory.IsChecked = true;
        _recentBody.Children.Add(all);
    }

    /// <summary>A section whose body folds open and closed smoothly (height and opacity), with a turning chevron.</summary>
    private FrameworkElement Foldable(string title, UIElement body, bool open)
    {
        var chevron = new TextBlock
        {
            Text = ((char)0xE70D).ToString(), FontSize = 9, Style = (Style)FindResource("Icon"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"), RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(open ? 0 : -90),
        };
        var label = new TextBlock { Text = title, Style = (Style)FindResource("OverlineText"), VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        head.Children.Add(label);
        var header = new Button
        {
            Style = (Style)FindResource("SectionHeader"), Content = head,
        };
        AutomationProperties.SetName(header, title);
        var scale = new ScaleTransform(1, open ? 1 : 0);
        var holder = new Border { Child = body, Padding = new Thickness(12, 4, 8, 8), LayoutTransform = scale, Opacity = open ? 1 : 0, ClipToBounds = true };
        var isOpen = open;
        header.Click += (_, _) =>
        {
            isOpen = !isOpen;
            Motion.To(scale, ScaleTransform.ScaleYProperty, isOpen ? 1 : 0, isOpen ? 380 : 260, Motion.EaseOutStrong);
            Motion.To(holder, OpacityProperty, isOpen ? 1 : 0, isOpen ? 320 : 160);
            Motion.To((RotateTransform)chevron.RenderTransform, RotateTransform.AngleProperty, isOpen ? 0 : -90, 260, Motion.EaseOutStrong);
        };
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        section.Children.Add(header);
        section.Children.Add(holder);
        return section;
    }
}
