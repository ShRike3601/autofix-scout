namespace AutoFixScout.Probes;

/// <summary>Result of one ping: whether a reply came, and its round trip in milliseconds.</summary>
public readonly record struct PingSample(bool Ok, double Ms);

/// <summary>
/// What the test measured for one target. Holds numbers only: never an address, so the state can be logged and put in a
/// report without leaking where the computer is.
/// </summary>
public sealed record PingStats(int Sent, int Received, double? MedianMs, double? JitterMs, double LossPercent)
{
    /// <summary>False when nothing came back: the target blocks ping (many routers do) or is not reachable.</summary>
    public bool Answers => Received > 0;

    public static PingStats From(IReadOnlyList<PingSample> samples)
    {
        var ok = samples.Where(s => s.Ok).Select(s => s.Ms).ToList();
        var sent = samples.Count;
        return new PingStats(sent, ok.Count, ConnectionStatistics.Median(ok), ConnectionStatistics.Jitter(samples),
            sent == 0 ? 0 : 100.0 * (sent - ok.Count) / sent);
    }
}

/// <summary>The three statistics, pure and unit tested.</summary>
public static class ConnectionStatistics
{
    /// <summary>Middle value (mean of the two middle values for an even count); null for an empty list.</summary>
    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var v = values.OrderBy(x => x).ToList();
        var mid = v.Count / 2;
        return v.Count % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2;
    }

    /// <summary>
    /// Mean absolute difference of consecutive samples in send order. A lost sample breaks the chain: only two replies
    /// that follow each other directly are compared. Null when there is no such pair.
    /// </summary>
    public static double? Jitter(IReadOnlyList<PingSample> samples)
    {
        double sum = 0; var n = 0;
        for (var i = 1; i < samples.Count; i++)
            if (samples[i].Ok && samples[i - 1].Ok) { sum += Math.Abs(samples[i].Ms - samples[i - 1].Ms); n++; }
        return n == 0 ? null : sum / n;
    }
}

/// <summary>probe.connection_quality: the measured numbers. Null means "not measured", never "good".</summary>
public sealed class QualityState
{
    /// <summary>False when no default gateway was found (then <see cref="Gateway"/> is null).</summary>
    public bool GatewayFound { get; set; }
    public PingStats? Gateway { get; set; }
    /// <summary>The two public targets; null when the test did not run.</summary>
    public PingStats? InternetA { get; set; }
    public PingStats? InternetB { get; set; }
    /// <summary>The connection to the router is Wi-Fi.</summary>
    public bool OnWifi { get; set; }
    public int? WifiSignalPercent { get; set; }
    public Tri Supports5GHz { get; set; } = Tri.Unknown;
    /// <summary>Link speed reported by Windows for the adapter, in megabits per second.</summary>
    public int? LinkSpeedMbps { get; set; }
    public double DurationSeconds { get; set; }
    /// <summary>Name lookup (DNS) speed; null when not measured.</summary>
    public DnsMeasurement? Dns { get; set; }
    public IReadOnlyList<string> ProbeErrors { get; set; } = new List<string>();
}
