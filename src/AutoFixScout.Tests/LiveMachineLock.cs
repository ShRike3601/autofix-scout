using Xunit;

namespace AutoFixScout.Tests;

/// <summary>
/// Live tests change and restore real settings (proxy, Run keys, accessibility flags...). [Collection] only serializes
/// them inside one test run; this machine-wide semaphore also keeps separate runs (other agents, CI on the bench) apart.
/// A semaphore, not a mutex: xUnit may dispose the fixture on another thread.
/// </summary>
public sealed class LiveMachineLock : IDisposable
{
    private readonly Semaphore? _gate;

    public LiveMachineLock()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AFS_LIVE"))) return;
        _gate = new Semaphore(1, 1, @"Local\AutoFixScout-LiveTests");
        if (!_gate.WaitOne(TimeSpan.FromMinutes(15)))
            throw new TimeoutException("another live test run kept the machine for 15 minutes");
    }

    public void Dispose()
    {
        try { _gate?.Release(); } catch (SemaphoreFullException) { }
        _gate?.Dispose();
    }
}

[CollectionDefinition("Live machine")]
public sealed class LiveMachineCollection : ICollectionFixture<LiveMachineLock> { }
