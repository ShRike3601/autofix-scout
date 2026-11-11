# Handoff: state of AutoFix Scout (2026-11-11)

Written so a new Claude Code session (e.g. on the test laptop) can continue without the earlier conversation.

## What it is

Windows app (C# / .NET 8 / WPF) for non-technical people: three big buttons (no sound, camera not working,
can't get online). It runs read-only checks, explains the problem in one sentence, then offers fixes one at a
time, least risky first, each followed by a real test ("Did you hear the beep?"). Ends as Fixed, Not fixed
(helper report) or Probably hardware.

The source of truth is the owner's *AutoFix Scout Design Specification v2.0* (.docx, not in this repo). Key rules
from it that the code follows:

- Act on symptoms, never "a newer driver exists". Least risky fix first. Fail closed: unknown never means "passed".
- UI and logic never run elevated. Only `AutoFixScout.Executor` does, and it accepts only allowlisted action ids
  plus validated arguments (never commands, paths or URLs).
- Risk tiers: T0 read-only, T1 reversible, T2 driver change (save point, post-check, auto-undo, max 3 per session,
  explicit "I understand"), T3 not in v1. Only sound, camera and network devices (MEDIA, Camera, Image, Net classes).

## Where things are

| Area | Files |
|---|---|
| Probes (read-only) | `src/AutoFixScout/Probes/Real{Audio,Network,Camera}Probe.cs` |
| Triage + fix ladders | `src/AutoFixScout/Triage/{AudioTriage,AudioLadder,NetworkTriage,CameraTriage}.cs` (`LadderRunner`, `DriverRules` live in AudioLadder.cs) |
| Non-admin actions, save point markers | `src/AutoFixScout/FixActions.cs` |
| Driver store logic (shared with executor) | `src/AutoFixScout/Drivers/DriverStore.cs`, `DriverStatus.cs` |
| Elevated executor | `src/AutoFixScout.Executor/{Program,SavePoints,WindowsUpdateDrivers}.cs` |
| Session log (SQLite, spec schema) | `src/AutoFixScout/SessionLog.cs` -> `%LOCALAPPDATA%\AutoFixScout\session.db` |
| Helper report | `src/AutoFixScout/HelperReport.cs` -> `%LOCALAPPDATA%\AutoFixScout\reports\` |
| UI (screens + flow) | `src/AutoFixScout.UI/MainWindow.xaml(.cs)` |
| Tests (68, all pass in CI) | `src/AutoFixScout.Tests/` |

Unused leftovers from the first skeleton (safe to delete, not yet removed): `Orchestrator.cs`, `SessionManager.cs`,
`Simulator.cs`, `ActionCatalog.cs`, `ActionContext.cs` (both projects), `IProbe.cs`, `IExecutor.cs`, `Probes.cs`,
`StepResult.cs`, UI `IActionContext.cs`.

## Done

- Milestone 1-2: sound check + ladder (wrong output -> unmute -> restart audio services -> toggle device), screens.
- Milestone 4: network (airplane, enable adapter, pick Wi-Fi, flush DNS, toggle adapter, router restart; honest
  router/provider endings) and camera (enable, privacy switches, app holding camera, shutter/F-key, toggle).
- Session log + helper report.
- Milestone 3: T2 driver changes with save points:
  - `driver.rollback`: newest older oem*.inf in the driver store that fits the device (same class, names its
    hardware ID). Offered only if the current driver arrived in the last 30 days.
  - `driver.install_from_windows_update`: WUA COM search Type='Driver', matched by hardware ID. Offered only for a
    device still reporting an error. Never accepts a EULA.
  - Save point = `pnputil /export-driver` copy + manifest in `C:\ProgramData\AutoFixScout\savepoints\<id>` (admin-only
    ACL, manifest must be admin-owned) + best-effort restore point. Post-check: present, error code 0, expected version.
    Any failure -> automatic restore. A "No" on the real test -> restore.
  - The app writes a marker per open save point in `%LOCALAPPDATA%\AutoFixScout\pending-savepoints`; it's removed
    when the person confirms the fix. A marker found at startup = unfinished change -> app offers to restore.
  - Executor exit codes: 0 ok, 1 failed (nothing changed), 2 rejected input, 3 nothing to do, 4 changed but undone,
    6 restore failed (save point kept).

## Never tested on real hardware

Everything that touches Windows has only been compiled, not run: all probes, `UpdateDriverForPlugAndPlayDevices`,
`pnputil /export-driver`, the save point ACL/owner checks, restore point creation, the WUA calls, the UI flow.
That is the next job, on the test laptop.

## Test plan (only on AFS-TESTBENCH)

1. Run the app (CI artifact or `dotnet run --project src/AutoFixScout.UI`). Try each button on a healthy machine:
   findings should be sensible, nothing changed.
2. Sound: mute; stop Windows Audio service (admin); disable the sound device; switch default output to HDMI/BT.
   Each should be found and fixed by the right rung.
3. Network: airplane mode on; disable Wi-Fi adapter; disconnect Wi-Fi; set a bogus DNS server.
4. Camera: privacy switches off (each of the three); open the Camera app then run the check; disable the device.
5. Rollback: install an older official audio driver, then let Windows Update install the newer one.
   - Answer Yes to the beep: older driver stays (Device Manager -> Driver tab).
   - Answer No: newer driver comes back.
   - Kill the app during "Making a save point...", reopen: it should offer to put the driver back.
   - As a standard user, confirm you can read but not write `C:\ProgramData\AutoFixScout\savepoints`.
6. Windows Update driver: make the sound device report an error, check the WU rung finds, installs and undoes.
7. Check `session.db` rows and the helper report after each run.

The executor can also be run directly from an admin terminal, e.g.
`AutoFixScout.Executor.exe driver.rollback "<instance id>" <32-hex save point id>` then `echo $LASTEXITCODE`.
UAC prompts can't be clicked by automation; run the app from an elevated terminal on the test laptop to avoid them.

## Shortcuts taken (search the code for `shortcut:`)

Ladders hardcoded in C# instead of YAML playbooks; one UAC prompt per admin action (no long-lived executor);
airplane mode read from an undocumented registry value; "driver changed recently" = package staged date, not WU
history; camera test uses the Windows Camera app; session written once at the end of a flow.

## Next (owner's choice)

Real-hardware testing first. Then: Settings screen (opt-ins, delete my data), "Something else" free-text routing
(spec 9, LLM), packaging + code signing (Milestone 5), deleting the unused skeleton files.
