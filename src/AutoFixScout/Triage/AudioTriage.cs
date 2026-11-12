using AutoFixScout.Probes;

namespace AutoFixScout.Triage;

/// <summary>
/// Evaluates an audio state and returns a list of findings, ordered from most severe to least.
/// </summary>
public static partial class AudioTriage
{
    /// <summary>
    /// Calm, human-friendly message for each finding code.
    /// </summary>
    private static readonly Dictionary<FindingCode, string> _plainMessages = new()
    {
        [FindingCode.ProbeIncomplete] = "AutoFix Scout could not complete the sound check.",
        [FindingCode.AudioServiceStopped] = "The Windows sound service (\"Windows Audio\") is not running.",
        [FindingCode.EndpointBuilderStopped] = "The Windows service that manages sound devices (\"Windows Audio Endpoint Builder\") is not running.",
        [FindingCode.NoOutputEndpoint] = "Windows does not show any device that can play sound.",
        [FindingCode.NoActiveOutput] = "Sound devices are installed, but none of them is currently available to play sound.",
        [FindingCode.DefaultIsHdmiOrBluetooth] = "Your computer is sending sound to an HDMI or Bluetooth device instead of your speakers.",
        [FindingCode.Muted] = "The sound output is muted.",
        [FindingCode.VolumeVeryLow] = "The volume is set very low.",
        [FindingCode.DeviceError] = "A sound device reports a problem.",
        [FindingCode.LooksNormal] = "No problem was found with your sound settings.",
    };

