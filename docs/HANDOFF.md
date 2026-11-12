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
| Probes (read-only) | `src/AutoFixScout/Probes/Real*Probe.cs` (audio, network, camera, microphone, Bluetooth, storage, update, printer, display, health), `ProcessSampler.cs` |
| Areas (one class per problem: Topic, What, DiagnoseAsync, real test) | `src/AutoFixScout/Areas/*Flow.cs`; free-text routing in `SymptomRouter.cs` |
| Repair rules shared with the executor; user-level repairs | `src/AutoFixScout/Repair/` (`ServiceDefaults`, `HostsFile`, `SystemPath` are linked into the executor; `UserRepairs`, `StartupApps`, `CrashCauses`) |
| Triage + fix ladders | `src/AutoFixScout/Triage/*Triage.cs` (FindingCode enum and `Finding` in AudioTriage.cs) (`LadderRunner`, `DriverRules` live in AudioLadder.cs) |
| Non-admin actions, save point markers | `src/AutoFixScout/FixActions.cs` |
| Driver store logic (shared with executor) | `src/AutoFixScout/Drivers/DriverStore.cs`, `DriverStatus.cs` |
| Elevated executor | `src/AutoFixScout.Executor/{Program,SavePoints,WindowsUpdateDrivers,Repairs}.cs` (system repairs back up to `C:\ProgramData\AutoFixScout\backups`, tool output in `C:\ProgramData\AutoFixScout\logs`) |
| Session log (SQLite, spec schema) | `src/AutoFixScout/SessionLog.cs` -> `%LOCALAPPDATA%\AutoFixScout\session.db` |
| Helper report | `src/AutoFixScout/HelperReport.cs` -> `%LOCALAPPDATA%\AutoFixScout\reports\` |
| UI (screens + flow) | `src/AutoFixScout.UI/MainWindow.xaml(.cs)`, look in `MainWindow.Visuals.cs` + `Theme/` (see UI below) |
| Tests (all pass in CI) | `src/AutoFixScout.Tests/`; `Live*` tests touch the real machine and run only with `AFS_LIVE` set, one at a time |

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

## Wording

Every sentence a person reads follows `docs/WRITING.md`: formal, no contractions, the app never says "I" (it names
the action or "AutoFix Scout"), permission prompts, save points and automatic undo are always stated, Windows names
in quotes. Done for the logic project (finding messages, rung sentences, camera hints, network "not fixed" hints,
`FixActions` details, helper report headings); the UI project's own texts are a separate change.
`WritingStyleTests` scans every finding message, rung sentence and hint the triage classes produce for a set of
representative states (no contractions, no "I", no "we", no "!", admin rungs say "Windows will ask for your
permission"). New sentences must pass it; add a state there when you add a finding or rung.

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

Plain WPF, no UI library, no new packages. Presentation is kept apart from the flow: `MainWindow.xaml.cs` decides
what to show, `MainWindow.Visuals.cs` only draws it (`PresentStep` / `PresentHome`, header, journey, banner, picture,
checks list, transitions). Presentation-only hooks the flow calls: `ShowStepAsync(..., look:)` (picture + banner),
`ShowBusy(text, scan:)` (scanner + list of checks), `Journey(Stage)` (header steps), `SetTopic`. None of them change
what the flow does. All in-app wording follows `docs/WRITING.md`.

| What | Where |
|---|---|
| Colours (light / dark) | `src/AutoFixScout.UI/Theme/Light.xaml`, `Dark.xaml` (same keys in both, always `DynamicResource`) |
| High contrast | `ThemeManager.HighContrastPalette` maps every key onto the user's system colours (glows off, art goes monochrome) |
| Type scale, spacing, buttons, cards, focus ring, scroll bar | `Theme/Styles.xaml` |
| Illustrations (app mark, topics, endings) | `Theme/Art.xaml`: layered vector art on a 64-unit grid, one `ControlTemplate` each |
| Theme switching + Mica | `Theme/ThemeManager.cs` |
| Animation | `Theme/Motion.cs` |

- **Layout**: branded header on top (gradient + three soft glows drifting very slowly + faint scanner rings, app mark).
  Home: large header ("What would you like to check today?") and three elevated symptom cards, each with an
  illustration, a title and a formal subtitle ("Speakers, headphones and volume"). During a check the header turns
  compact: topic title + journey **Check -> Diagnosis -> Fix -> Confirm** (done = tick, current = number with halo,
  connector fills). An ending that did not resolve the problem marks the last step amber with "!".
- **Step card**: optional status banner (colour + icon by severity), illustration, overline "Step n of 4: ...", the
  first sentence as a heading, any further sentences as smaller supporting text (`Split`; "\n\n" also splits), buttons.
  Finding severity comes from `FindingLook(FindingCode)`: LooksNormal = ok (green), ProbeIncomplete = info (blue),
  settings such as mute / Airplane mode / privacy / disabled = warning (amber), everything else = error (red).
  Endings: fixed = green check with sparkles, no fault = blue shield, not fixed = report with "!", probably hardware =
  chip (both amber, and "Report for your helper" gets an accent outline), error = red, stopped = grey info.
- **Busy screen**: scanner (rotating arc + two sonar rings + magnifier). For the read-only check it also lists what is
  being looked at per topic (`ChecksFor`). The probe reports everything at once, so the list advances on a 900 ms
  timer and only says "checked / in progress / waiting", never a result; the last item stays in progress until the
  probe returns, and the caption says the results come when all checks are complete.
- **Theme**: follows Windows light/dark (`AppsUseLightTheme`) and high contrast live (WM_SETTINGCHANGE). Mica backdrop
  on Windows 11 22H2+ with transparency on; solid `#F3F3F3` / `#202020` otherwise (Windows 10, high contrast).
  `AUTOFIXSCOUT_THEME=light|dark` forces a theme for screenshots.
