using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AutoFixScout.UI;

/// <summary>
/// Picks the palette: dark by default (the app's own look), the user's system colours when Windows high contrast is on,
/// light only when asked for with AUTOFIXSCOUT_THEME=light. Surfaces are solid; the window draws its own title bar.
/// The palette is a swappable dictionary at index 0 of Application.Resources.MergedDictionaries.
/// </summary>
public static class ThemeManager
{
    public enum Mode { Light, Dark, HighContrast }

    public static Mode Current { get; private set; } = Mode.Dark;

    private static readonly Uri LightUri = new("pack://application:,,,/AutoFixScout.UI;component/Theme/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/AutoFixScout.UI;component/Theme/Dark.xaml");

    public static Mode Detect()
    {
        if (SystemParameters.HighContrast) return Mode.HighContrast;
        // For screenshots and testing: AUTOFIXSCOUT_THEME=light|dark.
        return Environment.GetEnvironmentVariable("AUTOFIXSCOUT_THEME")?.ToLowerInvariant() == "light" ? Mode.Light : Mode.Dark;
    }

    /// <summary>Loads the palette for the current mode into the application resources.</summary>
    public static void ApplyPalette()
    {
        Current = Detect();
        var palette = Current switch
        {
            Mode.Light => new ResourceDictionary { Source = LightUri },
            Mode.HighContrast => HighContrastPalette(),
            _ => new ResourceDictionary { Source = DarkUri },
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = palette; else merged.Add(palette);
    }

    /// <summary>Call from OnSourceInitialized. Applies the palette, and re-applies it when Windows settings change (high contrast on or off).</summary>
    public static void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        ApplyAll(window, hwnd);
        HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            // WM_SETTINGCHANGE, WM_THEMECHANGED, WM_SYSCOLORCHANGE
            if (msg is 0x001A or 0x031A or 0x0015)
                window.Dispatcher.BeginInvoke(() => ApplyAll(window, hwnd));
            return IntPtr.Zero;
        });
    }

    private static void ApplyAll(Window window, IntPtr hwnd)
    {
        ApplyPalette();
        // The app draws its own title bar; these only affect the system frame around it.
        var dark = Current == Mode.Dark ? 1 : 0;
        // Attribute 20 on Windows 10 20H1+ / 11, 19 on older Windows 10.
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        var round = 2; // DWMWCP_ROUND: keep rounded corners on Windows 11 (ignored on Windows 10)
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "WindowBackgroundBrush");
    }

    /// <summary>High contrast: every token maps onto the user's chosen system colours.</summary>
    private static ResourceDictionary HighContrastPalette()
    {
        // Plain system brushes; the whole palette is rebuilt on WM_SYSCOLORCHANGE.
        var d = new ResourceDictionary();
        var window = SystemColors.WindowBrush;
        var text = SystemColors.WindowTextBrush;
        var hi = SystemColors.HighlightBrush;
        var hiText = SystemColors.HighlightTextBrush;
        // Hover and selection keep the window colour behind text (so text stays readable); the highlight shows as an outline or bar.
        foreach (var k in new[] { "WindowBackgroundBrush", "SidebarBrush", "CardBrush", "CardHoverBrush", "CardPressedBrush",
                     "SurfaceBrush", "InputBrush", "ButtonBrush", "ButtonHoverBrush", "ButtonPressedBrush",
                     "SubtleHoverBrush", "SubtlePressedBrush", "NavSelectedBrush", "AccentSoftBrush", "StepPendingFillBrush",
                     "LogoBrush", "NeutralTileBrush", "SuccessTileBrush", "InfoTileBrush", "CautionTileBrush",
                     "ProblemTileBrush", "FocusInnerBrush", "OnStatusBrush" })
            d[k] = window;
        foreach (var k in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "CardBorderBrush", "DividerBrush", "InputBorderBrush",
                     "ButtonBorderBrush", "FocusOuterBrush", "NeutralGlyphBrush", "StepTrackBrush", "RingTrackBrush",
                     "LogoGlyphBrush", "LogoAccentBrush", "SuccessBrush", "InfoBrush", "CautionBrush", "ProblemBrush" })
            d[k] = text;
        foreach (var k in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentFillBrush", "CardHoverBorderBrush",
                     "CloseHoverBrush" })
            d[k] = hi;
        d["OnAccentBrush"] = hiText;
        return d;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
