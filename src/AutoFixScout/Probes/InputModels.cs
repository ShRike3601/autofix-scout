namespace AutoFixScout.Probes;

/// <summary>A keyboard, mouse or HID device from Windows (PnP classes Keyboard, Mouse, HIDClass).</summary>
public sealed record InputDevice(string Name, string InstanceId, string PnpClass, int? ProblemCode)
{
    public bool Disabled => ProblemCode == 22;
    public bool Errored => ProblemCode is { } c && c != 0 && c != 22;
    public bool IsKeyboard => PnpClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase);
    public bool IsMouse => PnpClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase);
    public bool IsTouchpad => Name.Contains("touch pad", StringComparison.OrdinalIgnoreCase) || Name.Contains("touchpad", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// probe.input: input devices, the precision touchpad switch and the four accessibility key features.
/// A flags value of null means it could not be read; a missing registry value is read as 0 (off), as Windows does.
/// </summary>
public sealed class InputState
{
    public IReadOnlyList<InputDevice> Devices { get; set; } = new List<InputDevice>();
    /// <summary>HKCU PrecisionTouchPad\Status Enabled: Yes = on, No = off, Unknown = no such setting.</summary>
    public Tri TouchpadEnabled { get; set; } = Tri.Unknown;
    /// <summary>HKCU PrecisionTouchPad LeaveOnWithMouse.</summary>
    public Tri LeaveOnWithMouse { get; set; } = Tri.Unknown;
    public int? StickyKeysFlags { get; set; }
    public int? FilterKeysFlags { get; set; }
    public int? ToggleKeysFlags { get; set; }
    public int? MouseKeysFlags { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();

    /// <summary>Bit 1 of every accessibility Flags value (SKF_STICKYKEYSON, FKF_FILTERKEYSON, TKF_TOGGLEKEYSON, MKF_MOUSEKEYSON).</summary>
    public const int OnBit = 1;
    public static bool IsOn(int? flags) => flags is { } f && (f & OnBit) != 0;
    public static int ClearOn(int flags) => flags & ~OnBit;
    public bool HasTouchpad => Devices.Any(d => d.IsTouchpad);
    public bool HasMouse => Devices.Any(d => d.IsMouse && !d.IsTouchpad);
}

public interface IInputProbe { InputState Read(); }
