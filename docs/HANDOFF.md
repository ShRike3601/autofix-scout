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
checks list, transitions, title bar and sidebar). Presentation-only hooks the flow calls: `ShowStepAsync(..., look:)` (picture + banner),
`ShowBusy(text, scan:)` (scanner + list of checks), `Journey(Stage)` (header steps), `SetTopic`. None of them change
what the flow does. All in-app wording follows `docs/WRITING.md`.

| What | Where |
|---|---|
| Colours (light / dark) | `src/AutoFixScout.UI/Theme/Light.xaml`, `Dark.xaml` (same keys in both, always `DynamicResource`) |
| High contrast | `ThemeManager.HighContrastPalette` maps every key onto the user's system colours |
| Type scale, spacing, buttons, cards, focus ring, scroll bar | `Theme/Styles.xaml` |
| App mark | `Theme/Art.xaml` (vector, 64-unit grid). Every other picture is an icon-font glyph on a tinted tile |
| Palette choice (dark / light / high contrast) | `Theme/ThemeManager.cs` |
| Animation | `Theme/Motion.cs` |

- **Look (redesigned 2026-11-13, owner request)**: a dark, flat utility-dashboard look in the style of vendor
  control-centre apps: solid surfaces, 1 px borders, no shadows, gradients, glows, illustrations or confetti. The
  owner found the earlier large-type, illustrated look unprofessional; do not bring back the big type scale.
- **Layout**: the window draws its own title bar (`WindowChrome`, 36 px drag strip, own minimise / maximise / close
  buttons). Left sidebar (220 px): app mark, navigation ("Troubleshoot", "History and settings"), and at the bottom
  "Report for your helper" and the version. Home page: greeting + question, the describe-the-problem box, and "Browse by
  category" (see **Categories** below). During a check the page header shows only the
  topic title; the journey **Check -> Diagnosis -> Fix -> Confirm** is a vertical "Progress" panel on the right (264 px:
  dot, name and one line per stage; done = tick, current = number with halo, connector fills downwards; hidden outside
  a check). An ending that did not resolve the problem marks the last step amber with "!".
  "History and settings" is disabled while a check is running; "Troubleshoot" closes the history page.
- **Closest checks** (2026-11-13, owner approved): "Find a fix" no longer offers one guess. `OnSymptomSearch` shows the
  top three `SymptomRouter.Rank` candidates as rows (icon, name, `Covers` line) plus "None of these", which runs the
  full check-up; when `NoGoodMatch` it says so and offers the nearest check and the check-up. `ShowChoicesAsync`
  (Visuals) is the reusable choice screen.
- **Categories** (2026-11-13, owner's idea: group like Control Panel instead of one tile per problem): the home page
  shows nine cards in three columns: eight categories named after the part of the computer (Sound and microphone,
  Internet and Bluetooth, Display and camera, Keyboard mouse and touchpad, Printer and scanner, Battery and
  performance, Apps and programs, Windows and security) and an accent "Full computer check-up" card that starts the
  check-up directly. Each card's caption lists what is inside. A category opens a choice screen (`OpenCategory` ->
  `ShowChoicesAsync`) whose rows are still symptom-first ("Printer is not working") with one line of examples, plus
  "Something else", which runs the full check-up. The list lives in `Categories()` in `MainWindow.Visuals.cs`; the
  cards are built in code (`BuildCategories`), not in XAML. **A new area needs one `Problem` line in its category**
  (glyph, title, caption, handler), no XAML. "Keyboard types the wrong characters" uses `OnTypingClicked`
  (`InputFlow.DiagnoseAsync(typingFocus: true)`). The old "Common problems" cards and 18 "Other problems" tiles are gone.
