# What AutoFix Scout can do

Every check is read-only first. Every fix is offered one at a time, least risky first, with the person's consent, and
followed by a real test. Admin fixes go through the elevated executor, which accepts only fixed actions and validated
arguments. System changes are backed up first; a restore point is attempted before system-wide repairs.

Status: **Done** = built, unit-tested and checked on the test laptop (every admin repair also ran on the laptop through the elevated test runner);
**Planned** = on the overnight roadmap (docs/HANDOFF.md).

## Ways in
| | Status |
|---|---|
| Big buttons: sound, camera, internet | Done |
| "Something else": 14 more problem areas | Done |
| "Describe the problem in your own words" (offline keyword routing, explains its choice) | Done |
| History and reports (recent checks, local reports and backups) | Done |

## Problem areas
| Area | Finds | Fixes | Status |
|---|---|---|---|
| Sound | muted, wrong output, audio services, device off/removed/error, recent bad driver | unmute, choose output, restart services, restart/re-detect device, driver rollback or Windows Update driver with save point and auto-undo | Done |
| Camera | privacy switches, device off/removed/error, app holding the camera | Settings steps, enable/restart/re-detect, drivers as above | Done |
| Internet | airplane mode, Wi-Fi off/disconnected, adapter off/error, bad DNS, **dead proxy, hijacked hosts file, wrong clock, network add-ons, network services off** | Settings steps, enable/restart adapter, flush/reset DNS, proxy reset, hosts cleanup, clock sync, Winsock reset, service defaults | Done |
| Microphone | no/disabled mic, privacy, wrong default, muted, very low level | unmute and raise level, Settings steps, services | Done |
| Bluetooth | adapter missing/off/error, service stopped, radio off, nothing paired, headphones paired but not connected | start/restore service, enable/restart adapter, re-detect, guided pairing | Done |
| Printer | spooler, no printer, offline, paused, error, stuck jobs | restart spooler, clear queue, set online, Settings steps | Done |
| Screen and display | display adapter error, second screen not shown, very low brightness | extend display, brightness, Settings steps | Done |
| Slow or full computer | low disk, large temp files, memory pressure, many startup apps, **busy programs**, long uptime | clear temp and Recycle Bin, **switch off startup apps like Task Manager**, component store cleanup, Settings steps | Done |
| Windows Update | services off, restart pending, low space, out of date, **repeated failures** | service defaults, restart services, **Update reset (folders renamed)**, DISM/SFC | Done |
| Full computer check-up | program crashes with likely cause, blue screens, disk/hardware errors, missing runtimes, failing services, wrong clock, proxies, hosts, network add-ons, damaged PATH, missing TEMP, switched-off Windows services, damaged Store apps, pending restart | DISM, SFC, chkdsk scan, clock sync, proxy/hosts/Winsock, service defaults, PATH repair, TEMP repair, Store app re-register, Explorer restart, icon cache, search index, Visual C++ runtime (Microsoft-signed) | Done |
| A program keeps closing | the person picks the program; cause from the crash record (runtime, missing file, .NET, graphics driver, Windows files, the program) | Visual C++ runtime, graphics driver via Windows Update, repair/reinstall the program, system file repair | Done |
| Start menu, taskbar or search | shell crashes, damaged shell packages, Windows Search off | restart Start/search, restart Explorer, re-register shell packages, search index, icon cache, DISM/SFC | Done |
| Microsoft Store and app installs | Store packages, Store services, wrong clock | Store cache reset, re-register, service defaults, troubleshooter | Done |
| Word, Excel or Outlook | Click-to-Run Office present | Office Quick Repair (Microsoft-signed tool only), Online Repair guide | Done |
| OneDrive not syncing | OneDrive installed / running | start OneDrive, reset OneDrive (files kept) | Done |
| Security, viruses or pop-ups | antivirus missing/off, real-time protection off, old signatures, firewall off, UAC off, SmartScreen off, hosts/proxy hijacks, suspicious startup entries | signature update, quick scan, firewall on, UAC on, Windows Security steps | Done |
| Drive health (in the full check-up) | drive health and failure prediction reported by Windows (wear counters need admin: later) | back up first (Windows Backup), repair-service advice | Done |
| Battery, sleep and power | battery wear, sleep never on battery, PC waking by itself (wake sources), high-performance plan on battery, Fast Startup | Balanced plan, power and battery saver settings, guided wake and Fast Startup steps | Done |
| Any device with a warning (in the full check-up) | Device Manager error codes explained in plain words (no driver, cannot start, stopped by Windows...) | re-detect devices, optional driver updates, maker's driver, USB replug; drivers never changed | Done |
| Network extras (in the internet check) | no address from the router, weak Wi-Fi signal, IP address conflict, metered connection | renew address (admin, one adapter), guided router and Wi-Fi steps, metered setting | Done |
| Temporary user profile (in the full check-up) | "my desktop and files are gone" (temporary profile, profile service errors) | restart and sign in again, guided recovery for a helper; profiles never touched | Done |
| Keyboard, mouse, touchpad | touchpad switched off, Sticky/Filter/Toggle/Mouse Keys on, missing or failing input devices | touchpad on, accessibility keys off (backed up), re-detect devices, guided steps | Done |
| Clock settings (in the full check-up) | wrong clock with "Set time automatically" off | Date and time settings step, then clock sync | Done |
| Browser taken over by policies ("managed by your organization") | forced extensions, homepage, search engine, browser proxy | remove the policy values (backed up) | Planned (Q1) |
| What changed before the problem | programs, updates and drivers installed in the last 7 days | uninstall guidance, System Restore as a last resort (guided) | Planned (Q2) |
| Graphics driver resets | display driver resets and freezes | driver update guidance | Planned (Q3) |
| One app has no sound | app muted or at zero in the Volume mixer, audio enhancements | unmute that app, guided | Planned (Q4) |
| What fills the drive | Windows.old, hibernation file, Downloads, update caches | Storage Sense, component cleanup, guided (personal files never deleted) | Done |

## Safety rules the code enforces
- The UI never runs elevated; the executor accepts only allowlisted action ids with fixed or validated arguments.
- Display and storage drivers are never touched; driver changes have save points and automatic undo.
- Nothing is deleted that cannot be recreated: hosts lines are commented out, update folders renamed, settings backed
  up to `C:\ProgramData\AutoFixScout\backups` or `%LOCALAPPDATA%\AutoFixScout\backups`.
- Downloads and Office's repair tool run only with a valid Microsoft signature.
- Unknown is never treated as healthy (fail closed); every sentence follows docs/WRITING.md.
