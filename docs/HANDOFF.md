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
| UI (screens + flow) | `src/AutoFixScout.UI/MainWindow.xaml(.cs)`, look in `MainWindow.Visuals.cs` + `Theme/` (see UI below) |
| Tests (107, all pass in CI) | `src/AutoFixScout.Tests/` |

The unused first-skeleton leftovers (Orchestrator, SessionManager, Simulator, ActionCatalog, ActionContext, IProbe, IExecutor, Probes, StepResult, the legacy models in Models.cs, and the Serilog/YamlDotNet packages) are removed. Still to remove after the UI redesign lands: UI `IActionContext.cs` and unused UI packages (e.g. System.Data.SqlClient).

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
  - Executor exit codes: 0 ok, 1 failed (nothing changed), 2 rejected input, 3 nothing to do (restore: nothing to put
    back), 4 changed but undone, 6 restore failed (save point kept). Retention: see "Steps 5-6 findings".

## Real-hardware test results (AFS-TESTBENCH)

Laptop: MSI, Realtek audio (+ NVIDIA/Intel HDMI audio, Nahimic), Chicony "HD Webcam", Intel AX201 Wi-Fi, Realtek
Ethernet, Cloudflare WARP on (Wi-Fi DNS = 127.0.2.2/.3), Location services off, Windows 11 build 26300.

| Step | Status | Notes |
|---|---|---|
| 1. Healthy machine | **Passed after fix** (2026-11-11) | See below. Probes ran fine; no device/service/DNS/privacy/default-output change; WARP DNS not blamed. |
| 2. Sound faults | **Passed after fixes** (2026-11-11) | Muted -> unmute (no UAC). AudioSrv stopped -> restart_services (UAC). Realtek disabled (code 22) -> restart (no) then toggle_device (yes). Default = Bluetooth JBL Go 3 -> wrong_output after fix (see below). 2e Realtek uninstalled (driver kept) -> restart_services (no) then new rescan (yes). |
| 3. Network faults | **Passed after fixes** (2026-11-11, TestBench) | Wi-Fi disabled -> enable_adapter. Wi-Fi disconnected -> pick_wifi (internet back 7 s after reconnect). Bogus DNS (WARP paused) -> flush_dns (no) then new reset_dns (yes). Airplane mode (owner, by hand, no UAC) -> found (SystemRadioState=1), airplane rung opened Settings, switched off -> fixed; session + report correct. |
| 4. Camera faults | **Passed / changed** (2026-11-11, TestBench) | Each privacy switch off -> privacy rung, named correctly. Camera disabled -> enable_camera. 4f camera uninstalled (driver kept) -> new rescan first, fixed. "Camera app holding it" can't be detected on this Windows build (see below); ladder changed. Human check of the real Camera-app test still to do with the new UI. |
| 5. Rollback | **Passed after fix** (2026-11-11, TestBench + manual) | Intel Display Audio 10.27.0.8 -> 10.27.0.9 staged as "the recent update". 5a "works" -> 10.27.0.8 stays, marker cleared. 5b "doesn't" -> 10.27.0.9 restored from the save point. 5c app closed right after the change, reopened -> offered "Put it back", restored (owner, by hand). 5d non-admin can read but not write the save point folder. |
| 6. Windows Update driver | **Partly** (2026-11-11, TestBench) | Only "nothing offered": WU has no driver for the Realtek device here, so no change and no save point. A real WU install + undo is still untested on hardware. |
| 7. session.db + report | **Passed after fixes** (2026-11-11) | 10 UI sessions (steps 1, 2, BT check) with the right outcomes and steps; reports match and name no network. Cut-off flows weren't logged and `started_at` was wrong (see below). Bench runs don't write session.db (by design). |

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

Uninstalled devices (added before friend testing, 2026-11-11, TestBench 2e/4f):
- **Added:** a device uninstalled in Device Manager (driver kept) or dropped off after an update used to end as
  "can't find it" / probably hardware. New T1 rung `rescan` (executor `device.rescan <audio|camera|network>`):
  `CM_Reenumerate_DevNode` on the PnP root + `CMP_WaitNoPendingInstallEvents`, i.e. "Scan for hardware changes".
  Offered for NoCamera (first), NoNetworkAdapter, and sound with no active output (after restart_services).
  Network (Wi-Fi adapter uninstalled) not run on hardware: it would cut this session's connection; same code path.

Step 4 findings:
- **Changed:** Windows 26300 no longer writes live camera use to the consent-store registry a non-admin app can
  read (it moved to the admin-only `C:\ProgramData\Microsoft\Windows\CapabilityAccessManager\CapabilityAccessManager.db`),
  and `MediaCapture` exclusive control is granted even while the Camera app streams. So CameraInUse only fires on
  older builds; the LooksNormal ladder now starts with `close_other_apps` (Teams, Zoom, WhatsApp, browser call),
  and the finding says "no app reported holding it".
