# AutoFix Scout — Change Log

## v2.0 (Implementation in progress) — 4 November 2026
**Status**: Base document for implementation; supersedes Architecture v1.0 and Database Schema v1.1.

### Changes from v1.0 / v1.1 (Section 20)
- **Target user**: Operator/helper roles separated; Simple mode with plain language.
- **Consent model**: Safety via risk tiers, snapshots, post-checks, auto-undo; consent is one layer.
- **Fix strategy**: Symptom-driven ladder; rollback and low-risk fixes before updates (no "download newer driver first").
- **AI role**: LLM router over fixed playbooks; no command generation.
- **Driver sources**: Windows Update and local store only in v1; vendor sources later via signed manifest.
- **Trust anchor**: Signature verification, not hash checks.
- **Rollback safety**: Driver snapshot as primary rollback; restore point best effort.
- **Fail closed**: No success defaults; nothing passes unless explicitly set.
- **Schema**: 5 tables, audit-focused, valid SQLite.
- **Stack**: C#/.NET 8 + WPF + SQLite only (no Python/Electron).
- **Platform**: Windows only (11 primary, 10 best-effort); ARM out of scope.
- **Scope**: 3 symptoms until pilot data says otherwise; milestones with stop gates.

### Implemented Components
#### Core Library (`AutoFixScout`)
- **Interfaces**: `IProbe` (read-only T0), `IExecutor` (elevated T1/T2)
- **ActionCatalog**: Compiled-in allowlist of 24 actions from spec section 7.1
- **SessionManager**: SQLite CRUD for sessions, steps, undo records, device snapshots, settings
- **Orchestrator**: Coordinates triage, playbook execution, optional LLM routing, helper report export
- **ProbesReal**: Real read-only probe implementation using WMI for system access (device/driver queries, service states)
- **ExecutorReal**: Elevated action executor using PowerShell/WMI for T1/T2 actions
- **Simulator**: Read-only probe implementation for unit testing (section 16)

#### UI (`AutoFixScout.UI`)
- **MainWindow.xaml**: Simple mode home screen with three large buttons + "Something else"
- **SettingsView.xaml**: Telemetry opt-in, LLM toggle, T2 approval settings
- **App.xaml.cs**: Application entry point with orchestrator initialization
- **Program.cs**: Portable launcher (EXE)

#### Tests (`AutoFixScout.Tests`)
- **Unit tests for Orchestrator**: Create session, triage audio/network, step logging
- **Simulator tests**: Unit-test playbooks against recorded device states

### Roadmap Milestones (Section 18)
| Milestone | Deliverable | Status |
|-----------|-------------|--------|
| 0: Foundations | Solution skeleton, probe/action interfaces, simulator, session log, playbook loader | ✅ In progress |
| 1: No-sound read-only | All audio probes + triage + helper report | ❌ Pending (Milestone 5) |
| 2: T1 actions + UI | Unmute, service restart, device toggle, test tone, Simple-mode screens | ⏳ Planned |
| 3: T2 safety | Executor process, snapshot/rollback, Windows Update driver, post-check, auto-undo | ⏳ Planned |
| 4: Camera & Wi‑Fi playbooks | YAML data + LLM routing (optional) | ⏳ Planned |
| 5: Packaging & pilot | Signed installer, opt-in telemetry, pilot with real users | ⏳ Planned |

### Next Steps
1. Install .NET 8 SDK → run `build.ps1 -Build` to compile and test.
2. Implement real probe actions (WMI, Core Audio, NAudio) in `Probes.cs`.
3. Add driver snapshot/rollback via `pnputil /export-driver`, `/add-driver`.
4. Create helper report exporter (plain-text/HTML with hardware IDs).
5. Pilot with 5–10 non-technical users; measure fix rate, harm rate, time-to-resolution.

### Open Questions to Settle Early (Section 19)
- Support Windows 10 or require Windows 11? → **Windows 11 primary** for v1.
- MSI vs MSIX installer? → Start with portable EXE; evaluate Store later.
- Code-signing route and cost? → Plan now; sign at Milestone 5.
- Remote helper mode vs Quick Assist integration? → Build helper report first.
- Free, portfolio-only, or paid subscription? → Undecided; affects signing/backend.

---

*See [README.md](../README.md) for full overview and [docs/API.md](../docs/API.md) for public interfaces.*
