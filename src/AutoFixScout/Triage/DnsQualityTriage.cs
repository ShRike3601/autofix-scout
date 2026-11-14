using System.Globalization;

namespace AutoFixScout.Triage;

/// <summary>
/// Judges the DNS speed numbers (see <see cref="DnsSpeedProbe"/>). Pure logic, tested with synthetic numbers.
/// The servers are named "your current DNS server" and "public DNS server 1 / 2"; no address appears in any text.
/// </summary>
public static class DnsQualityTriage
{
    public const double SlowMedianMs = 150;
    public const double MaxFailurePercent = 30;
    /// <summary>A public server is named as faster only when it is at least this many milliseconds AND <see cref="MinFactor"/> times faster.</summary>
    public const double MinGainMs = 50;
    public const double MinFactor = 2;

    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public static bool Slow(DnsStats s) => s.MedianMs > SlowMedianMs || s.FailurePercent > MaxFailurePercent;

    private static string Describe(string name, DnsStats? s) =>
        s == null ? $"{name}: not measured"
        : $"{name}: sent {s.Sent}, answered {s.Answered}, median {(s.MedianMs is { } m ? N(m) + " ms" : "n/a")}, failed {N(s.FailurePercent)} %";

    /// <summary>Numbers for the technical detail; empty when nothing was measured.</summary>
    public static string Detail(DnsMeasurement? m) => m == null ? ""
        : $"{Describe("your current DNS server", m.Current)}; {Describe("public DNS server 1", m.PublicA)}; {Describe("public DNS server 2", m.PublicB)}; names {m.Names}";

    /// <summary>
    /// Null when the name lookups are fine or cannot be judged. NetQualityDnsSlow when only the current server is slow;
    /// NetQualityDnsSlowEverywhere when the public servers are slow too (the whole connection is slow, not only DNS).
    /// </summary>
    public static Finding? Evaluate(DnsMeasurement? m)
    {
        if (m?.Current is not { Sent: > 0 } cur || !Slow(cur)) return null;
        var detail = Detail(m);
        // A public server that answered nothing is probably blocked on this network: it says nothing about the connection.
        var usable = new[] { m.PublicA, m.PublicB }.Where(p => p is { Answered: > 0 }).Select(p => p!).ToList();
        if (usable.Count > 0 && usable.All(Slow))
            return new Finding(FindingCode.NetQualityDnsSlowEverywhere,
                "Looking up website names was slow with every DNS server that was tested, including the public ones. " +
                "The whole internet connection is slow, so the DNS server is not the cause.", detail);

        var good = usable.Where(p => !Slow(p)).OrderBy(p => p.MedianMs).FirstOrDefault();
        var text = new List<string> { "Looking up website names is slow." };
        var failing = cur.FailurePercent > MaxFailurePercent;
        if (failing)
            text.Add(cur.MedianMs is null
                ? "Your current DNS server did not answer any of the lookups."
                : $"Your current DNS server did not answer about {Math.Round(cur.FailurePercent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)} of every 100 lookups.");
        if (cur.MedianMs is { } cm && cm > SlowMedianMs)
            text.Add($"Your current DNS server took about {N(Math.Round(cm))} ms for a lookup.");
        if (good?.MedianMs is { } gm)
        {
            if (cur.MedianMs is { } c && c - gm >= MinGainMs && c >= MinFactor * gm)
                text.Add($"A public DNS server answered in about {N(Math.Round(gm))} ms.");
            else if (failing)
                text.Add("A public DNS server answered the lookups that your current one did not.");
        }
        text.Add("A slow DNS server makes web pages start slowly even when the connection itself is fast.");
        return new Finding(FindingCode.NetQualityDnsSlow, string.Join(" ", text), detail);
    }

    internal static readonly Rung ChangeServer = new("dns_change_server", "ui.open_settings_page", 0,
        "The DNS server of the router or of the internet provider is slow. AutoFix Scout will open the network settings. " +
        "To use a different DNS server, please select your connection, then \"DNS server assignment\", and choose \"Edit\". " +
        "AutoFix Scout does not change this setting by itself.", "network_status");
}
