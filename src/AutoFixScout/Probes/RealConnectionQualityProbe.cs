using System.Net;
using System.Net.NetworkInformation;

namespace AutoFixScout.Probes;

/// <summary>Sends one ping. A seam so the timing logic can be tested without a network.</summary>
public interface IPingSender { Task<PingSample> SendAsync(IPAddress target, int timeoutMs, CancellationToken ct); }

public sealed class RealPingSender : IPingSender
{
    public async Task<PingSample> SendAsync(IPAddress target, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var r = await ping.SendPingAsync(target, timeoutMs).WaitAsync(ct);
            return r.Status == IPStatus.Success ? new PingSample(true, r.RoundtripTime) : new PingSample(false, 0);
        }
        catch (PingException) { return new PingSample(false, 0); }
    }
}

/// <summary>
/// Measures delay, jitter and packet loss to the default gateway and to two well-known public addresses, all at the same
/// time, one ping per target every 250 ms for about 15 seconds. Read-only: it sends ordinary ICMP echo requests only.
/// </summary>
public sealed class RealConnectionQualityProbe
{
    public static readonly IPAddress PublicA = IPAddress.Parse("1.1.1.1");
    public static readonly IPAddress PublicB = IPAddress.Parse("8.8.8.8");
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    public const int TimeoutMs = 1000;

    private readonly IPingSender _sender;
    public RealConnectionQualityProbe(IPingSender? sender = null) => _sender = sender ?? new RealPingSender();

    public async Task<QualityState> ReadAsync(TimeSpan? duration = null, TimeSpan? interval = null, CancellationToken ct = default)
    {
        var state = new QualityState();
        var errors = new List<string>();
        IPAddress? gateway = null;
        try
        {
            Activity.Report(ActivityKind.Network, "Finding the default gateway and the type of connection (no addresses are kept)");
            var found = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (Nic: n, Gw: n.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))))
                .FirstOrDefault(x => x.Gw != null);
            if (found.Nic != null)
            {
                gateway = found.Gw;
                state.GatewayFound = true;
                state.OnWifi = found.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                if (found.Nic.Speed is > 0 and var bps) state.LinkSpeedMbps = (int)Math.Min(int.MaxValue, bps / 1_000_000);
            }
        }
        catch (Exception ex) { AppLog.Warn("ConnectionQuality probe", ex); errors.Add("gateway: " + ex.Message); }

        if (state.OnWifi)
            try
            {
                if (NetworkChecks.ReadWifi() is { } wifi) { state.WifiSignalPercent = wifi.SignalPercent; state.Supports5GHz = wifi.Supports5GHz; }
            }
            catch (Exception ex) { AppLog.Warn("ConnectionQuality probe", ex); errors.Add("wifi: " + ex.Message); }

        var length = duration ?? DefaultDuration;
        var step = interval ?? Interval;
        Activity.Report(ActivityKind.Network,
            $"Measuring ping delay, jitter and packet loss to the router and to two public servers for {Activity.Seconds(length)} ({Activity.Count((int)Math.Round(1000 / step.TotalMilliseconds), "ping")} per second each)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await MeasureAsync(new IPAddress?[] { gateway, PublicA, PublicB }, length, step, ct);
        state.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        state.Gateway = gateway == null ? null : results[0];
        state.InternetA = results[1];
        state.InternetB = results[2];
        state.ProbeErrors = errors;
        Activity.Report(ActivityKind.Analyze, "Calculated the median delay, jitter and packet loss of each target");
        return state;
    }

    /// <summary>Pings every non-null target once per <paramref name="step"/> for <paramref name="length"/>. Index i of the result belongs to targets[i].</summary>
    internal async Task<PingStats[]> MeasureAsync(IReadOnlyList<IPAddress?> targets, TimeSpan length, TimeSpan step, CancellationToken ct)
    {
        var count = Math.Max(1, (int)(length.TotalMilliseconds / step.TotalMilliseconds));
        var samples = targets.Select(_ => new PingSample[count]).ToArray();
        var pending = new List<Task>();
        using var timer = new PeriodicTimer(step);
        for (var i = 0; i < count; i++)
        {
            for (var t = 0; t < targets.Count; t++)
            {
                if (targets[t] is not { } ip) continue;
                var (ti, si) = (t, i);
                pending.Add(Task.Run(async () => samples[ti][si] = await _sender.SendAsync(ip, TimeoutMs, ct)));
            }
            if (i < count - 1) await timer.WaitForNextTickAsync(ct);
        }
        await Task.WhenAll(pending);
        return samples.Select(s => PingStats.From(s)).ToArray();
    }
}