- **Colours**: Windows 11 accent blue (`#005FB8` light, `#60CDFF` dark). Text `#1B1B1B` / white, secondary text kept
  dark enough for older eyes (~8:1). Topic duotones: sound blue, camera orange, internet green.
- **Type**: Segoe UI Variable (falls back to Segoe UI): display 32, title 26, card title 21, body / buttons 18,
  caption 15, overline 13. Small icons from Segoe Fluent Icons (falls back to Segoe MDL2 Assets on Windows 10).
- **Shape**: cards 14 px corners, buttons 10 px, step card 16 px; answer buttons full width, 56 px tall; primary =
  gradient accent with a soft glow, secondary = outlined, subtle = text + icon.
- **Motion**: everything goes through `Motion`, which checks `SystemParameters.ClientAreaAnimation` each time
  (Windows "Animation effects" off = no movement at all, nothing loops). Screens fade + slide up (240 ms fade,
  340 ms exponential ease-out), home cards stagger by 55 ms, cards lift 3 px with a deeper shadow on hover and dip on
  press (100-220 ms), journey connectors fill in 320 ms, end pictures pop and their check draws itself. Only the
  header glows (13-21 s) and the busy visuals loop. Repeated busy screens don't re-animate. Nothing waits for an animation.
- **Accessibility**: two-ring keyboard focus visual, `AutomationProperties.Name` + `HelpText` on the cards, the journey
  and banner have names ("Step 2 of 4: Diagnosis"), the sentence is a live region whose name is the full text, window
  fits the work area and scrolls when small (min 440 x 480).
- **Screenshots**: `AUTOFIXSCOUT_SOFTWARE_RENDER=1` forces WPF software rendering. Needed on the TestBench on
  2026-11-11: with hardware rendering every WPF window (also the previous UI) stayed white when started from a Claude
  shell, while UI Automation saw the full layout. Not seen elsewhere; check on the owner's PC before release.

## Shortcuts taken (search the code for `shortcut:`)

Ladders hardcoded in C# instead of YAML playbooks; one UAC prompt per admin action (no long-lived executor);
airplane mode read from an undocumented registry value; "driver changed recently" = package staged date, not WU
history; camera test uses the Windows Camera app; session row opened at flow start but steps written once at the end.

## Next (owner's choice)

Real-hardware testing first. Then: Settings screen (opt-ins, delete my data), "Something else" free-text routing
(spec 9, LLM), packaging + code signing (Milestone 5), deleting the unused skeleton files.

## Overnight plan (2026-11-12 night, unattended on AFS-TESTBENCH)

The owner is away and cannot click. Merging PRs is blocked in auto mode, so everything lands on branch
`overnight` with one PR for the owner to review. Admin tests run through the scheduled task
`AFS-Bench-Elevated` (owner runs `C:\afs-bench\SETUP-run-once-as-admin.ps1` once): drop a `.ps1` into
`C:\afs-bench\jobs`, run `schtasks /run /tn AFS-Bench-Elevated`, read `C:\afs-bench\out\<name>.log`
(`<name>.done` appears when finished). Never reboot, never break the network for more than seconds (it
would cut off the session), never change the clock.

Goal: move from simple switches (mute, airplane mode) to real software repair: "deep fix engine".

1. [done] Merge PR 8 (storage/update), 9 (microphone), 10 (printer/display WIP) into `overnight`.
2. [done, calibrated on the bench] Health area ("Something else is wrong"): `HealthProbe` reads event logs (app crashes + faulting module,
   blue screens, unexpected shutdowns, disk/NTFS/WHEA errors, missing runtimes (SideBySide), failing
   services), clock offset, proxy (WinINet, WinHTTP), hosts-file hijacks, Winsock LSPs, system PATH, TEMP,
   critical services disabled, broken Store apps, pending restart.
3. [code done; user-level ones passed live (proxy, Store app, Explorer); admin ones wait for the runner: `TestBench.exe health --long`] Repair actions (allowlisted, validated, backups first): sfc, DISM RestoreHealth, time resync, reset
   WinHTTP proxy, reset user proxy (non-admin), clean hosts (backup), Windows Update component reset
   (rename, not delete), restore service default start type, repair system PATH, rebuild search index,
   re-register broken Store app (non-admin), restart Explorer, rebuild icon cache, install VC++ runtime
   (Microsoft URL + signature check), chkdsk /scan, winsock/TCP-IP reset (unit-tested only, needs restart).
