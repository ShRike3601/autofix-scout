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
| Tests (77, all pass in CI) | `src/AutoFixScout.Tests/` |

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

## Real-hardware test results (AFS-TESTBENCH)

Laptop: MSI, Realtek audio (+ NVIDIA/Intel HDMI audio, Nahimic), Chicony "HD Webcam", Intel AX201 Wi-Fi, Realtek
Ethernet, Cloudflare WARP on (Wi-Fi DNS = 127.0.2.2/.3), Location services off, Windows 11 build 26300.

| Step | Status | Notes |
|---|---|---|
| 1. Healthy machine | **Passed after fix** (2026-11-11) | See below. Probes ran fine; no device/service/DNS/privacy/default-output change; WARP DNS not blamed. |
| 2. Sound faults | **Passed after fixes** (2026-11-11) | Muted -> unmute (no UAC). AudioSrv stopped -> restart_services (UAC). Realtek disabled (code 22) -> restart (no) then toggle_device (yes). Default = Bluetooth JBL Go 3 -> wrong_output after fix (see below). |
| 3. Network faults | **Passed after fixes** (2026-11-11, TestBench) | Wi-Fi disabled -> enable_adapter. Wi-Fi disconnected -> pick_wifi (internet back 7 s after reconnect). Bogus DNS (WARP paused) -> flush_dns (no) then new reset_dns (yes). Airplane mode **not run yet** (needs a person: no reliable way to switch it from a script). |
| 4. Camera faults | **Passed / changed** (2026-11-11, TestBench) | Each privacy switch off -> privacy rung, named correctly. Camera disabled -> enable_camera. "Camera app holding it" can't be detected on this Windows build (see below); ladder changed. Human check of the real Camera-app test still to do with the new UI. |
| 5. Rollback | not run | |
| 6. Windows Update driver | not run | |
| 7. session.db + report | checked for step 1 | |

Step 1 findings:
- **Fixed:** with "looks normal" findings the app still offered fixes before testing (sound offered and ran
  `restart_services`; camera asked about the shutter) and logged them as `fixed`; network logged `not_fixed`.
  Now `LadderRunner.NoFaultAsync` runs the real test first when every finding is LooksNormal; if it works the flow
  ends with outcome `NoFaultFound` and offers nothing. Owner decision: new session outcome `no_fault_found`
  (spec 12 schema extended; `SessionLog` migrates older session.db files by rebuilding the session table).
- **Not a bug, test-setup trap:** an app launched from the Claude desktop app (an MSIX-packaged app) inherits its
  file-system virtualization, so `%LOCALAPPDATA%\AutoFixScout` is silently redirected to
  `%LOCALAPPDATA%\Packages\anthropicpbc.claude_*\LocalCache\Local\AutoFixScout` and Notepad shows "The system cannot
  find the path specified" for the helper report. **Have the owner start the app from Explorer** (or a terminal
  they opened themselves). Same applies when reading session.db from a Claude shell: the sandboxed copy wins.

Step 2 findings:
- **Fixed:** Bluetooth outputs were detected by name only; Windows names a JBL Go 3 "Speakers (JBL Go 3)", so
  the default-is-Bluetooth finding never fired. `GuessKind` now checks the endpoint's bus
  (`PKEY_Device_EnumeratorName` = `BTHENUM`/`BTHLE*`) first.
- **Fixed:** helper report printed "DefaultMuted value: 0" for a muted device (`Tri.Yes` = 0); now prints the name.
- HDMI as wrong default not tried (no HDMI sink attached); same code path as Bluetooth.

Step 3 findings:
- **Fixed:** this network (campus Wi-Fi) blocks ping to the router, so a bad DNS server was reported as "can't
  reach your router, it's probably the router". The probe now falls back to the neighbour (ARP) table:
  `MSFT_NetNeighbor` State Reachable/Permanent for the gateway counts as reachable.
- **Fixed:** a hand-typed DNS server survives flush_dns and an adapter restart, so nothing fixed it. New T1 rung
  `reset_dns` / executor action `network.reset_dns <adapter instance id>` (WMI
  `Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder(null)`), offered only for DnsFailing with
  non-loopback static servers (loopback = a VPN such as WARP, left alone). The previous servers are in the probe
  state / helper report (`StaticDnsServers`).
- Cloudflare WARP hides DNS faults (it answers DNS itself); the bench pauses it with `--pause-warp` and
  reconnects it, retrying (one `warp-cli connect` once reported Success but stayed disconnected).

Step 4 findings:
- **Changed:** Windows 26300 no longer writes live camera use to the consent-store registry a non-admin app can
  read (it moved to the admin-only `C:\ProgramData\Microsoft\Windows\CapabilityAccessManager\CapabilityAccessManager.db`),
  and `MediaCapture` exclusive control is granted even while the Camera app streams. So CameraInUse only fires on
  older builds; the LooksNormal ladder now starts with `close_other_apps` (Teams, Zoom, WhatsApp, browser call),
  and the finding says "no app reported holding it".
- CS8848 at `RealCameraProbe.cs:66` was harmless (parsed as intended); parenthesised.

### TestBench (batch runner)

`tools/AutoFixScout.TestBench` runs the plan without the UI: per scenario it creates the fault, runs the app's
real probe + triage + ladder (executor rungs for real, "open Settings / please do X" rungs played as the person),
checks healthy, and always restores. One UAC prompt per batch; refuses to run off AFS-TESTBENCH; arms a scheduled
failsafe restore (`AFS-TestBench-Failsafe`, script in `C:\ProgramData\AFS-TestBench`) and removes it at the end.

    AutoFixScout.TestBench.exe [sound] [network] [camera] [--only 3c,3d] [--pause-warp] --out C:\afs\docs\testbench-results

Reports (`docs/testbench-results/*.md`) are committed; raw logs aren't (they can name local networks, repo is public).
It can't judge what only a person can (hearing the beep, seeing yourself); those stay manual.

Still never run on real hardware: `UpdateDriverForPlugAndPlayDevices`, `pnputil /export-driver`, the save point
ACL/owner checks, restore point creation, the WUA calls (steps 5 and 6).

## Test plan (only on AFS-TESTBENCH)

1. Run the app (CI artifact or the built `AutoFixScout.UI.exe`, started from Explorer, see above). Try each button
   on a healthy machine: findings should be sensible, no fix offered, outcome `no_fault_found`, nothing changed.
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