    public static IReadOnlyList<Finding> Evaluate(AudioState state)
    {
        var findings = new List<Finding>();

        // Rule 1: Any ProbeErrors -> ProbeIncomplete (highest priority)
        if (state.ProbeErrors.Count > 0)
        {
            findings.Add(new Finding(
                FindingCode.ProbeIncomplete,
                _plainMessages[FindingCode.ProbeIncomplete],
                string.Join("; ", state.ProbeErrors)));
            return findings.AsReadOnly();
        }

        // Rule 2: service AudioSrv not Running -> AudioServiceStopped
        var audioSvc = state.Services.FirstOrDefault(s => s.Name == "AudioSrv" || s.Name == "audio");
        if (audioSvc != null && audioSvc.Status == "Stopped")
        {
            findings.Add(new Finding(
                FindingCode.AudioServiceStopped,
                _plainMessages[FindingCode.AudioServiceStopped],
                $"Service 'AudioSrv' status: {audioSvc.Status}"));
        }

        // Rule 3: AudioEndpointBuilder not Running -> EndpointBuilderStopped
        var endpointBuilder = state.Services.FirstOrDefault(s => s.Name.Contains("Endpoint") || s.Name.Contains("Builder"));
        if (endpointBuilder != null && endpointBuilder.Status == "Stopped")
        {
            findings.Add(new Finding(
                FindingCode.EndpointBuilderStopped,
                _plainMessages[FindingCode.EndpointBuilderStopped],
                $"Service '{endpointBuilder.Name}' status: {endpointBuilder.Status}"));
        }

        // Rule 4: no endpoints at all -> NoOutputEndpoint
        if (state.Endpoints.Count == 0)
        {
            findings.Add(new Finding(
                FindingCode.NoOutputEndpoint,
                _plainMessages[FindingCode.NoOutputEndpoint],
                "Endpoints list is empty"));
        }

        // Rule 5: endpoints exist but none active -> NoActiveOutput
        else if (state.Endpoints.Count > 0 && !state.Endpoints.Any(e => e.IsActive))
        {
            findings.Add(new Finding(
                FindingCode.NoActiveOutput,
                _plainMessages[FindingCode.NoActiveOutput],
                "No endpoints marked as IsActive=true"));
        }

        // Rule 6: default endpoint KindGuess is Hdmi or Bluetooth and a Speakers or Headphones endpoint is also active -> DefaultIsHdmiOrBluetooth
        var actualDefault = state.Endpoints.FirstOrDefault(e => e.IsDefault);
        if (actualDefault != null && (actualDefault.KindGuess == "Hdmi" || actualDefault.KindGuess == "Bluetooth"))
        {
            var speaker = state.Endpoints.FirstOrDefault(e =>
                !e.IsDefault && e.IsActive && (e.KindGuess == "Speakers" || e.KindGuess == "Headphones"));
            if (speaker != null)
            {
                findings.Add(new Finding(
                    FindingCode.DefaultIsHdmiOrBluetooth,
                    _plainMessages[FindingCode.DefaultIsHdmiOrBluetooth],
                    $"Default: '{actualDefault.Name}' ({actualDefault.KindGuess}); working alternative: '{speaker.Name}'"));
            }
        }

        // Rule 7: DefaultMuted == Yes -> Muted
        if (state.DefaultMuted == Tri.Yes)
        {
            findings.Add(new Finding(
                FindingCode.Muted,
                _plainMessages[FindingCode.Muted],
                $"DefaultMuted: {state.DefaultMuted}"));
        }

        // Rule 8: DefaultVolumePercent is not null and < 5 -> VolumeVeryLow
        if (state.DefaultVolumePercent.HasValue && state.DefaultVolumePercent.Value < 5)
        {
            findings.Add(new Finding(
                FindingCode.VolumeVeryLow,
                _plainMessages[FindingCode.VolumeVeryLow],
                $"DefaultVolumePercent: {state.DefaultVolumePercent}"));
        }

        // Rule 9: any device with ProblemCode not null and not 0 -> DeviceError
        foreach (var device in state.Devices)
        {
            if (device.ProblemCode.HasValue && device.ProblemCode.Value != 0)
            {
                findings.Add(new Finding(
                    FindingCode.DeviceError,
                    _plainMessages[FindingCode.DeviceError],
                    $"Device '{device.Name}' ProblemCode: {device.ProblemCode}"));
            }
        }

        // Rule 9b: per-app sound (Volume mixer), only when the device itself is fine so the device findings keep priority.
        if (findings.Count == 0)
        {
            foreach (var app in state.Sessions.Where(s => s.Muted).DistinctBy(s => s.Label))
                findings.Add(new Finding(FindingCode.AppSoundMuted,
                    $"Sound is muted for \"{app.Label}\" in the Volume mixer.",
                    $"Session '{app.Process}': muted, level {app.VolumePercent}%, {(app.Active ? "active" : "inactive")}"));
            foreach (var app in state.Sessions.Where(s => !s.Muted && s.VolumePercent < AppSound.LowPercent).DistinctBy(s => s.Label))
                findings.Add(new Finding(FindingCode.AppSoundVeryLow,
                    $"Sound is set very low for \"{app.Label}\" in the Volume mixer.",
                    $"Session '{app.Process}': level {app.VolumePercent}%, {(app.Active ? "active" : "inactive")}"));
        }

        // Rule 10: LooksNormal ONLY if no other findings AND no Unknown values
        var hasOtherFindings = findings.Count > 0;
        var hasUnknownMuteOrVolume = state.DefaultMuted == Tri.Unknown || (state.DefaultVolumePercent.HasValue && state.DefaultVolumePercent.Value == 0); // Treat 0 as potentially unknown/neutral

        if (!hasOtherFindings && !hasUnknownMuteOrVolume)
        {
            findings.Add(new Finding(
                FindingCode.LooksNormal,
                _plainMessages[FindingCode.LooksNormal],
                "No errors detected"));
        }

        // Sort by severity: ProbeIncomplete highest, then by code order, LooksNormal lowest
        var ordered = findings.OrderBy(f => f.Code).ToList();
        
        return ordered.AsReadOnly();
    }
}

public record Finding(FindingCode Code, string PlainMessage, string TechnicalDetail);

