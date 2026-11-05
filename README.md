# AutoFix Scout v1.0 — Design Specification Implementation

## Overview
AutoFix Scout is a Windows diagnostic tool that diagnoses common hardware issues (no sound, camera not working, Wi‑Fi down) and guides non-technical users through vetted, reversible fixes in plain language.

**Key constraints:**
- Targets **Windows 11 x64 only** for v1 (Windows 10 is best-effort per spec).
- Works **offline**; cloud LLM and telemetry are optional opt-ins.
- Uses **risk tiers (T0–T3)** with snapshots, auto-undo, and fail-closed design.
- Simple mode: three large buttons + "Something else" for typed/voice input.

## Quick Start

### 1. Install Prerequisites
```powershell
# Install .NET 8 SDK from https://aka.ms/dotnet-download
dotnet --version  # Should show 8.x.x
```

### 2. Build (Developer)
```powershell
cd "C:/my folder/projects/new project/AutoFix Scout"
.\build.ps1 -Build
```

### 3. Run (Portable)
- After `-Publish`, run `bin\Release\net8.0-windows\win-x64\publish\AutoFixScout.exe`
- Or build a self-contained EXE with WiX/MSIX later (see Packaging section).

## Architecture Summary
- **UI process**: WPF app with large, accessible buttons (simple mode home screen).
- **Orchestrator**: coordinates read-only probes → playbook engine → optional LLM router.
- **Executor**: elevated separate process for T1/T2 actions (driver snapshots, service restarts).
- **Database**: SQLite session log (`session.db`) — stores sessions, steps, undo records, device snapshots, settings.

## Core Modules

### 1. Probes (T0)
Read-only diagnostic probes implemented in `Probes.cs`:
- `probe.audio_state` — default output device, mute/volume, endpoint type
- `probe.camera_privacy` — consent store + apps using camera
- `probe.network` — adapter state, airplane mode, gateway ping, DNS test
- `probe.update_history` — Windows Update history (driver changes last 30 days)

### 2. Orchestrator (`Orchestrator.cs`)
- Routes free-text input to symptom class (optional LLM or heuristic fallback).
- Executes playbook YAML steps from compiled-in allowlist (`ActionCatalog`).
- Logs each step to SQLite; supports undo records for T2 actions.

### 3. UI (`MainWindow.xaml` + `App.xaml`)
- Three large buttons: sound/camera/online + "Something else".
- Shows one finding → one proposed fix ("Try to fix it" button).
- Helper details view (hardware IDs, driver versions) behind "Details for my helper".
- Settings panel for telemetry opt-in and T2 approval requirements.

## Playbooks (YAML Data)
Each symptom has a versioned YAML playbook (sections 8.1–8.3):
- **No sound**: unmute → restart audio service → device toggle → driver rollback → Windows Update driver
- **Camera not working**: privacy switch → close app using camera → device toggle → driver rollback
- **Wi‑Fi down**: airplane mode toggle → DNS flush → adapter toggle → router reboot hint

## Risk Tiers (Safety)
| Tier | Examples | Gate |
|------|----------|------|
| T0 | Probes, test tones, report export | None |
| T1 | Unmute, service restart, disable/enable device, DNS flush | One tap ("Yes, try it") |
| T2 | Driver rollback/install from Windows Update | Snapshot + helper approval or "I understand" |

See spec section 6 for hard invariants (no file deletion in user folders; no registry writes outside documented keys).

## Testing Strategy
- Unit test each playbook against simulator implementation.
- Fault injection: mute audio, stop audio service, disable device, install older driver → verify ladder rung + undo.
- Pilot with 5–10 non-technical users before public release (section 16).

## Build Scripts
```powershell
.\build.ps1 -Build       # Compile solution
.\build.ps1 -Publish     # Portable publish to bin\Release\net8.0-windows\win-x64\publish
.\build.ps1 -Pack        # Package as MSI (requires WiX Toolset)
.\build.ps1 -Test        # Run unit tests
```

## Signing & Distribution
- Code signing is mandatory for SmartScreen trust (section 14).
- Placeholder signing script in `build.ps1` — replace with your certificate path once purchased.
- Packaging: self-contained EXE → MSI/MSIX later; see WiX Toolset or Visual Studio publishing guides.

## Privacy & Legal
- Diagnostics stay on the machine by default.
- Telemetry and LLM are opt-in via Settings panel.
- Redact PII before any cloud transmission (section 9).
- Review against applicable laws (e.g., India DPDP Act, GDPR) before public release.

## Next Steps
1. **Install .NET 8 SDK** → run `build.ps1 -Build` to compile and test.
2. **Add driver snapshot/rollback** — `pnputil /export-driver`, `/add-driver` via Executor process (T2).
3. **Create helper report exporter** — plain-text/HTML with hardware IDs, driver versions for remote helpers (section 4.3).
4. **Pilot** with 5–10 non-technical users; measure fix rate and harm rate before public release (milestone 6).

### Known Limitations (v1)
- Probe implementations use WMI/PowerShell which may have limited support on Windows 10 (Windows 11 primary target).
- Real probe actions will replace simulator stubs once unit tests pass.

## References
- Full design: `AutoFix Scout Design Specification v2.0.docx` (read in session).
- See spec sections 3–6 for scope, experience, architecture, and safety invariants.

---

**Note**: This implementation uses the C#/.NET 8 stack recommended by section 5 of the spec. Once .NET SDK is available on your machine, run `build.ps1 -Build` to compile and test.
