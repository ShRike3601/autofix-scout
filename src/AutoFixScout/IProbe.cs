namespace AutoFixScout;

/// <summary>
/// Read-only probe actions (T0, no admin).
/// </summary>
public interface IProbe
{
    Task<ServiceState> ProbeServiceStateAsync();
    Task<IEnumerable<DeviceInfo>> ProbeDevicesAsync();
    Task<DriverInfo> ProbeDriversAsync(string hardwareId);
    Task<AudioState> ProbeAudioStateAsync();
    Task<CameraPrivacyState> ProbeCameraPrivacyAsync();
    Task<NetworkState> ProbeNetworkAsync();
    Task<UpdateHistory> ProbeUpdateHistoryAsync();
}
