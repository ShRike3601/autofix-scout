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

    private static readonly Uri DarkGlassUri = new("pack://application:,,,/AutoFixScout.UI;component/Theme/DarkGlass.xaml");

    /// <summary>True while the window shows the Windows 11 acrylic backdrop (frosted glass).</summary>
    public static bool Glass { get; private set; }

    /// <summary>Loads the palette for the current mode into the application resources.</summary>
    public static void ApplyPalette(bool glass = false)
    {
        Current = Detect();
        var palette = Current switch
        {
            Mode.Light => new ResourceDictionary { Source = LightUri },
            Mode.HighContrast => HighContrastPalette(),
            // Later merged dictionaries win, so the glass surfaces override the solid ones.
            _ when glass => new ResourceDictionary { MergedDictionaries = { new ResourceDictionary { Source = DarkUri }, new ResourceDictionary { Source = DarkGlassUri } } },
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
        Glass = Detect() == Mode.Dark && SetBackdrop(window, hwnd, on: true);
        if (!Glass) SetBackdrop(window, hwnd, on: false);
        // With the frame extended over the window, DWM draws the native Windows 11 caption buttons; use those and hide ours.
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(window) is { } chrome) chrome.UseAeroCaptionButtons = Glass;
        if (window.FindName("CaptionButtons") is UIElement own) own.Visibility = Glass ? Visibility.Collapsed : Visibility.Visible;
        ApplyPalette(Glass);
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

    /// <summary>
    /// Frosted glass: the Windows 11 (22H2+) acrylic system backdrop behind the whole window. Needs the WPF surface to be
    /// transparent and the DWM frame extended over the client area. Off on Windows 10, when "Transparency effects" is
    /// off, and with AUTOFIXSCOUT_GLASS=0. Returns whether glass is now on.
    /// </summary>
    private static bool SetBackdrop(Window window, IntPtr hwnd, bool on)
    {
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
        var source = HwndSource.FromHwnd(hwnd);
        if (on)
        {
            if (Environment.OSVersion.Version.Build < 22621 || Environment.GetEnvironmentVariable("AUTOFIXSCOUT_GLASS") == "0") return false;
            if (!TransparencyEffectsOn()) return false;
            var acrylic = 3; // DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_TRANSIENTWINDOW (acrylic)
            if (DwmSetWindowAttribute(hwnd, 38, ref acrylic, sizeof(int)) != 0) return false;
            if (chrome != null) chrome.GlassFrameThickness = new Thickness(-1);
            var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref all);
            if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            return true;
        }
        var none = 1; // DWMSBT_NONE
        DwmSetWindowAttribute(hwnd, 38, ref none, sizeof(int));
        if (chrome != null) chrome.GlassFrameThickness = new Thickness(0);
        var zero = new Margins();
        DwmExtendFrameIntoClientArea(hwnd, ref zero);
        return false;
    }

    private static bool TransparencyEffectsOn()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("EnableTransparency") is not int v || v != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