public enum FindingCode
{
    ProbeIncomplete,
    AudioServiceStopped,
    EndpointBuilderStopped,
    NoOutputEndpoint,
    NoActiveOutput,
    DefaultIsHdmiOrBluetooth,
    Muted,
    VolumeVeryLow,
    DeviceError,
    // network (spec 8.3)
    AirplaneModeOn,
    NoNetworkAdapter,
    NetAdapterDisabled,
    NetAdapterError,
    NotConnected,
    GatewayUnreachable,
    DnsFailing,
    NoInternet,
    // camera (spec 8.2)
    NoCamera,
    CameraDisabled,
    CameraError,
    CameraPrivacyOff,
    CameraInUse,
    LooksNormal,
    // storage
    StorageDeviceError,
    SystemDriveCritical,
    SystemDriveLow,
    LargeTempFiles,
    HighMemoryPressure,
    ManyStartupApps,
    LongUptime,
    HeavyProcessCpu,
    HeavyProcessMemory,
    StartupAppsOptional,
    // update
    UpdateServiceDisabled,
    UpdateServiceStopped,
    UpdateRebootPending,
    UpdateLowDiskSpace,
    UpdatesOutOfDate,
    // Microphone
    NoMicrophone,
    NoActiveMicrophone,
    MicDeviceError,
    MicPrivacyOff,
    MicWrongDefault,
    MicMuted,
    MicLevelVeryLow,
    // Health check (something else is wrong)
    HealthHardwareErrors,
    HealthDiskErrors,
    HealthClockWrong,
    HealthDeadProxy,
    HealthDeadSystemProxy,
    HealthHostsHijacked,
    HealthServiceOff,
    HealthPathDamaged,
    HealthTempMissing,
    HealthWinsockAddOn,
    HealthStoreAppsDamaged,
    HealthShellCrashing,
    HealthBlueScreens,
    HealthProgramCrashing,
    HealthRuntimeMissing,
    HealthServicesFailing,
    HealthUpdatesFailing,
    HealthRestartPending,
    // Bluetooth
    NoBluetoothAdapter,
    BluetoothAdapterDisabled,
    BluetoothAdapterError,
    BluetoothServiceStopped,
    BluetoothOff,
    BluetoothNothingPaired,
    BluetoothAudioNotConnected,
    // Start menu, taskbar, search
    ShellPackageDamaged,
    // Drive health
    HealthDriveFailing,
    HealthDriveWarning,
    // Printer
    PrintSpoolerStopped,
    NoPrinter,
    DefaultPrinterVirtual,
    PrinterOffline,
    PrinterPaused,
    PrinterError,
    PrintJobsStuck,
    // Display
    DisplayAdapterError,
    ExternalDisplayNotShown,
    BrightnessVeryLow,
    // Apps, Microsoft Store, Office, OneDrive
    StoreMissing,
    StorePackageDamaged,
    OfficeNotFound,
    OneDriveNotFound,
    OneDriveNotRunning,
    // Security and unwanted software
    SecurityNoAntivirus,
    SecurityRealTimeOff,
    SecuritySignaturesOld,
    SecurityFirewallOff,
    SecurityUacOff,
    SecuritySmartScreenOff,
    SecuritySuspiciousStartup,
    // Battery, sleep and power
    PowerBatteryReplace,
    PowerBatteryWorn,
    PowerSleepNeverOnBattery,
    PowerWakesOften,
    PowerFastStartupOn,
    PowerPlanHighOnBattery,
    // Keyboard, mouse and touchpad
    NoKeyboardFound,
    InputDeviceDisabled,
    InputDeviceError,
    TouchpadOff,
    StickyKeysOn,
    FilterKeysOn,
    ToggleKeysOn,
    MouseKeysOn,
    // Devices with a Device Manager warning, temporary profile
    HealthDeviceProblem,
    HealthTempProfile,
    // Network extras
    NetNoAddress,
    NetWeakWifi,
    NetIpConflict,
    NetMetered,
    // Big space users on a low or full system drive
    StorageBigUsers,
    // Per-app sound (Volume mixer)
    AppSoundMuted,
    AppSoundVeryLow,
    // Recent changes and graphics driver resets
    HealthRecentChanges,
    HealthGraphicsResets,
    // Security: browser policies
    SecurityBrowserPolicy,
    SecurityBrowserPolicyManaged,
    // Per-app privacy choices (Microphone and Camera)
    MicAppDenied,
    CameraAppDenied,
    // Scanners (Printer area)
    ScannerServiceOff,
    ScannerDeviceError,
    // Blue-screen causes and virtual memory
    HealthPageFileTooSmall,
    // Security: threats found by Microsoft Defender
    SecurityThreatActive,
    SecurityThreatsRemoved,
    // Programs will not install (Windows Installer)
    InstallerServiceOff,
    InstallFailures,
    InstallBlockedByRestart,
}
