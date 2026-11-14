using System.Globalization;
using System.Xml.Linq;

namespace AutoFixScout.Probes;

/// <summary>One battery from the capacity report. Only numbers and the chemistry are kept; the name, serial number and maker are never read.</summary>
public sealed record BatteryInfo(int? DesignCapacity, int? FullChargeCapacity, int? CycleCount, string? Chemistry)
{
    /// <summary>1 - full / design, as a percentage. Null when either value is missing or the design capacity is zero.</summary>
    public double? WearPercent => DesignCapacity is > 0 && FullChargeCapacity is >= 0
        ? Math.Max(0, 100.0 * (DesignCapacity.Value - FullChargeCapacity.Value) / DesignCapacity.Value) : null;
}

/// <summary>One entry of the report's capacity history: the full-charge capacity at the start of a period (mWh).</summary>
public sealed record BatteryHistoryPoint(DateTime Start, int FullChargeCapacity);

public sealed class BatteryReportData
{
    public IReadOnlyList<BatteryInfo> Batteries { get; init; } = Array.Empty<BatteryInfo>();
    public IReadOnlyList<BatteryHistoryPoint> History { get; init; } = Array.Empty<BatteryHistoryPoint>();

    /// <summary>Batteries with a usable design and full-charge capacity.</summary>
    public IEnumerable<BatteryInfo> Usable => Batteries.Where(b => b.WearPercent is not null);

    public int? DesignTotal => Usable.Any() ? Usable.Sum(b => b.DesignCapacity!.Value) : null;
    public int? FullTotal => Usable.Any() ? Usable.Sum(b => b.FullChargeCapacity!.Value) : null;

    /// <summary>Wear of all usable batteries together (1 - full / design), as a percentage.</summary>
    public double? WearPercent => DesignTotal is > 0 && FullTotal is { } f ? Math.Max(0, 100.0 * (DesignTotal.Value - f) / DesignTotal.Value) : null;

    /// <summary>The highest cycle count over the batteries that report one above zero (many computers report 0 for "unknown").</summary>
    public int? MaxCycles => Batteries.Where(b => b.CycleCount is > 0).Select(b => b.CycleCount!.Value).DefaultIfEmpty(0).Max() is var m and > 0 ? m : null;
}

/// <summary>Pure parser for the XML written by "powercfg /batteryreport /xml". It does not read the computer name, serial number or maker.</summary>
public static class BatteryReportParser
{
    /// <summary>The parsed report, or null when the text is not a battery report.</summary>
    public static BatteryReportData? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch (Exception) { return null; }
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "BatteryReport") return null;

        var batteries = new List<BatteryInfo>();
        foreach (var b in Children(Child(root, "Batteries"), "Battery"))
            batteries.Add(new BatteryInfo(Int(b, "DesignCapacity"), Int(b, "FullChargeCapacity"), Int(b, "CycleCount"), Text(b, "Chemistry")));

        var history = new List<BatteryHistoryPoint>();
        foreach (var h in Children(Child(root, "History"), "HistoryEntry"))
        {
            var full = IntAttr(h, "FullChargeCapacity");
            if (full is > 0 && DateTime.TryParse(Attr(h, "LocalStartDate") ?? Attr(h, "StartDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                history.Add(new BatteryHistoryPoint(start, full.Value));
        }
        return new BatteryReportData { Batteries = batteries, History = history.OrderBy(x => x.Start).ToList() };
    }

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    private static IEnumerable<XElement> Children(XElement? parent, string name) => parent?.Elements().Where(e => e.Name.LocalName == name) ?? Enumerable.Empty<XElement>();
    private static string? Text(XElement parent, string name) => Child(parent, name)?.Value.Trim() is { Length: > 0 } t ? t : null;
    private static int? Int(XElement parent, string name) => ToInt(Text(parent, name));
    private static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
    private static int? IntAttr(XElement e, string name) => ToInt(Attr(e, name));
    private static int? ToInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
}
