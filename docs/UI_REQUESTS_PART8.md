# UI requests, part 8 (for the UI session)

Branch `perf` (pushed). Everything below is core code that is finished and tested; it only needs screens. Wording
follows docs/WRITING.md. Please merge `origin/perf` into your branch, wire these, run all tests, push, and send the
overnight session the commit hash. Tiles are one Problem line each in `Categories()` in `MainWindow.Visuals.cs`.

## 1. New problem tiles
| Category | Label | Caption | Handler | Topic |
|---|---|---|---|---|
| Battery and performance | Computer or games running slowly | Games stutter, the computer got slow, or the fans are loud. | `PerfFlow.DiagnoseAsync` (runs ~4 s, already off the UI thread) | `PerfFlow.Topic` = "performance" |
| Display and camera (or Battery and performance) | Games stutter or crash | Graphics card, driver and overclock check | `GraphicsFlow.DiagnoseAsync` | `GraphicsFlow.Topic` = "graphics" |
| Battery and performance | Short load test | Checks whether the computer slows down under full load (about 2 minutes). | see 2 | `StressFlow.Topic` = "stress" |

`What` and `TestQuestion` exist on each Flow, like the other areas. The free-text router already routes to
"performance" and "graphics".

## 2. Short load test (stress test) screens
1. Consent: show `StressFlow.Consent` (or the `StressFlow.ConsentBullets` list); buttons "Start test" / "Not now".
   Nothing starts before "Start test".
2. Progress: `var cts = new CancellationTokenSource(); var progress = new Progress<StressSample>(s => ...);`
   `var d = await StressFlow.DiagnoseAsync(progress, cts.Token);` (about 2 minutes).
   Phase text: Idle "Measuring the idle state", Cpu "Loading the processor", Gpu "Loading the graphics card",
   Cooldown "Cooling down". Show `s.Seconds` (of about 105), `s.CpuLimitPercent` (processor speed limit, "n/a" when
   null), `s.GpuTemperatureC` ("n/a" when null). Note: "Please do not unplug the charger." A "Stop" button calls
   `cts.Cancel()`.
3. Results: the normal findings + ladder screen from `d`; "Run the test again" as the re-test.

## 3. Update banner (`using AutoFixScout.Updates;`, one `UpdateService` for the app's lifetime)
On startup, off the UI thread: `var info = await updates.CheckAsync();` (never throws; null when offline, turned
off, or a dev build). If not null: "Version {info.Version} is ready." with "Update and restart" and "Not now".
On "Update and restart": progress, `var path = await updates.DownloadAsync(info, new Progress<double>(p => ...));`
- null: "The update could not be downloaded. Please try again later."
- else: "Windows will ask for your permission to install the update." then
  `if (updates.StartSetup(path)) Application.Current.Shutdown(); else` "The update was not installed."
Never download or start without the click.

## 4. What's new (once per version)
At startup: `var wn = new WhatsNew().TakeOnce();` If not null: panel "What is new in version {wn.Version}" with
`wn.Text` (plain text) and "Close". It records itself.

## 5. History with Undo
`var list = new ChangeHistory().List();` Each `ChangeEntry`: `WhenUtc`, `Description`, `CanUndo`, `Undone`, `Hint`.
"Undo" button only when `CanUndo`; "Undone" when `Undone`; otherwise `Hint` in a muted line.
Click: `var r = await new ChangeHistory().UndoAsync(entry.StepId);` show `r.Message`, reload. Before a driver undo say
that Windows will ask for permission and the previous driver will be restored.

## 6. Settings
- "Check for updates when AutoFix Scout starts": `var s = AppSettings.Load(); s.CheckForUpdates` / `s.Save()`.
  Helper text: "AutoFix Scout asks github.com once at start whether a newer version exists. No personal data is sent."
- Wi-Fi watcher: show "The Wi-Fi watcher is on. It reconnects your Wi-Fi automatically." when
  `WifiWatcherSetup.ReadState() == Tri.Yes`, with "Switch off" ->
  `FixActions.RunAsync(new Rung("wifi_watch_off", "network.wifi_watch_off", 1, ""))`. (Switching it on is a normal
  rung in the Wi-Fi ladder.) Optional: `new WifiWatchStore().Summarize(DateTime.UtcNow)`.
- "Show a notification when AutoFix Scout reconnects Wi-Fi": `WifiWatcherSetup.GetNotify()` / `SetNotify(bool)`.
