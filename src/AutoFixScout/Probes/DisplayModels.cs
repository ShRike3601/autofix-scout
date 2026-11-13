namespace AutoFixScout.Probes;

/// <summary>A display adapter (graphics chip). Reported only: the app never changes display adapters or their drivers.</summary>
public sealed record DisplayAdapterInfo(string Name, string InstanceId, int? ProblemCode);

/// <summary>One display mode: size in pixels and refresh rate in Hz.</summary>
public readonly record struct DisplayMode(int Width, int Height, int Refresh)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>One active screen: current mode, every supported mode and the display scale in percent (null = unknown).</summary>
public sealed record ScreenInfo(string Name, DisplayMode Current, IReadOnlyList<DisplayMode> Modes, int? ScalePercent)
{
    /// <summary>The largest supported resolution by pixel count (ties: the larger width). Null when no modes were listed.</summary>
    public DisplayMode? Native => Modes.Count == 0 ? null
        : Modes.OrderByDescending(m => m.Pixels).ThenByDescending(m => m.Width).First();

    /// <summary>The highest supported refresh rate at the current resolution; null when no mode matches.</summary>
    public int? BestRefresh => Modes.Where(m => m.Width == Current.Width && m.Height == Current.Height)
        .Select(m => (int?)m.Refresh).Max();
}

/// <summary>probe.display. Null counts = could not read, never "fine".</summary>
public sealed class DisplayState
{
    public IReadOnlyList<DisplayAdapterInfo> Adapters { get; set; } = new List<DisplayAdapterInfo>();
    /// <summary>Screens Windows detects as plugged in (WmiMonitorBasicDisplayParams, Active = true).</summary>
    public int? ConnectedScreens { get; set; }
    /// <summary>Screens currently showing the desktop (EnumDisplayDevices: active monitors on attached outputs).</summary>
    public int? ShowingScreens { get; set; }
    /// <summary>Built-in screen brightness 0-100 (WmiMonitorBrightness); null on desktops or when Windows does not report it.</summary>
    public int? BrightnessPercent { get; set; }
    /// <summary>Active screens with current mode, supported modes and scale (read-only; empty when unreadable).</summary>
    public IReadOnlyList<ScreenInfo> Screens { get; set; } = new List<ScreenInfo>();
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IDisplayProbe { DisplayState Read(); }
