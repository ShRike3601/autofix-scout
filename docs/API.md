# AutoFix Scout Public API Reference

## Overview
This document describes the public interfaces in `AutoFixScout` library: `IProbe`, `IExecutor`, `ActionCatalog`, and data models for probes, actions, and session logging.

## Interfaces

### IProbe
Read-only probe actions (T0, no admin). All methods are async and return model objects.

```csharp
namespace AutoFixScout;

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
```

### IExecutor
Elevated executor actions (T1/T2, admin). Accepts `ActionContext` with undo info.

```csharp
public interface IExecutor
{
    Task<AudioState> UnmuteAndSetVolumeAsync(string endpointId, int volume);
    Task RestartServiceAsync(string serviceName);
    Task DisableEnableDeviceAsync(string instanceId);
    Task FlushDnsAsync();
    Task ToggleAdapterAsync(string adapterName);
    Task SnapshotDriverAsync(string hardwareId);
    Task RollbackDriverAsync(string hardwareId, string previousInfPath);
    Task InstallDriverFromWindowsUpdateAsync(string hardwareId, string hardwareIds);
}
```

## Models (Section 7.1 + Section 12)

### Probes
- `ServiceState`: `{ Name, Running }`
- `DeviceInfo`: `{ Name, DeviceID, PNPClass, ConfigManagerErrorCode }`
- `DriverInfo`: `{ Name, Version, Provider, INF, HardwareID }`
- `AudioState`: `{ DefaultOutputDevice, Muted, Volume, EndpointType, ActiveEndpoints }`
- `CameraPrivacyState`: `{ CameraAccessAllowed, AppsCurrentlyUsingCamera[] }`
- `NetworkState`: `{ AdapterName, AirplaneMode, Enabled, GatewayReachable, DnsResolving }`
- `UpdateHistory`: `{ DriverUpdatesLast30Days[] }`

### Executor (T1/T2)
Returns model objects after action:
- T1: same input model with success flag + undo payload if applicable.
- T2: snapshot created first → rollback/install → post-check test.

## ActionCatalog (Section 7)
Compiled-in action allowlist — never accepts raw commands/scripts.

```csharp
public static class ActionCatalog
{
    // T0 probes
    public static readonly (string id, string name, Func<..., Task<object>>) probe_service_state = ...;
    public static readonly (string id, ...) probe_devices = ...;
    // ... all 24 actions from spec section 7.1

    // T1 actions
    public static readonly (string id, ..., UndoInfo?) audio_unmute_and_set_volume = ...;
    // ... device.disable_enable, network.flush_dns, network.toggle_adapter

    // T2 actions
    public static readonly (string id, ..., UndoInfo?) driver_snapshot = ...;
    public static readonly (string id, ..., UndoInfo?) driver_rollback_to_previous = ...;
}
```

Each action has:
- `id`: string identifier from spec section 7.1 table.
- `name`: plain-language description.
- `admin`: whether elevated privileges needed.
- `undo`: payload and method to revert the action (if reversible).

## SessionManager (Section 12)
SQLite session log with 5 tables: `session`, `step_log`, `undo_record`, `device_snapshot`, `setting`.

```csharp
public sealed class SessionManager
{
    public SessionManager(string dbPath = "session.db");

    public Task InitializeAsync();  // create tables + default settings
    public Task<Session?> CreateSessionAsync(string symptom, string appVersion, string playbookVersion);
    public Task<Session?> GetSessionAsync(string sessionId);
    public Task<StepLog?> CreateStepAsync(...);
    public Task CompleteStepAsync(int stepId, StepResult result, string? error = null);
    public Task<UndoRecord?> CreateUndoAsync(...);
    public Task ApplyUndoAsync(int undoId);
    public Task<IEnumerable<Setting>> GetSettingsAsync();
    public Task SetSettingAsync(string key, string value);
    public Task<DeviceSnapshot?> SnapshotDriverAsync(...);
}
```

## Orchestrator (Section 5)
Coordinates triage, playbook execution, session logging, and optional LLM routing.

```csharp
public sealed class Orchestrator
{
    public Orchestrator(IProbe probe, IExecutor? executor = null, string dbPath = "session.db",
        bool llmEnabled = false, Func<string, Symptom>? llmRoute = null);

    // Triage (read-only)
    public Task<TriagedResult> TriageAudioAsync();
    public Task<TriagedResult> TriageCameraAsync();
    public Task<TriagedResult> TriageNetworkAsync();

    // Playbook loading + fix proposal
    public Task<FixProposal> ProposeNextFixAsync(string sessionId, Symptom symptom);
    public Task<FixResult> ApplyFixAsync(string sessionId, string actionId, Dictionary<string, object>? parameters);

    // Helper report (section 4.3)
    public Task<HelperReport> ExportReportAsync(string sessionId);
}
```

## Risk Tiers
- `T0`: read-only, no admin gate.
- `T1`: reversible state change; one tap + plain description required.
- `T2`: driver changes; mandatory snapshot + undo; helper approval or "I understand" step.

See spec section 6 for hard invariants and safety rules.
