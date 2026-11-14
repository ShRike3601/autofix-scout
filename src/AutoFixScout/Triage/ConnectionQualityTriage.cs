using System.Globalization;

namespace AutoFixScout.Triage;

/// <summary>
/// "The internet is slow" / "games lag online": decides from measured ping numbers whether the problem is inside the
/// home or office network (Wi-Fi, distance, interference) or with the internet connection (provider). Pure logic on a
/// <see cref="QualityState"/>, so it is tested with synthetic samples. A router that does not answer ping is Unknown,
/// never a fault: many routers block ICMP.
/// </summary>
public static class ConnectionQualityTriage
{
    // Router (gateway) is bad when any of these is exceeded. The delay limit applies on Wi-Fi only.
    public const double GatewayMaxLossPercent = 2;
    public const double GatewayMaxJitterMs = 15;
    public const double GatewayMaxMedianWifiMs = 20;

    // Internet is bad when any of these is exceeded (judged on the servers that answered).
    public const double InternetMaxLossPercent = 2;
    public const double InternetMaxMedianMs = 80;
    public const double InternetMaxJitterMs = 30;

    /// <summary>A target with fewer pings than this was stopped early; the result is not judged.</summary>
    public const int MinSamples = 20;

    private static Finding F(FindingCode code, string plain, string detail) => new(code, plain, detail);

    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>"6" for 5.6 percent lost: the number of packets out of every 100.</summary>
    private static string Per100(double loss) => Math.Round(loss, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    public static bool GatewayBad(PingStats g, bool onWifi) =>
        g.LossPercent > GatewayMaxLossPercent
        || g.JitterMs > GatewayMaxJitterMs
        || (onWifi && g.MedianMs > GatewayMaxMedianWifiMs);

    public static bool InternetBad(PingStats p) =>
        p.LossPercent > InternetMaxLossPercent || p.MedianMs > InternetMaxMedianMs || p.JitterMs > InternetMaxJitterMs;

    /// <summary>The internet servers that answered; the best one (least loss, then least delay) stands for the connection.</summary>
    private static List<PingStats> Answering(QualityState s) =>
        new[] { s.InternetA, s.InternetB }.Where(p => p is { Answers: true }).Select(p => p!).ToList();

    private static PingStats Best(IEnumerable<PingStats> p) => p.OrderBy(x => x.LossPercent).ThenBy(x => x.MedianMs).First();

    private static string Describe(string name, PingStats? p) =>
        p == null ? $"{name}: not measured"
        : !p.Answers ? $"{name}: sent {p.Sent}, no reply"
        : $"{name}: sent {p.Sent}, received {p.Received}, median {N(p.MedianMs!.Value)} ms, jitter {(p.JitterMs is { } j ? N(j) + " ms" : "n/a")}, loss {N(p.LossPercent)} %";

    private static string Detail(QualityState s) =>
        $"{Describe("router", s.Gateway)}; {Describe("internet server 1", s.InternetA)}; {Describe("internet server 2", s.InternetB)}; " +
        $"connection {(s.OnWifi ? "Wi-Fi" : "wired or unknown")}; Wi-Fi signal {(s.WifiSignalPercent is { } w ? w + " %" : "n/a")}; " +
        $"link speed {(s.LinkSpeedMbps is { } l ? l + " Mbps" : "n/a")}; test length {N(s.DurationSeconds)} s";

    /// <summary>One finding. Never empty.</summary>
    public static IReadOnlyList<Finding> Evaluate(QualityState s)
    {
        var detail = Detail(s);
        if (s.InternetA == null || s.InternetB == null || s.InternetA.Sent < MinSamples || s.InternetB.Sent < MinSamples)
            return new[] { F(FindingCode.ProbeIncomplete, "AutoFix Scout could not complete the connection quality test.",
                string.Join("; ", s.ProbeErrors.Append(detail))) };

        var gateway = s.Gateway is { Answers: true } g ? g : null; // silent or missing router = Unknown
        var internet = Answering(s);

        if (gateway != null && GatewayBad(gateway, s.OnWifi))
            return new[] { F(FindingCode.NetQualityLocalNetwork, LocalMessage(s, gateway), detail) };

        if (internet.Count == 0)
            return new[] { F(FindingCode.NetQualityNotMeasurable, gateway == null
                ? "Neither the router nor servers on the internet answered the test. Many networks block this kind of test, so the connection quality could not be judged."
                : "The router answered steadily, but servers on the internet did not answer the test, so the quality of the internet connection could not be judged. Some networks block this kind of test.", detail) };

        if (internet.All(InternetBad))
        {
            var best = Best(internet);
            return new[] { F(gateway == null ? FindingCode.NetQualityInternetUnclear : FindingCode.NetQualityInternet, InternetMessage(best, gateway == null), detail) };
        }

        var bestOk = Best(internet);
        var routerPart = gateway == null
            ? "The router does not answer this test, so only the connection to the internet was measured. "
            : $"The router answered in about {N(gateway.MedianMs!.Value)} ms. ";
        return new[] { F(FindingCode.NetQualitySteady,
            "The connection was steady during the test. " + routerPart +
            $"Servers on the internet answered in about {N(bestOk.MedianMs!.Value)} ms, and {N(bestOk.LossPercent)} percent of the packets were lost.", detail) };
    }

    private static string LocalMessage(QualityState s, PingStats g)
    {
        var parts = new List<string>();
        if (g.LossPercent > GatewayMaxLossPercent)
            parts.Add($"About {Per100(g.LossPercent)} of every 100 packets were lost between this computer and the router.");
        if (g.JitterMs > GatewayMaxJitterMs)
            parts.Add($"The delay between this computer and the router varied by about {N(g.JitterMs!.Value)} ms from one packet to the next.");
        if (s.OnWifi && g.MedianMs > GatewayMaxMedianWifiMs)
            parts.Add($"The router took about {N(g.MedianMs!.Value)} ms to answer, which is slow for a Wi-Fi connection.");
        if (s.OnWifi && s.WifiSignalPercent is < NetworkChecks.WeakSignalPercent and var pct)
            parts.Add($"The Wi-Fi signal is weak ({pct} percent).");
        parts.Add(s.OnWifi
            ? "The problem is most likely inside your home or office network, in the Wi-Fi connection to the router, and not with your internet provider."
            : "The problem is most likely inside your home or office network, between this computer and the router, and not with your internet provider.");
        return string.Join(" ", parts);
    }

    private static string InternetMessage(PingStats p, bool routerSilent)
    {
        var parts = new List<string> { routerSilent ? "The connection to the internet was poor." : "The router answered normally, but the connection to the internet was poor." };
        if (p.LossPercent > InternetMaxLossPercent)
            parts.Add($"About {Per100(p.LossPercent)} of every 100 packets were lost on the way to the internet.");
        if (p.MedianMs > InternetMaxMedianMs)
            parts.Add($"Servers on the internet took about {N(p.MedianMs!.Value)} ms to answer, which is slow.");
        if (p.JitterMs > InternetMaxJitterMs)
            parts.Add($"The delay to the internet varied by about {N(p.JitterMs!.Value)} ms from one packet to the next.");
        parts.Add(routerSilent
            ? "The router does not answer this test, so it cannot be said whether the cause is in your home or office network or with your internet provider."
            : "The problem is most likely with the internet connection or the internet provider, and not with this computer or the Wi-Fi.");
        return string.Join(" ", parts);
    }

    private static readonly Rung PauseTraffic = new("pause_traffic", "ui.human_step", 0,
        "Please pause large downloads, uploads, video streams and updates on all devices that use this network. Then run the check again.");

    private static readonly Rung CheckCable = new("check_cable_quality", "ui.human_step", 0,
        "Please check that the network cable is firmly plugged in at both ends and is not damaged. If possible, try a different cable or a different port on the router. Then run the check again.");

    public static IReadOnlyList<Rung> Ladder(QualityState s, IReadOnlyList<Finding> findings)
    {
        var rungs = new List<Rung>();
        switch (findings[0].Code)
        {
            case FindingCode.NetQualityLocalNetwork:
                if (s.OnWifi)
                {
                    rungs.Add(NetworkTriage.MoveCloser);
                    if (s.Supports5GHz == Tri.Yes) rungs.Add(NetworkTriage.Use5GHz);
                    rungs.Add(PauseTraffic);
                }
                else rungs.Add(CheckCable);
                rungs.Add(NetworkTriage.RestartRouter);
                break;
            case FindingCode.NetQualityInternet or FindingCode.NetQualityInternetUnclear:
                if (findings[0].Code == FindingCode.NetQualityInternetUnclear && s.OnWifi) rungs.Add(NetworkTriage.MoveCloser);
                rungs.Add(PauseTraffic);
                rungs.Add(NetworkTriage.RestartRouter);
                var best = Best(Answering(s));
                rungs.Add(new("contact_provider", "ui.human_step", 0,
                    "If the problem continues after the router has been restarted, please contact your internet provider. Please give them these numbers: " +
                    $"about {Per100(best.LossPercent)} of every 100 packets lost, a typical delay of {N(best.MedianMs!.Value)} ms and a delay variation of {(best.JitterMs is { } j ? N(j) : "unknown")} ms. " +
                    $"They were measured over about {N(s.DurationSeconds)} seconds."));
                break;
        }
        return rungs;
    }

    public static string? NotFixedHint(IReadOnlyList<Finding> f) => f[0].Code switch
    {
        FindingCode.NetQualityLocalNetwork => "The measurements point to your home or office network, not to the internet provider. " +
            "Moving closer to the router, or using a network cable, usually makes the connection steadier.",
        FindingCode.NetQualityInternet => "The measurements point to the internet connection. Your internet provider can check the line; please give them the numbers from this check.",
        _ => null,
    };
}
