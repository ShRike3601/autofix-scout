using System.Numerics;

namespace AutoFixScout.Stress;

/// <summary>
/// Processor load: one thread per logical core at BelowNormal priority (the computer stays usable), tight vector
/// floating-point and integer work, no allocations. Every thread watches a stop flag and the token about every millisecond.
/// </summary>
internal sealed class CpuLoad : ILoad
{
    private readonly Thread[] _threads;
    private volatile bool _stop;
    private readonly CancellationToken _token;
    private static double _sink;

    public CpuLoad(int threads, CancellationToken token = default)
    {
        _token = token;
        _threads = new Thread[Math.Max(1, threads)];
    }

    public int Threads => _threads.Length;
    public int AliveThreads => _threads.Count(t => t is { IsAlive: true });

    public void Start()
    {
        for (var i = 0; i < _threads.Length; i++)
        {
            var seed = i + 1;
            _threads[i] = new Thread(() => Work(seed)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "AutoFixScout stress " + i };
            _threads[i].Start();
        }
    }

    private void Work(int seed)
    {
        var a = new Vector<double>(1.0 + seed * 1e-3);
        var b = new Vector<double>(1.0000001);
        var c = new Vector<double>(1e-9);
        var d = new Vector<double>(0.5);
        ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL;
        while (!_stop && !_token.IsCancellationRequested)
        {
            // About one millisecond of work per chunk.
            for (var i = 0; i < 4000; i++)
            {
                a = a * b + c;
                d = d * b + a * c;
                a = Vector.Min(a, new Vector<double>(4.0));
                d = Vector.Min(d, new Vector<double>(4.0));
                h = h * 6364136223846793005UL + 1442695040888963407UL;
                h ^= h >> 29;
                h *= 0xBF58476D1CE4E5B9UL;
            }
        }
        _sink = Vector.Dot(a, d) + h;
    }

    /// <summary>Asks every thread to end and waits for them. Returns true when all ended within the time.</summary>
    public bool Stop(TimeSpan wait)
    {
        _stop = true;
        var deadline = DateTime.UtcNow + wait;
        foreach (var t in _threads)
        {
            if (t == null) continue;
            var left = deadline - DateTime.UtcNow;
            if (!t.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero)) return false;
        }
        return true;
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(2));
}
