using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AutoFixScout.Updates;

namespace AutoFixScout.UI;

/// <summary>
/// Settings panel sections: Appearance (theme, accent, text size, glass, motion, step list) and Data (data folder,
/// clear history). Every change is saved at once and applied without a restart.
/// </summary>
public partial class MainWindow
{
    private bool _confirmClear;

    private void AddAppearanceSettings()
    {
        var s = AppSettings.Load();
        SettingsBody.Children.Add(Section("Appearance", first: true));
        var card = new StackPanel();

        card.Children.Add(Label("Theme"));
        card.Children.Add(Segmented(new[] { ("dark", "Dark"), ("light", "Light"), ("system", "Match Windows") }, s.Theme,
            v => Change(x => x.Theme = v)));

        card.Children.Add(Label("Accent colour", top: 14));
        var swatches = new WrapPanel();
        foreach (var (key, hex, name) in new[] { ("blue", "#2670E8", "Blue"), ("teal", "#0E8F7E", "Teal"), ("violet", "#7148E0", "Violet"), ("orange", "#C4520C", "Orange") })
            swatches.Children.Add(Swatch(key, hex, name, s.Accent == key));
        card.Children.Add(swatches);

        card.Children.Add(Label("Text size", top: 14));
        card.Children.Add(Segmented(new[] { ("1", "Standard"), ("1.1", "Larger"), ("1.2", "Largest") },
            s.TextScale >= 1.15 ? "1.2" : s.TextScale >= 1.05 ? "1.1" : "1",
            v => Change(x => x.TextScale = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))));

        card.Children.Add(Divider());
        var glass = Check("Frosted glass", s.Glass);
        glass.Click += (_, _) => Change(x => x.Glass = glass.IsChecked == true);
        card.Children.Add(glass);
        card.Children.Add(Helper("On Windows 11, in the dark and the light theme. Turn it off for a solid look or on slower computers."));
        var motion = Check("Reduce motion", s.ReduceMotion);
        motion.Margin = new Thickness(0, 12, 0, 0);
        motion.Click += (_, _) => Change(x => x.ReduceMotion = motion.IsChecked == true);
        card.Children.Add(motion);
        card.Children.Add(Helper("Pages and buttons change without movement."));
        var steps = Check("Show every step of a check", s.ActivityOpen);
        steps.Margin = new Thickness(0, 12, 0, 0);
        steps.Click += (_, _) => Change(x => x.ActivityOpen = steps.IsChecked == true, rebuild: false);
        card.Children.Add(steps);
        card.Children.Add(Helper("The list of what AutoFix Scout is reading opens by itself during each check."));
        SettingsBody.Children.Add(Card(card));
    }

    private void AddDataSettings()
    {
        SettingsBody.Children.Add(Section("Your data"));
        var card = new StackPanel();
        var folder = SmallButton("Open the data folder");
        folder.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\""));
        };
        var clear = SmallButton(_confirmClear ? "Select again to clear" : "Clear history");
        clear.Margin = new Thickness(8, 0, 0, 0);
        clear.Click += (_, _) =>
        {
            if (!_confirmClear) { _confirmClear = true; BuildSettings(); return; }
            _confirmClear = false;
            try { using var db = new SessionLog(SessionLog.DefaultPath); db.DeleteAll(); BuildSettings("The history has been cleared."); }
            catch (Exception) { BuildSettings("The history could not be cleared."); }
            RefreshRecentActivity();
        };
        var row = new WrapPanel();
        row.Children.Add(folder);
        row.Children.Add(clear);
        card.Children.Add(row);
        var note = Text(_confirmClear
            ? "Clearing removes the list of checks and changes. Changes that are listed now can then no longer be undone from AutoFix Scout."
            : "Everything AutoFix Scout keeps (history, reports, copies of changed settings) stays on this computer.", "CaptionText");
        note.Margin = new Thickness(0, 10, 0, 0);
        card.Children.Add(note);
        SettingsBody.Children.Add(Card(card));

        SettingsBody.Children.Add(Section("Keyboard shortcuts"));
        var keys = new StackPanel();
        foreach (var (combo, what) in new[] { ("Ctrl + K", "Describe a problem"), ("Ctrl + ,", "Open or close the settings"), ("Ctrl + H", "History"), ("Esc", "Close this panel") })
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var key = Text(combo, "CaptionText");
            key.Width = 90;
            DockPanel.SetDock(key, Dock.Left);
            line.Children.Add(key);
            line.Children.Add(new TextBlock { Text = what });
            keys.Children.Add(line);
        }
        SettingsBody.Children.Add(Card(keys));
    }

    /// <summary>Saves one change, re-applies the look at once, and redraws the panel so the choice shows as selected.</summary>
    private void Change(Action<AppSettings> edit, bool rebuild = true)
    {
        var s = AppSettings.Load();
        edit(s);
        s.Save();
        ThemeManager.Refresh(this);
        if (rebuild) BuildSettings();
    }

    private TextBlock Label(string text, double top = 0)
    {
        var t = Text(text, "CaptionText");
        t.Margin = new Thickness(0, top, 0, 8);
        return t;
    }

    /// <summary>A row of small buttons where one is chosen (the chosen one is filled with the accent).</summary>
    private FrameworkElement Segmented(IEnumerable<(string Value, string Text)> options, string selected, Action<string> pick)
    {
        var row = new WrapPanel();
        foreach (var (value, text) in options)
        {
            var b = new Button
            {
                Content = text, Style = (Style)FindResource(value == selected ? "AccentButton" : "StandardButton"),
                MinWidth = 0, MinHeight = 30, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0),
            };
            AutomationProperties.SetName(b, value == selected ? $"{text}, selected" : text);
            b.Click += (_, _) => { if (value != selected) pick(value); };
            row.Children.Add(b);
        }
        return row;
    }

    private FrameworkElement Swatch(string key, string hex, string name, bool selected)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(color) };
        var ring = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2), Child = dot,
            BorderBrush = selected ? (Brush)FindResource("TextPrimaryBrush") : Brushes.Transparent,
        };
        var b = new Button
        {
            Content = ring, Style = (Style)FindResource("SubtleButton"), Tag = "", Padding = new Thickness(0), MinHeight = 0,
            Margin = new Thickness(0, 0, 6, 0), ToolTip = name,
        };
        Motion.SetHoverScale(b, 1.12);
        Motion.SetGlow(b, Color.FromArgb(0x99, color.R, color.G, color.B));
        Motion.SetRippleRadius(b, 16);
        AutomationProperties.SetName(b, selected ? $"{name} accent, selected" : $"{name} accent");
        b.Click += (_, _) => { if (!selected) Change(x => x.Accent = key); };
        return b;
    }
}
