using AutoFixScout.Probes;

namespace AutoFixScout.Stress;

/// <summary>What one reading returned. Null = not readable.</summary>
internal sealed record StressSnapshot(double? CpuLimit, double? CpuPerformance, bool? OnBattery, NvidiaReading? Gpu);

internal interface IStressReader : IDisposable
{
    bool HasBattery { get; }
    /// <summary>The card reading before the load starts; null when the card cannot report its temperature.</summary>
    Task<NvidiaReading?> ProbeGpuAsync();
    Task<StressSnapshot> ReadAsync();
}

internal interface ILoad : IDisposable
{
    void Start();
    /// <summary>Stops the load and waits up to the given time. Returns true when everything has ended.</summary>
    bool Stop(TimeSpan wait);
}

internal interface IGpuLoad : ILoad
{
    string? AdapterName { get; }
    bool TryInitialize(out string? reason);
}

/// <summary>
/// The real readings: processor counters (PDH, same counters as the performance check, collected once a second from one
/// open query) and one nvidia-smi call per reading (about 100 to 300 ms, run on a pool thread, never on a load thread).
/// </summary>
internal sealed class RealStressReader : IStressReader
{
    private IntPtr _query, _limit, _perf;
    private readonly string? _smi = RealGraphicsProbe.FindNvidiaSmi();
    private string? _prefix;

    public RealStressReader()
    {
        if (RealPerfProbe.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        if (RealPerfProbe.PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Performance Limit", IntPtr.Zero, out _limit) != 0) _limit = IntPtr.Zero;
        if (RealPerfProbe.PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _perf) != 0) _perf = IntPtr.Zero;
        RealPerfProbe.PdhCollectQueryData(_query);
    }

    public bool HasBattery => RealPerfProbe.ReadPower().HasBattery;

    public async Task<NvidiaReading?> ProbeGpuAsync()
    {
        if (_smi == null) return null;
        var (rows, prefix) = await Task.Run(() => RealGraphicsProbe.QueryNvidia(_smi));
        _prefix = prefix;
        return rows.FirstOrDefault(r => r.TemperatureC != null);
    }

    private double? Value(IntPtr counter) =>
        counter != IntPtr.Zero && RealPerfProbe.PdhGetFormattedCounterValue(counter, RealPerfProbe.PdhFmtDouble, out _, out var v) == 0 && v.Status == 0
            ? Math.Round(v.Double, 1) : null;

    public async Task<StressSnapshot> ReadAsync()
    {
        Task<NvidiaReading?> gpu = _smi == null || _prefix == null
            ? Task.FromResult<NvidiaReading?>(null)
            : Task.Run(() => RealGraphicsProbe.QueryNvidia(_smi, _prefix).Rows.FirstOrDefault());
        double? limit = null, perf = null;
        if (_query != IntPtr.Zero && RealPerfProbe.PdhCollectQueryData(_query) == 0) { limit = Value(_limit); perf = Value(_perf); }
        var battery = RealPerfProbe.ReadPower().OnBattery;
        return new StressSnapshot(limit, perf, battery, await gpu);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { RealPerfProbe.PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