- CS8848 at `RealCameraProbe.cs:66` was harmless (parsed as intended); parenthesised.

Steps 5-6 findings:
- **Fixed (real bug):** `DriverStore.Read` took the driver version from `Win32_PnPSignedDriver`, which lags real
  changes (still the old version 6 s after setupapi logged a successful install). The executor's post-check uses the
  same reader, so it could have undone driver changes that worked. Now reads `Enum\<instance>\Driver` ->
  `Control\Class\<key>` `InfPath`/`DriverVersion` (live, no admin needed); WMI is only a fallback.
- **Weak heuristic:** "driver changed in the last 30 days" = oem*.inf creation date. On a freshly set up PC every
  oem*.inf is recent, so every driver counts as "just updated".
- **Fixed (needs a hardware retest): wrong device offered.** The sound flow offered rollback for any MEDIA device with
  an older package in the store, e.g. the HDMI audio driver when the problem was the laptop speakers. Now
  `AudioLadder.RollbackTargets`: devices reporting an error first, then the device behind the default output; other
  devices only when that device is unknown; a display-audio device (Intel/NVIDIA/AMD function on HDAUDIO/INTELAUDIO,
  or "HDMI"/"Display Audio"/"DisplayPort" in the name) only when the default output is on it (or is HDMI with its
  device unknown, or there is no default output). The probe ties each output to its device
  (`AudioEndpointInfo.DeviceInstanceId`): endpoint `PKEY_Device_InstanceId` -> parent device nodes (cfgmgr32
  `CM_Get_Parent`, up to 4 levels) -> first one in the MEDIA list. Not yet seen on real hardware (the dev PC has no
  audio endpoints): on the TestBench check that "Speakers (Realtek)" maps to the Realtek device and that rollback is
  no longer offered for Intel Display Audio while the speakers are the default. Camera and network already target
  the device they diagnose (network: the broken or connected adapter; camera: Camera-class devices first).
- **Fixed: save points piled up** (3 on the TestBench, and the old "keep the newest 10" prune could delete an open
  one). Now: a successful restore (`driver.restore`, or the automatic undo, exit 4) deletes that save point's folder;
  a failed restore writes a `restore-failed` flag into it and it is never pruned (exit 6). When a new save point is
  made, folders older than 30 days are pruned unless flagged or still held open by an app marker (the executor checks
  `pending-savepoints\<id>` in every profile's `%LOCALAPPDATA%`, MSIX-redirected copies included; can't tell = keep).
  Only 32-hex folders directly inside `savepoints`, never links. `driver.restore` of a save point without a manifest
  (the manifest is written before any change, or the folder is already gone) now returns 3 "nothing to put back" and
  the app clears its marker. A save point the person **kept** ("it works") stays until pruned after 30 days: deleting
  it at once needs another UAC prompt right after "Yes, it works" (owner's call; it is also a helper's way back).
- Staging a newer driver needs `UpdateDriverForPlugAndPlayDevices` with INSTALLFLAG_FORCE: `pnputil /add-driver
  /install` keeps the installed driver when it matches the device more specifically.
- **Bench bug, laptop changed:** the first `--only unstage` used a regex over the whole pnputil output that ran from
  one entry into the next; it deleted the laptop's original Intel Display Audio package (oem9.inf, 10.27.0.8) instead
  of the test one. Intel Display Audio now runs 10.27.0.9 (oem42.inf, Microsoft-signed, works). Fixed: `PnpDrivers`
  parses entry by entry; `stage` writes `C:\ProgramData\AFS-TestBench\driver-stage.txt` (original + staged package)
  and `unstage` deletes only the staged package, only while the original is still in the store (tests in
  `PnpDriversTests`). Getting 10.27.0.8 back needs the MSI/Intel graphics package; not done.

Step 7 findings:
- **Fixed:** a flow cut off mid-way (5c: app closed during a driver change; or a crash) left no row at all, because
  the session was written only at the end. The row is now opened when the flow starts; at the next app start
  `SessionLog.CloseUnfinished` marks open rows `abandoned`. An exception in the flow also ends it as `abandoned`.
  Verified in the app: camera check closed on its first screen, reopened -> `abandoned`, started 14 s before it ended.
- **Fixed:** `started_at` was stamped when the row was written at the end (every session lasted ~0.02 s); now the
  flow start time.
- Still missing: steps of a cut-off flow (written at the end), the startup "Put it back" restore (not logged
  anywhere), `device_snapshot` and `undo_record` (never written). Reports include device instance IDs, which for
  Bluetooth contain the device's MAC address; fine for a helper, worth knowing before any upload feature.

