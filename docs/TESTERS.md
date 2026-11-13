# Testing AutoFix Scout

Thank you for helping to test AutoFix Scout, an early version of an application that finds and resolves common
Windows problems: sound, camera, microphone, internet and Wi-Fi, Bluetooth, printers, the screen, keyboard and mouse,
a slow or full computer, Windows Update, start-up, and damaged Windows files or apps. This build is not yet digitally signed, so Windows will show a
warning before it runs.

Code signing policy: free code signing provided by SignPath.io, certificate by SignPath Foundation (applied for). See
[CODE_SIGNING.md](https://github.com/ShRike3601/autofix-scout/blob/main/docs/CODE_SIGNING.md).

## Requirements

- A Windows 10 or Windows 11 PC or laptop (Intel, AMD or ARM; the installer chooses the right version).
- An administrator account on the PC. Some fixes ask Windows for permission.
- No other software is needed.

## Installation

1. Download `AutoFixScout-Setup-<version>.exe` from the release page.
2. Open the file. If Windows shows "Windows protected your PC", select **More info**, then **Run anyway**.
   Your antivirus software may also ask for confirmation.
   If Windows shows "Smart App Control blocked an app that may be unsafe" instead, there is no way to run this
   build on that PC: Smart App Control blocks every app that is not digitally signed. Please test on another PC
   until signed releases are available. Turning Smart App Control off is not recommended, because on many PCs it
   cannot be turned on again without resetting Windows.
3. Select **Yes** when Windows asks whether the installer may make changes, then follow the steps.
4. Start AutoFix Scout from the Start menu (or the desktop shortcut, if you chose one).

When AutoFix Scout needs to change a setting that requires administrator rights, Windows asks "Do you want to allow
this app to make changes to your device?". Select **Yes** to continue, or **No** to skip that fix.

To remove it: Settings > Apps > Installed apps > AutoFix Scout > **Uninstall**.

## Problems you can safely create

Create one problem, select the matching option in AutoFix Scout, and follow the instructions. If the problem is not
resolved, undo it yourself as shown.

| Problem | How to create it | How to undo it |
|---|---|---|
| Sound muted | Mute the volume from the speaker icon on the taskbar | Unmute it |
| Wrong sound output | Connect headphones, HDMI or a Bluetooth speaker, make it the default, then disconnect or turn it off | Settings > System > Sound > choose your speakers |
| Sound device turned off | Device Manager > Sound, video and game controllers > your sound device > right-click > **Disable device** | Right-click > **Enable device** |
| Sound device removed | Same place > right-click > **Uninstall device** (leave "Attempt to remove the driver" unticked) | Device Manager > Action > **Scan for hardware changes** |
| Camera privacy | Settings > Privacy & security > Camera > turn off "Camera access" | Turn it back on |
| Camera turned off | Device Manager > Cameras (or Imaging devices) > your camera > **Disable device** | **Enable device** |
| Camera removed | Same place > **Uninstall device** (driver not removed) | **Scan for hardware changes** |
| External webcam unplugged | Unplug a USB webcam | Plug it back in |
| Airplane mode | Turn on Airplane mode | Turn it off |
| Wi-Fi off | Turn off Wi-Fi from the taskbar | Turn it back on |
| Wi-Fi adapter turned off | Device Manager > Network adapters > your Wi-Fi adapter > **Disable device** | **Enable device** |
| Microphone muted | Settings > System > Sound > your microphone > turn the volume to 0 or mute it | Turn it back up |
| Bluetooth off | Turn off Bluetooth from the taskbar | Turn it back on |
| Dead proxy server | Settings > Network & internet > Proxy > Manual setup > turn on, address `127.0.0.1`, port `9` > Save | Turn "Use a proxy server" off |
| Windows Search off | Run `services.msc`, open "Windows Search", set Startup type to **Disabled**, select **Stop** | AutoFix Scout restores it, or set it back to **Automatic (Delayed Start)** and **Start** |
| A program that keeps closing | Use any program that crashes for you; AutoFix Scout reads the crashes Windows recorded | - |
| Startup apps | Install something that starts with Windows (Steam, Discord, Spotify) | Task Manager > Startup apps > enable it again |

Please do **not** tick "Attempt to remove the driver for this device", delete system files, or test on a work or
school PC that you do not own.

## What to send back

For each test: the problem you created, what AutoFix Scout said, whether it resolved the problem, and anything that
was unclear. If it was not resolved, select **Details for my helper** and send that report file. AutoFix Scout keeps
everything on your PC and uploads nothing. Reports list device names and identifiers only (no passwords, files or
network names).

Your data is stored in `%LOCALAPPDATA%\AutoFixScout`; delete that folder to remove it. Driver save points, if any,
are in `C:\ProgramData\AutoFixScout`.
