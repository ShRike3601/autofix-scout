using System.Text.RegularExpressions;

namespace AutoFixScout.Triage;

/// <summary>One Kernel-PnP device event (400, 410, 420 or 430) for a USB device.</summary>
public sealed record UsbPnpEvent(string InstanceId, DateTime WhenUtc);

/// <summary>A USB device that Windows added or removed repeatedly. Only the friendly name and class are kept for display.</summary>
public sealed record UsbChurn(string Name, string PnpClass, int Count);

/// <summary>USB part of the full check-up: repeated add/remove of one device, hub and controller errors, selective suspend.</summary>
public sealed class UsbState
{
    public IReadOnlyList<UsbChurn> Churn { get; set; } = new List<UsbChurn>();
    /// <summary>USB hub or controller errors and warnings in the System log in the last 3 days.</summary>
    public int HubErrors { get; set; }
    /// <summary>All Kernel-PnP device events for USB devices in the last 3 days.</summary>
    public int PnpEvents { get; set; }
    /// <summary>USB selective suspend of the active power plan; null = could not be read.</summary>
    public bool? SelectiveSuspendAc { get; set; }
    public bool? SelectiveSuspendDc { get; set; }
    public bool SelectiveSuspendOn => SelectiveSuspendAc == true || SelectiveSuspendDc == true;
}

public static class UsbHealth
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(3);
    /// <summary>The same device instance this many times in the window counts as disconnecting.</summary>
    public const int RepeatThreshold = 5;

    /// <summary>
    /// Every start-up and wake from sleep logs one or two Kernel-PnP events per device without any real disconnect,
    /// so a device is flagged only when it reconnects clearly more often than the computer starts or wakes.
    /// </summary>
    public static int ThresholdFor(int startsAndWakes) => RepeatThreshold + 2 * Math.Max(0, startsAndWakes);
    /// <summary>Hub and controller errors at or above which a finding is made.</summary>
    public const int ErrorThreshold = 3;

    public const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    public const string SelectiveSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    public static bool IsUsbInstance(string? id) =>
        id is not null && (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Instance ids that occur at least <paramref name="threshold"/> times, most frequent first. Non-USB ids are ignored.</summary>
    public static IReadOnlyList<(string InstanceId, int Count)> Repeated(IEnumerable<UsbPnpEvent> events, int threshold = RepeatThreshold) =>
        events.Where(e => IsUsbInstance(e.InstanceId))
            .GroupBy(e => e.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count())).Where(x => x.Item2 >= threshold)
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"powercfg /query" output for the selective suspend setting: 1 = enabled, 0 = disabled. Hex values only.</summary>
    public static (bool Ac, bool Dc)? ParseSelectiveSuspend(string output) =>
        PowerTriage.ParseAcDc(output) is { } v && v.Ac <= 1 && v.Dc <= 1 ? (v.Ac == 1, v.Dc == 1) : null;

    /// <summary>Strips anything that looks like an instance id or a long serial number from a friendly name.</summary>
    public static string SafeName(string? name)
    {
        var n = Regex.Replace(name ?? "", @"\b(USB|USBSTOR|HID|SCSI)[\\]\S+|\b[0-9A-Fa-f]{12,}\b", "").Trim();
        return n.Length == 0 ? "a USB device" : n;
    }
}