### TestBench (batch runner)

`tools/AutoFixScout.TestBench` runs the plan without the UI: per scenario it creates the fault, runs the app's
real probe + triage + ladder (executor rungs for real, "open Settings / please do X" rungs played as the person),
checks healthy, and always restores. One UAC prompt per batch; refuses to run off AFS-TESTBENCH; arms a scheduled
failsafe restore (`AFS-TestBench-Failsafe`, script in `C:\ProgramData\AFS-TestBench`) and removes it at the end.

    AutoFixScout.TestBench.exe [sound] [network] [camera] [drivers] [--only 3c,3d] [--pause-warp] [--newer-inf <path>] --out C:\afs\docs\testbench-results

`drivers` needs `--newer-inf`: a newer official INF for Intel Display Audio (used: 10.27.0.9 from the Intel graphics
cab on the Microsoft Update Catalog). `--only stage` leaves it installed for a manual test in the app;
`--only unstage` undoes exactly that stage.

Reports (`docs/testbench-results/*.md`) are committed; raw logs aren't (they can name local networks, repo is public).
It can't judge what only a person can (hearing the beep, seeing yourself); those stay manual.

Run on real hardware now: rollback via `UpdateDriverForPlugAndPlayDevices`, `pnputil /export-driver`, save point
ACL, restore point creation, restore from a save point, the WUA search. Still never run: a WU driver actually
downloaded and installed (WU offers nothing for this laptop's devices). Airplane mode (3a) needs a person (done by hand).

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

## UI

Plain WPF, no UI library. Presentation is kept apart from the flow: `MainWindow.xaml.cs` decides what to show,
`MainWindow.Visuals.cs` only draws it (`PresentStep` / `PresentHome`, the picture above each sentence, transitions).
`ShowStepAsync` has an optional `look` argument that changes nothing but the picture.

| What | Where |
|---|---|
| Colours (light / dark) | `src/AutoFixScout.UI/Theme/Light.xaml`, `Dark.xaml` (same keys in both, always `DynamicResource`) |
| High contrast | `ThemeManager.HighContrastPalette` maps every key onto the user's system colours |
| Buttons, focus ring, scroll bar, type scale | `Theme/Styles.xaml` |
| Theme switching + Mica | `Theme/ThemeManager.cs` |
| Animation | `Theme/Motion.cs` |

- **Theme**: follows Windows light/dark (`AppsUseLightTheme`) and high contrast live (WM_SETTINGCHANGE). Mica backdrop
  on Windows 11 22H2+ with transparency on; solid `#F3F3F3` / `#202020` otherwise (Windows 10, high contrast).
  `AUTOFIXSCOUT_THEME=light|dark` forces a theme for screenshots.
- **Colours**: Windows 11 accent blue (`#005FB8` light, `#60CDFF` dark). Text `#1B1B1B` / white, secondary text kept
  dark enough for older eyes (~8:1). Each topic has its own tile colour (sound blue, camera orange, internet green);
  endings: fixed = green animated check, no fault = blue check, not fixed / probably hardware = amber (and the
  "Details for my helper" button gets an accent outline), error = red, abandoned = grey info.
- **Type**: Segoe UI Variable (falls back to Segoe UI); title 34, message 24, card titles 22, buttons 19, captions 15.
  Icons from Segoe Fluent Icons (falls back to Segoe MDL2 Assets on Windows 10).
- **Shape**: cards 10-14 px corners, buttons 8 px; answer buttons are full width, 58 px tall, main answer filled.
- **Motion**: everything goes through `Motion`, which checks `SystemParameters.ClientAreaAnimation` each time
  (Windows "Animation effects" off = no movement at all). Screens fade + slide up (220 ms fade, 320 ms exponential
  ease-out), home cards stagger by 25 ms, buttons lift on hover / dip on press (90-200 ms), the check pops and draws
  itself. Repeated busy screens don't re-animate. Colour changes are instant. Nothing waits for an animation.
- **Accessibility**: two-ring keyboard focus visual, `AutomationProperties.Name` on the cards, the sentence is a live
  region so screen readers read each new step, window fits the work area and scrolls when small (min 420 x 460).

## Shortcuts taken (search the code for `shortcut:`)

Ladders hardcoded in C# instead of YAML playbooks; one UAC prompt per admin action (no long-lived executor);
airplane mode read from an undocumented registry value; "driver changed recently" = package staged date, not WU
history; camera test uses the Windows Camera app; session row opened at flow start but steps written once at the end.

## Next (owner's choice)

Real-hardware testing first. Then: Settings screen (opt-ins, delete my data), "Something else" free-text routing
(spec 9, LLM), packaging + code signing (Milestone 5), deleting the unused skeleton files.
