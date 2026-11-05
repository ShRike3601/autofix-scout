namespace AutoFixScout;

/// <summary>
/// Elevated executor actions (T1/T2, admin).
/// </summary>
public interface IExecutor
{
    Task<AudioState> UnmuteAndSetVolumeAsync(string endpointId, int volume);
    Task RestartServiceAsync(string serviceName);
    Task DisableEnableDeviceAsync(string instanceId);
    Task FlushDnsAsync();
    Task ToggleAdapterAsync(string adapterName);
    Task<string?> SnapshotDriverAsync(string hardwareId);
    Task RollbackDriverAsync(string hardwareId, string previousINFPath);
    Task InstallDriverFromWindowsUpdateAsync(string hardwareId, string hardwareIds);
}
