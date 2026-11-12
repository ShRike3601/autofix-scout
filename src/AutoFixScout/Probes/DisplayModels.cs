namespace AutoFixScout.Probes;

/// <summary>A display adapter (graphics chip). Reported only: the app never changes display adapters or their drivers.</summary>
public sealed record DisplayAdapterInfo(string Name, string InstanceId, int? ProblemCode);

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
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}

public interface IDisplayProbe { DisplayState Read(); }
