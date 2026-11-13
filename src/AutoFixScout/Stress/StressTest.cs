using System.Diagnostics;

namespace AutoFixScout.Stress;

/// <summary>What the test uses to read and load. The real one is built by default; tests pass fakes.</summary>
internal sealed record StressEnvironment(
    Func<IStressReader> CreateReader,
    Func<int, CancellationToken, ILoad> CreateCpuLoad,
    Func<IGpuLoad?> CreateGpuLoad)
{
    public static StressEnvironment Real { get; } = new(
        () => new RealStressReader(),
        (n, ct) => new CpuLoad(n, ct),
        () => new D3D11GpuLoad());
}

/// <summary>
/// Opt-in short stress test: idle baseline, processor load, graphics card load, cool-down, one reading per second.
/// Safety: refuses on battery, ends on cancel, ends a load early when the card reaches 90 degrees Celsius or the processor
/// stays below half speed for 5 seconds, hard cap of 2 minutes, every load is stopped in finally. Changes no setting.
/// </summary>
public static class StressTest
{
    public static Task<StressResult> RunAsync(StressOptions options, IProgress<StressSample>? progress, CancellationToken ct) =>
        RunAsync(options, progress, ct, StressEnvironment.Real);

    internal static async Task<StressResult> RunAsync(StressOptions options, IProgress<StressSample>? progress, CancellationToken ct, StressEnvironment env)
    {
        var result = new StressResult { SampleIntervalSeconds = options.SampleIntervalSeconds };
        var total = Stopwatch.StartNew();
        IStressReader? reader = null;
        ILoad? cpu = null;
        IGpuLoad? gpu = null;
        ILoad? active = null;
        try
        {
            reader = env.CreateReader();
            result.HasBattery = reader.HasBattery;
            var first = await reader.ReadAsync().WaitAsync(ct);
            // Fail closed: on battery, or a laptop whose power state is unknown, the test does not start.
            if (first.OnBattery == true || (first.OnBattery == null && result.HasBattery))
            {
                result.Outcome = StressOutcome.RefusedOnBattery;
                return result;
            }

            // Decide about the graphics card load before the baseline, so the person is told early.
            var gpuProbe = await reader.ProbeGpuAsync().WaitAsync(ct);
            result.GpuName = gpuProbe?.Name;
            if (options.GpuSeconds > 0)
            {
                if (gpuProbe == null)
                {
                    result.GpuStatus = GpuLoadStatus.NoReadings;
                    result.GpuStatusDetail = "the graphics card cannot report its temperature";
                }
                else
                {
                    gpu = env.CreateGpuLoad();
                    string? why = null;
                    if (gpu == null || !gpu.TryInitialize(out why))
                    {
                        result.GpuStatus = GpuLoadStatus.Unavailable;
                        result.GpuStatusDetail = gpu == null ? "no graphics load is available" : why;
                    }
                }
            }

            var threads = options.CpuThreads > 0 ? options.CpuThreads : Environment.ProcessorCount;
            result.CpuThreads = threads;
            var phases = new List<(StressPhase Phase, double Seconds)> { (StressPhase.Idle, options.IdleSeconds), (StressPhase.Cpu, options.CpuSeconds) };
            if (result.GpuStatus == GpuLoadStatus.NotAttempted && gpu != null && options.GpuSeconds > 0) phases.Add((StressPhase.Gpu, options.GpuSeconds));
            phases.Add((StressPhase.Cooldown, options.CooldownSeconds));

            var stopped = false;
            foreach (var (phase, seconds) in phases)
            {
                if (seconds <= 0) continue;
                // After a safety stop only the cool-down readings are taken.
                if (stopped && phase is StressPhase.Cpu or StressPhase.Gpu) continue;
                ct.ThrowIfCancellationRequested();

                if (phase == StressPhase.Cpu) { active = cpu = env.CreateCpuLoad(threads, ct); cpu.Start(); }
                else if (phase == StressPhase.Gpu) { active = gpu!; gpu!.Start(); result.GpuStatus = GpuLoadStatus.Ran; }

                var phaseSamples = new List<StressSample>();
                var phaseWatch = Stopwatch.StartNew();
                for (var k = 0; k * options.SampleIntervalSeconds < seconds; k++)
                {
                    var due = TimeSpan.FromSeconds(k * options.SampleIntervalSeconds) - phaseWatch.Elapsed;
                    if (due > TimeSpan.Zero) await Task.Delay(due, ct);
                    ct.ThrowIfCancellationRequested();

                    var snap = await reader.ReadAsync().WaitAsync(ct);
                    var g = snap.Gpu;
                    var sample = new StressSample(phase, Math.Round(total.Elapsed.TotalSeconds, 2), Math.Round(phaseWatch.Elapsed.TotalSeconds, 2),
                        snap.CpuLimit, snap.CpuPerformance, snap.OnBattery,
                        g?.TemperatureC, g?.ClockGraphicsMhz, g?.ClockGraphicsMaxMhz, g?.PowerDrawW, g?.PowerLimitW,
                        g?.ThrottleReasons, g?.ThermalSlowdown ?? false, g?.PowerBrake ?? false);
                    phaseSamples.Add(sample);
                    result.Samples.Add(sample);
                    progress?.Report(sample);

                    var decision = StressSafety.Decide(phase, phaseSamples, total.Elapsed.TotalSeconds, options.MaxTotalSeconds);
                    if (decision != StressStop.None)
                    {
                        result.StopReason = decision;
                        result.StoppedIn = phase;
                        result.Outcome = StressOutcome.StoppedForSafety;
                        stopped = true;
                        break;
                    }
                }

                if (active != null) { active.Stop(TimeSpan.FromSeconds(2)); active.Dispose(); active = null; }
                if (stopped && result.StopReason is StressStop.TimeLimit or StressStop.Unplugged) break;
                if (stopped && phase == StressPhase.Cooldown) break;
            }
        }
        catch (OperationCanceledException)
        {
            result.Outcome = StressOutcome.Cancelled;
        }
        finally
        {
            // Loads first, then the readers and the card: nothing keeps running after this point.
            try { active?.Stop(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            try { cpu?.Dispose(); } catch (Exception) { }
            try { gpu?.Dispose(); } catch (Exception) { }
            try { reader?.Dispose(); } catch (Exception) { }
            result.TotalSeconds = Math.Round(total.Elapsed.TotalSeconds, 1);
        }
        return result;
    }
}