- **Part 8 screens** (2026-11-14, `MainWindow.Extras.cs`, owned by the UI work; requests in `docs/UI_REQUESTS_PART8.md`):
  - *Battery and performance* has three more rows: "Computer or games running slowly" (`OnPerfClicked`), "Games stutter
    or crash" (`OnGraphicsClicked`), "Short load test" (`OnStressClicked`).
  - *Short load test*: consent screen (bullets, "Start test" / "Not now"), then a busy screen with phase, seconds,
    processor speed limit, graphics card temperature and a "Stop" button, then the normal `RunFlowAsync` result
    screens. The re-test is the load test itself; a clean first run is not repeated (`_stressFresh`). A stopped test
    (`StressNotRun`) is shown as information, not as a problem.
  - *Home notices*: `ShowStartupNoticesAsync` (called from `Loaded`) shows the "What is new" card once per version and
    the update bar ("Version X is ready." / "Update and restart" / "Not now"). Download and setup only start on the click.
  - *History and settings* is now a real page (`HistoryPanel`, built in `BuildHistory`), not a step screen: changes with
    "Undo" / "Undone" / hint, recent checks with outcome, "Open the reports folder", and settings (update check,
    Wi-Fi watcher state with "Switch off", notification on reconnect). `HistoryText.Build` is no longer used by the UI.
  - Sessions are logged under the topic name itself (only "sound" is still mapped to "audio").
  - Checked on the bench: home with the What is new card, history page (also maximised), category rows, load test
    consent, progress and Stop. **Not exercised**: Undo, Wi-Fi watcher "Switch off", the update bar and download
    (a local build is never offered an update), a load test run to the end, perf and graphics checks.
