using System.Diagnostics;
using Xunit;

namespace AutoFixScout.Tests;

/// <summary>
/// Live tests change and restore real settings (proxy, Run keys, accessibility flags...). [Collection] only serializes
/// them inside one test run; this machine-wide semaphore also keeps separate runs (other agents, CI on the bench) apart.
/// A semaphore, not a mutex: xUnit may dispose the fixture on another thread.
/// A semaphore is never released when its holder is killed, so the holder writes its process id to a file; a waiting run
/// that finds the holder gone takes the slot over instead of waiting until it times out (2026-11-14: a killed test host
/// made every later live test hang).
/// </summary>
public sealed class LiveMachineLock : IDisposable
{
    private readonly Semaphore? _gate;
    private static readonly string Holder = Path.Combine(Path.GetTempPath(), "AutoFixScout-LiveTests.holder");

    public LiveMachineLock()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        _gate = new Semaphore(1, 1, @"Local\AutoFixScout-LiveTests");
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!_gate.WaitOne(TimeSpan.FromSeconds(5)))
        {
            if (HolderGone()) break; // the previous holder died without releasing: its slot is ours
            if (DateTime.UtcNow > deadline) throw new TimeoutException("another live test run kept the machine for 15 minutes");
        }
        try { File.WriteAllText(Holder, Environment.ProcessId.ToString()); } catch (IOException) { }
    }

    /// <summary>True when the recorded holder process no longer exists (a missing or unreadable file counts as "still held").</summary>
    private static bool HolderGone()
    {
        try
        {
            if (!int.TryParse(File.ReadAllText(Holder).Trim(), out var pid)) return false;
            if (pid == Environment.ProcessId) return false;
            try { using var p = Process.GetProcessById(pid); return p.HasExited; }
            catch (ArgumentException) { return true; } // no such process
        }
        catch (IOException) { return false; }
    }

    public void Dispose()
    {
        try { if (File.Exists(Holder) && File.ReadAllText(Holder).Trim() == Environment.ProcessId.ToString()) File.Delete(Holder); }
        catch (IOException) { }
        try { _gate?.Release(); } catch (SemaphoreFullException) { }
        _gate?.Dispose();
    }
}

[CollectionDefinition("Live machine")]
public sealed class LiveMachineCollection : ICollectionFixture<LiveMachineLock> { }