4. "A program keeps closing" flow: crashed apps from the event log -> cause by faulting module
   (runtime, .NET, graphics driver, system file, the app itself) -> matching rungs.
5. [done, checked in the real UI] Symptom search on the home screen: free text -> area or health check.
6. [done, checked in the real UI] Wire all areas into the home screen ("Other problems" opens the list).
7. TestBench: fault injection for each new repair, run unattended through the elevated runner.
8. Docs, writing-style tests, CI green, PR from `overnight`.

Next (in order): HealthFlow, home wiring and the crash picker are done (PR 11, checked in the real UI).
Then:
Bluetooth area [done], then run the admin health bench as soon
as `schtasks /query /tn AFS-Bench-Elevated` works: job file running
`tools\AutoFixScout.TestBench\bin\Debug\net8.0-windows\AutoFixScout.TestBench.exe health --long --out C:\afs-bench\out`.

### Status 2026-11-12, ~22:20 IST (PR 11, CI green, 419 tests)
Done and checked in the real UI on the bench: full check-up, "A program keeps closing" (picker), symptom search
box, Bluetooth, Start menu/taskbar/search, Microsoft Store/Office/OneDrive, Security, slow computer (startup apps,
busy programs, component cleanup), microphone, Windows Update, printer, display, internet check with proxy/hosts/
clock/Winsock. Live (`AFS_LIVE`) tests pass for every non-admin repair.
Not run on hardware yet (need the elevated runner): every executor repair (DISM, SFC, chkdsk, Update reset, hosts,
PATH, WinHTTP, Winsock, services, VC++, search index, component cleanup, firewall/UAC on, Office Quick Repair),
OneDrive reset and WSReset (bench has no OneDrive/Office; WSReset opens the Store window).
UI note: "Apply fix" on an admin step leaves a UAC prompt that nobody can answer when unattended; it blocks
screenshots until it times out. Drive admin steps through the TestBench (elevated) instead of the UI.

## Roadmap (overnight part 2, from 2026-11-13 00:00 IST; owner asleep, heartbeat every 20 min)
Rules: stop at 80% of each 5-hour window, resume after the reset (02:20 IST, then 07:20). One item at a time unless
using worktree agents. Each item: probe + triage + tests + live test + real-UI check + HANDOFF/CAPABILITIES update.
- [x] R1 Drive health: MSFT_PhysicalDisk HealthStatus / Get-StorageReliabilityCounter (wear, read errors, temperature)
      in the full check-up; "back up now" finding; never repairs hardware.
- [x] R2 Battery and power ("battery drains / will not sleep / will not wake"): battery wear (design vs full charge),
      power plan, sleep settings, hibernate/fast startup, wake timers and devices allowed to wake; guided + safe fixes.
- [x] R3 Any device with an error (Device Manager warning) outside sound/camera/network: listed in the check-up with
      the Windows error code meaning; rescan; driver from Windows Update only for allowed classes, otherwise guided.
- [ ] R4 Network extras: no address from the router (169.254.x.x) -> renew the address; weak Wi-Fi signal; IP address
      conflict; metered connection blocking updates.
- [x] R5 Temporary user profile ("my files and desktop are gone"): detect, explain, guided recovery; never touches profiles.
- [x] R6 Keyboard, mouse and touchpad: touchpad switched off, Sticky/Filter Keys on, input device errors.
- [x] R7 Clock settings (automatic time; time zone auto-update off is normal, not flagged): automatic time / time zone off or wrong.
- [x] R8 Admin bench run (`C:\afs-bench\jobs\01-health-long.ps1`) as soon as the AFS-Bench-Elevated task exists.

Status 2026-11-13 ~00:45 IST: power area (R2) merged (436 tests); NOT yet in the UI/router (add a "Battery, sleep and power" button + router words, TestQuestion from PowerFlow). Input agent (R6) was paused at the 80% stop: continue in worktree C:\afs-wt\input-area (branch input-area; uncommitted work may be there), then merge and wire the UI.
- 2026-11-13: owner confirmed the bench touchpad really is switched off (Fn key) — the input area's "touchpad off" finding is correct on hardware. Admin bench (elevated runner): H1-H6 + clock sync + search index + VC++ install + Update reset + chkdsk scan all PASS, machine restored.
- 2026-11-13 00:33 long admin bench: VC++ install (signed download), Update reset (old folder kept as SoftwareDistribution.afs-<stamp>), chkdsk /scan, DISM RestoreHealth (9.4 min), SFC (1.7 min; it FOUND and repaired corrupt files on the bench), component cleanup (4 min): all PASS. Update services back to normal.
- R4 network extras: agent hit the usage limit mid-way; its work is in worktree C:\afs-wt\net-extras (branch net-extras). After the 02:20 reset: finish it (or resume the agent), review, test, merge.
