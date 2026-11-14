using System.Windows;
using System.Windows.Controls;
using AutoFixScout.Probes;
using AutoFixScout.Weekly;

namespace AutoFixScout.UI;

public partial class MainWindow
{
    /// <summary>The optional weekly check-up: one checkbox, its helper text, and the last result.</summary>
    private void AddWeeklyCheckupRow(StackPanel card)
    {
        Tri state;
        try { state = WeeklyCheckupSetup.ReadState(); } catch (Exception) { state = Tri.Unknown; }
        card.Children.Add(Divider());
        var box = Check("Weekly check-up", state == Tri.Yes);
        card.Children.Add(box);
        card.Children.Add(Helper("Once a week AutoFix Scout checks this computer in the background and shows a notification only when something needs attention. It changes nothing."));
        if (state == Tri.Yes)
        {
            var last = Text(WeeklyCheckup.Describe(WeeklyCheckup.Load()), "CaptionText");
            last.Margin = new Thickness(28, 4, 0, 0);
            card.Children.Add(last);
        }
        box.Click += async (_, _) =>
        {
            var on = box.IsChecked == true;
            box.IsEnabled = false;
            var (ok, _) = await Task.Run(() => on ? WeeklyCheckupSetup.On() : WeeklyCheckupSetup.Off());
            BuildSettings(ok ? (on ? "The weekly check-up has been switched on." : "The weekly check-up has been switched off.")
                : "The weekly check-up could not be changed.");
        };
    }
}
