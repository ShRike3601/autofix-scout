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
| Connection quality | measures delay, jitter and packet loss to the router and to two public servers for about 15 seconds, and tells whether a slow or laggy connection comes from the Wi-Fi or home network (more than 2 percent lost, jitter above 15 ms, or above 20 ms delay on Wi-Fi) or from the internet connection (more than 2 percent lost, delay above 80 ms or jitter above 30 ms); a router that does not answer ping is reported as unknown, never as a fault | Guided steps only: move closer, 5 GHz, pause downloads, check the cable, restart the router, contact the provider with the numbers (nothing is changed); no addresses or network names are kept | Done (UI pending) |
| Microphone | no/disabled mic, privacy, wrong default, muted, very low level | unmute and raise level, Settings steps, services | Done |
| Bluetooth | adapter missing/off/error, service stopped, radio off, nothing paired, headphones paired but not connected | start/restore service, enable/restart adapter, re-detect, guided pairing | Done |
| Printer | spooler, no printer, offline, paused, error, stuck jobs | restart spooler, clear queue, set online, Settings steps | Done |
| Screen and display | display adapter error, second screen not shown, very low brightness | extend display, brightness, Settings steps | Done |
| Computer or games running slowly | processor slowed by heat or power (counters, firmware limit events), power saver and battery, old BIOS, Intel 13th/14th gen microcode, memory below rated speed or single channel, little memory, blue screens and hardware errors | Settings steps, cooling, BIOS update, XMP and overclock guidance (steps for the person, nothing is changed) | Done |
| Graphics card and games | overheating or slowdown (NVIDIA), driver resets with or without an MSI Afterburner overclock at startup, narrow PCIe link, power brake, monitor plugged into the motherboard (desktop), old driver, Resizable BAR off (NVIDIA only; AMD and Intel stay Unknown) | human steps (reset Afterburner, clean driver install, reseat card, cable, BIOS Resizable BAR, cleaning), Graphics settings page; read-only | Done |
| Short load test (opt-in) | about 2 minutes: processor load then graphics card load with one reading per second; finds a processor that slows down (speed limit below 80 percent for 5 seconds, with time), a graphics card that overheats (87 degrees Celsius or heat slowdown, with time and temperature), power brake, clocks far below maximum, and a test that stopped for safety; NVIDIA only for the card (others stay Unknown, and the card load needs Direct3D 11) | same human steps as the performance and graphics checks; changes no setting; refuses on battery, stops at 90 degrees Celsius, below half processor speed for 5 seconds, on cancel and after 2 minutes | Done (UI pending) |
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
| Battery, sleep and power | battery wear, battery health from the Windows battery report (capacity against new, charge cycles; replacement advised at 40 percent wear or more, ageing noted at 20 percent), sleep never on battery, PC waking by itself (wake sources), high-performance plan on battery, Fast Startup | Balanced plan, power and battery saver settings, guided wake and Fast Startup steps | Done |
| Any device with a warning (in the full check-up) | Device Manager error codes explained in plain words (no driver, cannot start, stopped by Windows...) | re-detect devices, optional driver updates, maker's driver, USB replug; drivers never changed | Done |
| Network extras (in the internet check) | no address from the router, weak Wi-Fi signal, IP address conflict, metered connection | renew address (admin, one adapter), guided router and Wi-Fi steps, metered setting | Done |
| Temporary user profile (in the full check-up) | "my desktop and files are gone" (temporary profile, profile service errors) | restart and sign in again, guided recovery for a helper; profiles never touched | Done |
| Keyboard, mouse, touchpad | touchpad switched off, Sticky/Filter/Toggle/Mouse Keys on, missing or failing input devices | touchpad on, accessibility keys off (backed up), re-detect devices, guided steps | Done |
| Clock settings (in the full check-up) | wrong clock with "Set time automatically" off | Date and time settings step, then clock sync | Done |
| Browser taken over by policies ("managed by your organization") | forced extensions, homepage, search engine, browser proxy | remove the policy values (backed up, admin step covers both machine and own-account rules) | Done |
| What changed before the problem | programs, updates and drivers installed in the last 7 days | uninstall guidance, System Restore as a last resort (guided) | Done |
| Graphics driver resets | display driver resets and freezes | driver update guidance | Done |
| One app has no sound | app muted or at zero in the Volume mixer, audio enhancements | unmute that app, guided | Done |
| What fills the drive | Windows.old, hibernation file, Downloads, update caches | Storage Sense, component cleanup, guided (personal files never deleted) | Done |
| One app cannot use the microphone or camera | per-app privacy switch off for Teams, Zoom, a browser... | guided Settings step naming the app | Done |
| Blue-screen causes | stop code explained in plain words and grouped (driver, memory, graphics, disk, damaged files, hardware); the driver Windows named, in friendly words, when a report records one (reports can need administrator rights); whether it keeps happening (days, same code); sources: System log 1001, Kernel-Power 41 stop code, Windows Error Reporting reports, Minidump file count (never opened) | memory test (guided), update or reinstall the named driver, clean graphics driver install, heat and overclock advice for 0x124, disk scan, system file repair | Done |
| Virtual memory | page file missing or too small | let Windows manage the page file (admin, restart) | Done |
| Threats found by Microsoft Defender | recent detections, active or removed | Protection history, full scan, Defender Offline (guided) | Done |
| Scanner | image acquisition service off, scanner errors | restore service, re-detect, guided | Done |
| Wi-Fi keeps dropping | frequent disconnects (sleep and shutdown excluded), adapter power saving | guided | Done |
| Programs will not install | Windows Installer off, install failures explained, restart pending | service default, guided | Done |
| Notifications missing | notifications or app notifications off, Do not disturb on | guided, switch back on | Done |
| USB devices disconnecting | USB errors and frequent reconnects (beyond starts and wakes), selective suspend | guided | Done |
| Screen looks wrong | resolution below native, low refresh rate, unusual scaling | guided | Planned (G1) |
| Slow start-up | boot and sign-in time, slow start-up items | guided, startup apps | Planned (G2) |
| Keyboard types wrong characters | wrong or several keyboard layouts, switch hotkeys | guided | Planned (G3) |
| Sound gets quieter during calls | communications ducking, separate call device | switch ducking off (backed up), guided | Planned (G4) |
| Wi-Fi drops on a campus or office network | enterprise (802.1X) network, sign-ins that cannot reconnect by themselves, old Wi-Fi driver, adapter settings that drop on busy networks | save-sign-in guide, adapter tuning (admin: 802.11ac, gentler roaming, no radio power saving, prefer 5 GHz; backed up, automatic undo if no reconnect in 4 minutes), driver update guide, no router steps on campus | Done |

## Safety rules the code enforces
- The UI never runs elevated; the executor accepts only allowlisted action ids with fixed or validated arguments.
- Display and storage drivers are never touched; driver changes have save points and automatic undo.
- Nothing is deleted that cannot be recreated: hosts lines are commented out, update folders renamed, settings backed
  up to `C:\ProgramData\AutoFixScout\backups` or `%LOCALAPPDATA%\AutoFixScout\backups`.
- Downloads and Office's repair tool run only with a valid Microsoft signature.
- Unknown is never treated as healthy (fail closed); every sentence follows docs/WRITING.md.