- **Step card** (the "stage": fills the page beside the progress panel, content centred, at most 560 wide): optional
  status banner (tinted strip, icon + label coloured by severity), a 56 px icon tile, the first sentence as a centred
  heading, any further sentences as supporting text (`Split`; "\n\n" also splits; centred when short, left aligned for
  lists and long text), and the buttons side by side (not full width). The owner rejected a small card in the corner with a
  horizontal step bar on top (2026-11-13): keep the page filled.
  Finding severity comes from `FindingLook(FindingCode)`: LooksNormal = ok (green), ProbeIncomplete = info (blue),
  settings such as mute / Airplane mode / privacy / disabled = warning (amber), everything else = error (red).
  The icon tile shows the topic's icon (`TopicGlyph`) during a flow and a status icon on endings: fixed = green
  check, no fault = blue shield, not fixed = amber document, probably hardware = amber chip (for both, "Report for
  your helper" gets an accent outline), error = red, stopped = grey info.
- **Busy screen**: small scanner (rotating arc + two sonar rings + magnifier). For the read-only check it also lists
  what is being looked at per topic (`ChecksFor`). The probe reports everything at once, so the list advances on a
  900 ms timer and only says "checked / in progress / waiting", never a result; the last item stays in progress until
  the probe returns, and the caption says the results come when all checks are complete.
- **Theme**: dark by default, whatever the Windows app mode is. High contrast is still followed live
  (WM_SETTINGCHANGE). `Light.xaml` is kept in step but is only reachable with `AUTOFIXSCOUT_THEME=light`; there is
  no in-app switch yet. No Mica: all surfaces are solid.
- **Colours**: page `#121215`, sidebar `#0B0B0D`, cards `#1A1A1E`, borders `#28282E`; text `#F1F1F3` / `#A1A1AB`.
  One accent: `AccentBrush` `#5AA2FF` for icons and lines, `AccentFillBrush` `#2670E8` under white text (buttons,
  journey dots). Status: green / blue / amber / red, each with a dark tinted tile.
- **Type**: Segoe UI Variable (falls back to Segoe UI): page title 22, card heading 17, tile title 14, body 13.5,
  buttons 13, caption 12, label 11. Icons from Segoe Fluent Icons (falls back to Segoe MDL2 Assets on Windows 10).
- **Shape**: cards and tiles 8 px corners, buttons 5 px; answer buttons 34 px tall, at least 112 wide; primary =
  solid accent, secondary = grey with a border, sidebar items = text + icon.
- **Motion**: everything goes through `Motion`, which checks `SystemParameters.ClientAreaAnimation` each time
  (Windows "Animation effects" off = no movement at all, nothing loops). Screens fade + slide up a few pixels, home
  cards stagger by 40 ms, buttons dip on press, journey connectors fill in 320 ms, end icons pop. Only the busy
  visuals loop. Repeated busy screens don't re-animate. Nothing waits for an animation.
- **Accessibility**: two-ring keyboard focus visual, `AutomationProperties.Name` + `HelpText` on the cards, the journey
  and banner have names ("Step 2 of 4: Diagnosis"), the sentence is a live region whose name is the full text, window
  fits the work area and scrolls when small (min 880 x 560; default 1100 x 760).
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
- [x] R4 Network extras: no address from the router (169.254.x.x) -> renew the address; weak Wi-Fi signal; IP address
      conflict; metered connection blocking updates.
- [x] R5 Temporary user profile ("my files and desktop are gone"): detect, explain, guided recovery; never touches profiles.
- [x] R6 Keyboard, mouse and touchpad: touchpad switched off, Sticky/Filter Keys on, input device errors.
- [x] R7 Clock settings (automatic time; time zone auto-update off is normal, not flagged): automatic time / time zone off or wrong.
- [x] R8 Admin bench run (`C:\afs-bench\jobs\01-health-long.ps1`) as soon as the AFS-Bench-Elevated task exists.

Status 2026-11-13 ~00:45 IST: power area (R2) merged (436 tests); NOT yet in the UI/router (add a "Battery, sleep and power" button + router words, TestQuestion from PowerFlow). Input agent (R6) was paused at the 80% stop: continue in worktree C:\afs-wt\input-area (branch input-area; uncommitted work may be there), then merge and wire the UI.
- 2026-11-13: owner confirmed the bench touchpad really is switched off (Fn key) — the input area's "touchpad off" finding is correct on hardware. Admin bench (elevated runner): H1-H6 + clock sync + search index + VC++ install + Update reset + chkdsk scan all PASS, machine restored.
- 2026-11-13 00:33 long admin bench: VC++ install (signed download), Update reset (old folder kept as SoftwareDistribution.afs-<stamp>), chkdsk /scan, DISM RestoreHealth (9.4 min), SFC (1.7 min; it FOUND and repaired corrupt files on the bench), component cleanup (4 min): all PASS. Update services back to normal.
- R4 network extras: agent hit the usage limit mid-way; its work is in worktree C:\afs-wt\net-extras (branch net-extras). After the 02:20 reset: finish it (or resume the agent), review, test, merge.
- Roadmap part 2 complete (R1-R8). UI is being reworked in a separate Claude session (owner, 2026-11-13): do not edit src/AutoFixScout.UI from this session; expect a merge from that work.

## Roadmap (part 3, from 2026-11-13 ~01:30 IST)
No UI edits in this session (another session reworks the UI). New checks go into existing areas so they are reachable now.
- [x] Q1 Browser hijack by policy (Security area): Chrome/Edge/Firefox policies a home PC should not have (forced extensions,
      forced homepage/search engine, "managed by your organization"), plus browser proxy settings. Fix: remove those policy
      values with a backup (HKCU without admin, HKLM through the executor with a fixed list of policy names).
- [x] Q2 "What changed before the problem started" (check-up): programs, Windows updates and drivers installed in the last
      7 days (Uninstall registry keys, Win32_QuickFixEngineering, driver install events) shown in the finding detail and
      a guided "uninstall the recent update/program" step; System Restore as a last-resort guided step when a restore
      point exists from before the change (never run automatically).
- [x] Q3 Graphics driver resets and freezes (check-up): Display event 4101 (TDR) and LiveKernelEvent 141/117 counts ->
      graphics driver finding; guided driver update via Windows Update optional updates / maker's site.
- [x] Q4 Sound: one app muted or at zero in the Volume Mixer (per-app session volume), audio enhancements causing
      distortion (guided). Fix: unmute that app's session (non-admin, NAudio session API).
- [x] Q5 Big space users (Slow or full computer): Windows.old, hibernation file, Downloads folder size, Delivery
      Optimization cache, old Windows Update cleanup; safe cleanups only (Storage Sense / component cleanup), guided
      for personal folders (never delete personal files).
Working copies: C:\afs belongs to the UI session (branch ui-overhaul). This session works in C:\afs-wt\overnight (branch overnight); agents in C:\afs-wt\<name>.
- Q1 admin path verified on hardware (2026-11-13 03:55): dummy HKCU Chrome HomepageLocation removed by the elevated executor (exit 0), bad arg rejected (2), cleaned up.

## Roadmap (part 4, from 2026-11-13 ~04:00 IST)
- [x] P1 Per-app privacy (Microphone and Camera areas): one app (Teams, Zoom, a browser) denied microphone or camera
      access while the global switch is on (HKCU ConsentStore\<capability>\<app or NonPackaged\path> Value=Deny);
      guided Settings step naming the app; no silent changes to privacy choices.
- [x] P2 Blue-screen causes (check-up): read the stop code of recent blue screens (event 1001 BugCheck parameters /
      WER) and explain common ones in plain words (memory, driver, storage, graphics); offer Windows Memory Diagnostic
      as a guided step for memory-type codes; point to the driver for driver-type codes.
- [x] P3 Virtual memory (check-up): page file disabled or far too small for the installed memory -> "low memory" crashes;
      fix through the executor: let Windows manage the page file size (backup of the old setting; restart needed).
- [x] P4 Threats found by Microsoft Defender (Security): recent detections (MSFT_MpThreatDetection) and whether they were
      removed; guided "Protection history" step; full scan offer.
- [x] P5 Scanner and camera import (Printer area): Windows Image Acquisition service (stisvc) off; scanner device errors.
- P5: OnScannerClicked (MainWindow.xaml.cs) runs the printer flow with ScanFocus; the search box routes "scanner" there. The home tile is added by the UI session.
- P3 bug found on hardware: system.pagefile_auto used WMI path "\.\root\cimv2" (missing "\") -> "Invalid parameter"; fixed, LivePageFileTests guards it; elevated re-run: exit 3 (already automatic), bad arg 2.
- Idea from the UI session, owner has NOT decided: SymptomRouter could return a confidence plus the next 1-2 candidates and "no good match", so the UI can offer "closest checks". Do not start unless the owner asks.

## Roadmap (part 5, queued 2026-11-13 ~04:40 IST; start after the next usage reset, agents on Sonnet in worktrees)
- [x] F1 Wi-Fi keeps dropping (internet check): frequent disconnects (WLAN-AutoConfig 8003/8001 counts), adapter
      power saving ("Allow the computer to turn off this device"), roaming aggressiveness; guided steps; never change
      the network live in tests.
- [x] F2 Programs will not install (Store/apps area): Windows Installer service (msiserver) disabled, MsiInstaller
      failure events (11708/1033 with error codes explained), pending restart blocking installs, low space; fixes:
      service default, restart guide, clear the user's temp (exists).
- [x] F3 Notifications missing (Start menu/taskbar area): notifications switched off globally or per app, Do not
      disturb / Focus on, notification banners off; guided Settings steps naming the app; non-admin switch back on for the
      global toggle if it can be done safely.
- [x] F4 USB devices disconnecting (check-up): Kernel-PnP / USB hub errors and frequent device add/remove events, USB
      selective suspend on battery; guided power setting step, replug/hub advice.
- F2/F3 entries: OnInstallClicked (Apps, Install focus) and OnNotificationsClicked (Shell, notification focus) in MainWindow.xaml.cs; search routes 'install' and 'notifications'. Tiles: UI session. Bench note: the full check-up/install focus reports a pending restart, most likely from tonight's SFC repair; a restart clears it.
- F1/F4 found on the bench: the USB check first read the System log (no Kernel-PnP device events there: 0 vs 671) and then, with real data, flagged the built-in webcam because devices log events at every start-up/wake; fixed (Kernel-PnP Configuration log; threshold grows with starts and wakes). Wi-Fi drops subtract sleeps/shutdowns. The bench's Wi-Fi drops finding (about 28 in 3 days) is real: earlier test runs switched the adapter off and on.
- Roadmap part 5 complete.
- 2026-11-13 13:10: SymptomRouter.Rank (closest checks, confidence, Covers line, NoGoodMatch) added for the UI session's choice screen (owner approved via that session); commit 2f366cf.

## Roadmap (part 6, from 2026-11-13 ~13:15 IST)
- [ ] G1 Screen looks wrong (Display area): resolution below the screen's native one, refresh rate below the best supported
      (e.g. 60 Hz on a 144 Hz screen), unusual scaling causing blurry text; guided Settings steps (no silent display changes).
- [ ] G2 Slow start-up (Slow or full computer): boot and sign-in durations (Diagnostics-Performance event 100 where
      readable, else start-up app count and Fast Startup state), with the slowest start-up items when Windows names them.
- [ ] G3 Keyboard types the wrong characters (Keyboard area): several input languages/layouts installed, a different
      layout active than the display language suggests, the Alt+Shift / Windows+Space switch hotkeys; guided steps.
- [ ] G4 Sound gets quieter during calls (Sound area): communications "ducking" setting (reduce other sounds by 80%/50%/
      mute), different default device for communications; non-admin switch to "Do nothing" with a backup.
- 2026-11-13 15:25 bench Wi-Fi fix (owner's university WPA2-Enterprise network kept dropping on this laptop only):
  cause 1 = Intel AX201 driver 22.70.0.6 (2021) "disconnected by the driver"; cause 2 = no saved 802.1X sign-in, so
  every drop became a long outage (event 12013 "UI could not be displayed"). Owner saved the sign-in; adapter set to
  802.11ac, roaming Medium-low, No SMPS, prefer 5 GHz (backup C:\afs-bench\out\wifi-advanced-backup.csv). The Wi-Fi
  drops check now counts 12013 and offers "save your network sign-in" first. Next: newer Intel driver (owner's OK needed
  to download it).
- 2026-11-13 15:35: the owner's Wi-Fi problem is now a product capability (internet check, Wi-Fi drops): detects enterprise
  Wi-Fi, failed 802.1X re-sign-ins (12013), old driver (>2 years) and pending adapter tuning (WifiTuning, shared with the
  executor). New admin action network.wifi_stability <adapter instance id>: changes only those 4 values to supported
  targets, backs up, restarts the adapter, restores automatically if no internet within 4 minutes (exit 4); 3 = already
  tuned. Verified on the bench: no-op exit 3, bad arg 2, and a real run (original values restored first) exit 0 after
  7 s with backup written. No UI changes were needed (no new tile).
- Part 6 status: G1 display and G4 call ducking merged; G2 slow start-up and G3 keyboard layout are WIP on branches
  g2-boot / g3-layout (agents stopped by the owner, WIP committed, unreviewed, not merged).

## Part 7 status (2026-11-13)
- Merged g2-boot (slow start-up: boot times, slow items, Fast Startup) and g3-layout (several keyboard layouts with a switch hotkey, layout vs display language). 873 tests pass; live read-only tests pass on the bench.
- Known limit: the Diagnostics-Performance log (boot times) is readable only by administrators, so without elevation the slow start-up check reports Unknown and raises no finding. A later part can read it through the elevated executor.
- InputFlow.DiagnoseAsync(typingFocus: true) puts layout steps first; the UI does not call it yet (UI session to wire when the symptom is about wrong characters). The search words route "wrong characters", "keyboard layout" and similar to the keyboard check.

## Roadmap (part 8, from 2026-11-13 evening; owner away, heartbeat every 20 min, agents on Sonnet)
Owner's goals: find why games/PCs suddenly run slowly (throttling, Resizable BAR, power/voltage instability,
crashing or corrupt drivers, GPU overclocks from MSI Afterburner), a short opt-in stress test, a background
Wi-Fi watcher that reconnects, and a faster/smaller app. Rust rewrite: declined (bottleneck is Windows calls).
Branch `perf` (from main 9f5f3f5) collects the work; agents work in C:\afs-wt\g5..g8 and are merged into `perf`.
- g5-perf: new area "performance" (CPU/system): throttling (perf counters, Kernel-Processor-Power), power plan,
  battery, BIOS age, Intel 13th/14th gen microcode, RAM speed vs rated (XMP), single-channel RAM; reuses health readings.
- g6-gpu: new area "graphics": GPU, driver age, NVIDIA readings via nvidia-smi (temperature, clocks, throttle
  reasons, PCIe link, BAR1 = Resizable BAR), driver resets, MSI Afterburner overclock profiles, monitor on the wrong GPU.
- g7-watch: opt-in background Wi-Fi watcher (AutoFixScout.Watch.exe, HKCU Run), reconnects saved networks.
- g8-speed: ReadyToRun, parallel probes, smaller installer; measure before/after.
- then: opt-in stress test (CPU + GPU load ~60 s with live readings, stops on heat), bench live tests, CI, v0.3.0 tag.
- UI tiles: ask the UI session (one Problem line each in Categories() in MainWindow.Visuals.cs).
- Installer (done, bf699c2, verify on the bench): a newer setup silently uninstalls the old version first (same AppId,
  same folder, one Start menu entry), asks before a downgrade, force-closes a running app. Bench check: install
  v0.2.0, then the perf CI setup; expect one entry in Installed apps and one Start menu entry.
- Quality-of-life candidates (owner to pick): in-app "new version available" check against GitHub releases
  (needs privacy note), "What's new" after an update, "Did this fix it?" feedback kept locally and added to the
  helper report, changes history with Undo buttons, watcher toast "Wi-Fi reconnected", optional weekly check-up
  with a toast only when something is wrong, uninstall option to also delete AutoFix Scout data.
- Status ~22:30 IST: merged into perf: g5-perf (performance area, 10 Perf codes), g6-gpu (graphics area, Gpu codes;
  Afterburner .cfg keys unverified against a real install), g7-watch (watcher). 1018 tests pass. Installer fix: silent
  upgrade kept removing the watcher Run value -> PrepareToInstall saves/restores it and [Run] restarts the watcher.
  Running: g8-speed, g9-qol (update check, What's new, history+undo, uninstall-data prompt), g10-stress (stress test),
  g11-watch2 (watcher via wlanapi instead of English netsh text, "Wi-Fi reconnected" toast).
  Bench live (this laptop): PerfBiosOld (BIOS 2020); GpuDriverResets (2 in 14 days), GpuResizableBarOff (BAR1 256 MiB).
- UI tile requests to send in one message when all agents are merged: performance (PerfFlow, "Computer or games
  running slowly"), graphics (GraphicsFlow, "Games stutter or crash"), stress (StressFlow), update banner, What's new,
  History with Undo, watcher on/off + toast switch.

## Quality of life (branch g9-qol)
Core only; the UI session wires it (see the UI requests in the g9 report). Files: `src/AutoFixScout/Updates/*`
(`UpdateService`, `AppVersion`, `AppSettings`, `UpdateState`, `WhatsNew`, `ReleaseNotes`), `ChangeHistory.cs`,
`docs/WHATS_NEW.md` (embedded resource, built-in "What's new" text; the release workflow also puts it above the first
`---` of the GitHub release notes, which is what the app shows).
- Files in `%LOCALAPPDATA%\AutoFixScout`: `settings.json` (`CheckForUpdates`, default true; `OfferUpdatesToDevBuilds`,
  default false), `update.json` (last check time, last seen version, newest release found), `updates\` (downloaded setup).
- Update flow: `CheckAsync` (api.github.com, 5 s, at most once per start and per 6 h) -> person clicks ->
  `DownloadAsync` (size from the asset, SHA-256 from `AutoFixScout-Setup-<v>.exe.sha256` when present; build.yml writes and
  uploads it) -> `StartSetup` (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1`, runas) -> app exits. The setup
  restarts the app only with `/RELAUNCH=1`. Open item: an Authenticode check of the downloaded setup once releases are signed.
- History: `SessionLog.AddSteps` now stores `step_log.detail` and writes an `undo_record` for driver changes (save point id)
  and switched-off start-up apps; `ChangeHistory.List/UndoAsync` use them. Other changes are listed with a hint only.
- Uninstall asks (interactive only, default No) whether to delete both data folders; silent uninstall (updates) keeps them.
- Status ~23:30 IST (usage limit hit): ALL agents merged into perf (g5..g11); 1112 tests pass; combined installer
  compiles in CI (run 43463336447 success). UI requests sent to the UI session (docs/UI_REQUESTS_PART8.md).
  Manual test build 0.3.0-rc1 started: run 43464265774 (workflow_dispatch input "version").
  NEXT: (1) bench upgrade test via elevated runner (C:\afs-bench\run-job.ps1 picks jobs from C:\afs-bench\jobs):
  silent install v0.2.0 setup, then the rc1 setup silently; expect one Uninstall entry (DisplayVersion 0.3.0-rc1),
  one Start menu entry, no old files; then interactive uninstall data prompt by hand. (2) merge the UI session's
  branch when it reports its hash. (3) merge perf -> main, tag v0.3.0. AutoFix Scout is currently not installed here.
- Bench upgrade test 2026-11-14 01:34 (v0.2.0 -> 0.3.0-rc1, silent, elevated runner): PASS for one copy only (1 uninstall
  entry "AutoFix Scout 0.3.0-rc1", same 2 Start menu shortcuts, 486 -> 276 files, 0.2.0-only
  Microsoft.Xaml.Behaviors.dll removed) and for the watcher (Run value kept, watcher restarted by [Run] WatcherWasOn).
  Not run: the silent downgrade refusal. Harness lesson: PowerShell 5.1 `Start-Process -Wait` also waits for child
  processes, so it hung on the restarted watcher (elevated, because the runner is elevated; a normal install starts it
  as the user). Use `$p = Start-Process ... -PassThru; $p.WaitForExit()` instead. The hung runner + elevated watcher
  go away with a restart. 0.3.0-rc1 stays installed on the bench.
- UI session status: tiles for performance and graphics done locally (not pushed); stress screens, update banner,
  What's new, History, settings NOT done. Do not tag v0.3.0 until they are pushed and merged.
- 2026-11-14 ~02:30: UI session's ff42645 (all part 8 screens) merged into perf. Fixed: History names for every area,
  stress wording ("about two minutes"), CS8604. LiveReleaseChecks (AFS_LIVE) on the bench: real update check offered
  0.2.0 to a 0.1.0 build and verified the 95.7 MB download (no .sha256 on 0.2.0; 0.3.0 onwards has one); full load test
  105 s, Completed, GPU max 82 C, CPU limit 100 %, LooksNormal; ChangeHistory read the real log. Not exercised on the
  bench: Undo on a real change, watcher Switch off from the UI, update bar end-to-end (needs a newer release than the
  installed one), silent downgrade refusal.

## Part 9: UI redesign (2026-11-14, owner moved UI work to the overnight session; UI session told to stay out)
Done on `perf` (pushed): frosted glass (Windows 11 acrylic, Theme/DarkGlass.xaml layered over Dark.xaml, solid
fallback on Windows 10 / high contrast / transparency off), no bold type anywhere (Display = Light 24, Title = Normal 19),
native caption buttons when glass is on, smoother hover/page motion, sidebar "This computer" (SystemSummary, compact
icon + short value) and "Recent activity" (MainWindow.Sidebar.cs), Settings in its own panel sliding over any page
(gear button in the sidebar, Esc closes; MainWindow.Extras.cs BuildSettings/SetSettingsOpen), History page without
settings, activity line during checks that unfolds into all steps (MainWindow.Activity.cs + core Activity feed from
g12-activity). Test switch: AUTOFIXSCOUT_OPEN=settings | graphics | graphics-log. Screenshots: scratchpad shot.ps1
(PrintWindow when the app is not in front; NEVER SendKeys: the owner types in other windows).
Next (owner's requests, in order):
1. Buttons with presence: hover scale + soft glow (accent glow on AccentButton), press: ripple from the click point
   (Theme/Ripple.cs, RippleAdorner.Play) + spring back (ElasticEase). Wire in Motion.OnPressChanged.
2. iOS-style glass: soft drifting colour field (3 blurred gradient blobs) behind everything; cards with brighter
   frosted fill, top specular edge, soft shadow.
3. More settings (AppSettings in core): theme dark/light/system, glass on/off, accent colour, text size, reduce
   motion, activity list unfolded by default, clear history, open data folder.
4. Build, test, screenshots; then ask the owner 0.3.1 vs 0.4.0 (UI + activity feed + Afterburner fix => 0.4.0
   recommended); check the UI session has nothing to push; release; then propose next-release ideas.
- 2026-11-14 07:03 IST: v0.3.1 released (owner chose 0.3.1 over 0.4.0). PR 14 merged.

## Ideas for the next release (proposed 2026-11-14; owner said keep working, stop before the 11:30 reset)
Capabilities:
- Battery health (laptops): design vs full-charge capacity and cycle count (powercfg /batteryreport /xml, read-only),
  plain wear level and advice. [started: agent g13-battery]
- Connection quality: latency, jitter and packet loss to the gateway and to the internet, separately for Wi-Fi, so
  "internet is slow / games lag online" gets an answer (Wi-Fi vs provider). [started: agent g14-netquality]
- Blue screen explainer: stop code + the driver named in the crash report, in plain words, with what to do.
- Drive wear: SSD wear %, temperature and read errors from the storage reliability counters.
- Slow start-up through the elevated helper (the boot-time log needs admin).
- Game settings check: Game Mode, hardware-accelerated GPU scheduling, variable refresh rate, per-game GPU choice.
Quality of life:
- Keyboard shortcuts: Ctrl+K search, Ctrl+, settings, Esc back. Remember window size and position. [started here]
- Optional weekly check-up from the tray, with a notification only when something is wrong.
- Save a report as a file (HTML) to send to a helper.
- First-run tour (three short cards) for new users.
