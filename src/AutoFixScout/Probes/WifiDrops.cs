using System.Xml.Linq;

namespace AutoFixScout.Probes;

/// <summary>
/// Disconnect (8003) and connect (8001) counts of the WLAN-AutoConfig log over the last days. The reason of a disconnect
/// is kept as a category only; the network name, profile name and access point address are never read from the event.
/// </summary>
public sealed record WifiDropStats(int Disconnects, int Connects, int ByPerson, int SignalLost, int Driver, int Other, int SleepsAndShutdowns = 0, int SignInFailures = 0)
{
    /// <summary>Disconnects that were not caused by a person.</summary>
    /// <summary>Going to sleep or shutting down also logs a disconnect; those are subtracted.</summary>
    public int Unintentional => Math.Max(0, Disconnects - ByPerson - SleepsAndShutdowns);

    /// <summary>"3 disconnects (1 signal lost, 2 driver), 5 connects"</summary>
    public string Describe() =>
        $"{Disconnects} disconnects ({ByPerson} by the person, {SignalLost} signal lost, {Driver} driver, {Other} other), {Connects} connects";
}

public static class WifiDrops
{
    /// <summary>Unintentional disconnects in the window from which the finding is raised.</summary>
    public const int Threshold = 6;
    public const int Days = 3;

    public const string Query =
        "*[System[(EventID=8003 or EventID=8001) and TimeCreated[timediff(@SystemTime) <= 259200000]]]";

    public enum Category { Person, SignalLost, Driver, Other }

    /// <summary>
    /// WLAN reason codes: 2 and 3 are requests by the user. 0 is reported as "by the driver". The text is only a fallback for
    /// signal loss (the code is what is trusted first); the text is never stored.
    /// </summary>
    public static Category Categorize(int reasonCode, string? reasonText)
    {
        if (reasonCode is 2 or 3) return Category.Person;
        var t = (reasonText ?? "").ToLowerInvariant();
        if (t.Contains("signal") || t.Contains("link quality") || t.Contains("out of range") || t.Contains("lost")) return Category.SignalLost;
        if (reasonCode == 0 || t.Contains("driver")) return Category.Driver;
        return Category.Other;
    }

    /// <summary>Event id, ReasonCode and Reason text from the event XML. Nothing else is read.</summary>
    public static (int Id, int ReasonCode, string? Reason) ParseEvent(string xml)
    {
        var doc = XDocument.Parse(xml);
        var ns = doc.Root!.Name.Namespace;
        var id = (int?)doc.Root.Element(ns + "System")?.Element(ns + "EventID") ?? 0;
        string? Data(string name) => doc.Root.Element(ns + "EventData")?.Elements(ns + "Data")
            .FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value;
        return (id, int.TryParse(Data("ReasonCode"), out var code) ? code : -1, Data("Reason"));
    }

    public static WifiDropStats Count(IEnumerable<(int Id, int ReasonCode, string? Reason)> events)
    {
        int dis = 0, con = 0, person = 0, signal = 0, driver = 0, other = 0;
        foreach (var (id, code, reason) in events)
        {
            if (id == 8001) { con++; continue; }
            if (id != 8003) continue;
            dis++;
            switch (Categorize(code, reason))
            {
                case Category.Person: person++; break;
                case Category.SignalLost: signal++; break;
                case Category.Driver: driver++; break;
                default: other++; break;
            }
        }
        return new WifiDropStats(dis, con, person, signal, driver, other);
    }

    /// <summary>
    /// PnPCapabilities of the adapter. Bit 0x8 or 0x10 set (value 24 sets both) means Windows may NOT turn the adapter off to
    /// save power. No value means the default, which allows it.
    /// </summary>
    public static Tri MayTurnOffToSavePower(object? pnpCapabilities)
    {
        if (pnpCapabilities is null) return Tri.Yes;
        if (pnpCapabilities is int v) return (v & 0x18) != 0 ? Tri.No : Tri.Yes;
        return Tri.Unknown;
    }
}
