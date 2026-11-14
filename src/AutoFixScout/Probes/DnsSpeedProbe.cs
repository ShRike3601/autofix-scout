using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AutoFixScout.Probes;

/// <summary>Result of one name lookup: whether a valid answer came, and the time in milliseconds.</summary>
public readonly record struct DnsSample(bool Ok, double Ms);

/// <summary>Numbers for one DNS server (or the group of the configured ones). Never an address.</summary>
public sealed record DnsStats(int Sent, int Answered, double? MedianMs)
{
    public double FailurePercent => Sent == 0 ? 0 : 100.0 * (Sent - Answered) / Sent;

    public static DnsStats From(IReadOnlyList<DnsSample> samples)
    {
        var ok = samples.Where(s => s.Ok).Select(s => s.Ms).ToList();
        return new DnsStats(samples.Count, ok.Count, ConnectionStatistics.Median(ok));
    }
}

/// <summary>probe.dns_speed: numbers only. Null parts mean "not measured".</summary>
public sealed class DnsMeasurement
{
    /// <summary>The servers the adapter is configured with; null when none was found.</summary>
    public DnsStats? Current { get; set; }
    public DnsStats? PublicA { get; set; }
    public DnsStats? PublicB { get; set; }
    public int Names { get; set; }
}

/// <summary>Sends one DNS lookup. A seam so the measurement can be tested without a network.</summary>
public interface IDnsQuerySender { Task<DnsSample> QueryAsync(IPAddress server, string name, int timeoutMs, CancellationToken ct); }

/// <summary>A raw UDP query to port 53. Read-only: a lookup of a public name changes nothing on this computer.</summary>
public sealed class RealDnsQuerySender : IDnsQuerySender
{
    public async Task<DnsSample> QueryAsync(IPAddress server, string name, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var id = (ushort)Random.Shared.Next(0, 65536);
            var query = DnsQuery.Build(id, name);
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server, 53); // only datagrams from this server are received
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await udp.SendAsync(query, timeout.Token);
            while (true)
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                var reply = DnsQuery.Parse(r.Buffer, id);
                if (reply.Status == DnsReplyStatus.WrongId) continue; // not ours: keep waiting
                return new DnsSample(reply.Answered, sw.Elapsed.TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new DnsSample(false, 0); } // timeout
        catch (SocketException) { return new DnsSample(false, 0); }
        catch (ObjectDisposedException) { return new DnsSample(false, 0); }
    }
}

/// <summary>
/// Measures how long name lookups take: 6 common names, each asked twice, to the configured DNS servers and to two
/// public ones, all servers at the same time. Read-only: it sends DNS questions only and never changes a setting.
/// </summary>
public sealed class DnsSpeedProbe
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "www.microsoft.com", "www.google.com", "www.wikipedia.org", "www.youtube.com", "www.amazon.com", "www.bbc.co.uk",
    };
    public const int RoundsPerName = 2;
    public const int TimeoutMs = 2000;
    /// <summary>A server that answered none of its first queries is not asked further; the rest count as failed.</summary>
    public const int GiveUpAfterFailures = 4;

    private readonly IDnsQuerySender _sender;
    public DnsSpeedProbe(IDnsQuerySender? sender = null) => _sender = sender ?? new RealDnsQuerySender();

    /// <summary>The IPv4 DNS servers of the adapter that has the default gateway (else of any connected adapter). Empty when none.</summary>
    public static IReadOnlyList<IPAddress> ConfiguredServers()
    {
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => n.GetIPProperties()).ToList();
        var chosen = up.FirstOrDefault(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            ?? up.FirstOrDefault(p => p.DnsAddresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork));
        return chosen == null ? Array.Empty<IPAddress>()
            : chosen.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().ToList();
    }

    public async Task<DnsMeasurement> MeasureAsync(IReadOnlyList<IPAddress> configured, CancellationToken ct = default)
    {
        var servers = new List<IPAddress>(configured) { RealConnectionQualityProbe.PublicA, RealConnectionQualityProbe.PublicB };
        var n = configured.Count;
        Activity.Report(ActivityKind.Network,
            $"Measuring name lookups: {Activity.Count(Names.Count, "name")}, {Activity.Count(servers.Count, "server")} (no addresses are kept)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var all = await Task.WhenAll(servers.Select(s => Task.Run(() => AskAsync(s, ct), ct)));
        var m = new DnsMeasurement { Names = Names.Count };
        if (n > 0) m.Current = DnsStats.From(all.Take(n).SelectMany(x => x).ToList());
        m.PublicA = DnsStats.From(all[n]);
        m.PublicB = DnsStats.From(all[n + 1]);
        Activity.Report(ActivityKind.Analyze, "Calculated the median lookup time and the failures of each DNS server", sw.Elapsed);
        return m;
    }

    private async Task<List<DnsSample>> AskAsync(IPAddress server, CancellationToken ct)
    {
        var samples = new List<DnsSample>();
        var failedInARow = 0;
        var answered = false;
        for (var round = 0; round < RoundsPerName; round++)
            foreach (var name in Names)
            {
                ct.ThrowIfCancellationRequested();
                if (!answered && failedInARow >= GiveUpAfterFailures) { samples.Add(new DnsSample(false, 0)); continue; }
                var s = await _sender.QueryAsync(server, name, TimeoutMs, ct);
                samples.Add(s);
                if (s.Ok) answered = true; else failedInARow++;
            }
        return samples;
    }
}
