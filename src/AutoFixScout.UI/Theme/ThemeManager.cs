using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace AutoFixScout.UI;

/// <summary>
/// Follows the Windows light/dark/high-contrast setting and gives the window a Mica backdrop on Windows 11
/// (22H2 and later). Elsewhere, or with transparency or high contrast on, the window gets a solid background.
/// The palette is a swappable dictionary at index 0 of Application.Resources.MergedDictionaries.
/// </summary>
public static class ThemeManager
{
    public enum Mode { Light, Dark, HighContrast }

    public static Mode Current { get; private set; } = Mode.Light;

    private static readonly Uri LightUri = new("pack://application:,,,/AutoFixScout.UI;component/Theme/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/AutoFixScout.UI;component/Theme/Dark.xaml");

    public static Mode Detect()
    {
        if (SystemParameters.HighContrast) return Mode.HighContrast;
        // For screenshots and testing: AUTOFIXSCOUT_THEME=light|dark overrides the Windows setting.
        switch (Environment.GetEnvironmentVariable("AUTOFIXSCOUT_THEME")?.ToLowerInvariant())
        {
            case "light": return Mode.Light;
            case "dark": return Mode.Dark;
        }
        return ReadPersonalize("AppsUseLightTheme", 1) == 0 ? Mode.Dark : Mode.Light;
    }

    /// <summary>Loads the palette for the current system setting into the application resources.</summary>
    public static void ApplyPalette()
    {
        Current = Detect();
        var palette = Current switch
        {
            Mode.Dark => new ResourceDictionary { Source = DarkUri },
            Mode.HighContrast => HighContrastPalette(),
            _ => new ResourceDictionary { Source = LightUri },
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = palette; else merged.Add(palette);
    }

    /// <summary>Call from OnSourceInitialized. Applies the palette and backdrop, and re-applies both when Windows settings change.</summary>
    public static void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        ApplyAll(window, hwnd, source);
        source.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            // WM_SETTINGCHANGE ("ImmersiveColorSet" when light/dark flips), WM_THEMECHANGED, WM_SYSCOLORCHANGE
            if (msg is 0x001A or 0x031A or 0x0015)
                window.Dispatcher.BeginInvoke(() => ApplyAll(window, hwnd, source));
            return IntPtr.Zero;
        });
    }

    private static void ApplyAll(Window window, IntPtr hwnd, HwndSource source)
    {
        ApplyPalette();
        var dark = Current == Mode.Dark ? 1 : 0;
        // Title bar follows the theme (attribute 20 on Windows 10 20H1+ / 11, 19 on older Windows 10).
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));

        var mica = Current != Mode.HighContrast
                   && Environment.OSVersion.Version.Build >= 22621
                   && ReadPersonalize("EnableTransparency", 1) != 0;
        if (mica)
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            var type = 2; // DWMSBT_MAINWINDOW (Mica)
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0
                && DwmSetWindowAttribute(hwnd, 38, ref type, sizeof(int)) == 0)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                window.Background = Brushes.Transparent;
                return;
            }
        }
        var none = 1; // DWMSBT_NONE
        DwmSetWindowAttribute(hwnd, 38, ref none, sizeof(int));
        var zero = new Margins();
        DwmExtendFrameIntoClientArea(hwnd, ref zero);
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
        foreach (var k in new[] { "WindowBackgroundBrush", "CardBrush", "CardPressedBrush", "ButtonBrush", "ButtonPressedBrush",
                     "SoundTileBrush", "CameraTileBrush", "NetworkTileBrush", "NeutralTileBrush", "SuccessTileBrush",
                     "InfoTileBrush", "CautionTileBrush", "ProblemTileBrush", "SubtlePressedBrush", "FocusInnerBrush" })
            d[k] = window;
        foreach (var k in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "CardBorderBrush", "DividerBrush", "ButtonBorderBrush",
                     "FocusOuterBrush", "SoundGlyphBrush", "CameraGlyphBrush", "NetworkGlyphBrush", "NeutralGlyphBrush",
                     "SuccessBrush", "InfoBrush", "CautionBrush", "ProblemBrush", "RingTrackBrush" })
            d[k] = text;
        foreach (var k in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "CardHoverBrush", "ButtonHoverBrush", "SubtleHoverBrush" })
            d[k] = hi;
        d["OnAccentBrush"] = hiText;
        d["ShadowColor"] = Colors.Transparent;
        d["ShadowOpacity"] = 0.0;
        return d;
    }

    private static int ReadPersonalize(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch (Exception) { return fallback; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
