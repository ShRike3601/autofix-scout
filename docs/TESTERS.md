# Testing AutoFix Scout (for friends)

Thanks for helping! AutoFix Scout is an early test version of an app that finds and fixes common sound, camera and
internet problems on Windows. It's **not signed yet**, so Windows will warn you about it.

## What you need

- Windows 10 or 11. Use `AutoFixScout-win-x64.zip` on normal Intel/AMD PCs and laptops,
  `AutoFixScout-win-arm64.zip` on ARM laptops (Snapdragon / "Copilot+ PC"). Not sure? Settings > System > About >
  "System type": "x64-based" or "ARM-based".
- An administrator account on the PC (some fixes ask Windows for permission).
- Nothing else to install.

## Start it

1. Unzip the whole folder somewhere (e.g. Desktop). Don't run it from inside the zip.
2. Double-click `AutoFixScout.UI.exe`.
3. "Windows protected your PC": click **More info** > **Run anyway**. Your antivirus may also ask; allow it.
4. When the app wants to change something that needs admin rights, Windows asks "Do you want to allow this app to make
   changes?". Click **Yes** if you want it to go ahead.

## Safe problems to try

Make one problem, run the matching button, follow the app, and see whether it fixes it. Undo it yourself if it doesn't.

| Try | How to make it | How to undo it yourself |
|---|---|---|
| No sound: muted | Mute with the speaker icon in the taskbar | Unmute |
| No sound: wrong output | Plug in headphones/HDMI/Bluetooth speaker and make it the default, then unplug or switch off | Sound settings > choose your speakers |
| No sound: device off | Device Manager > Sound, video and game controllers > your sound device > right-click > **Disable device** | Right-click > **Enable device** |
| Camera: privacy | Settings > Privacy & security > Camera > turn "Camera access" off | Turn it back on |
| Camera: device off | Device Manager > Cameras (or Imaging devices) > your camera > **Disable device** | **Enable device** |
| Camera: external webcam | Unplug a USB webcam | Plug it back in |
| Internet: airplane mode | Turn on Airplane mode | Turn it off |
| Internet: Wi-Fi off | Turn Wi-Fi off in the taskbar | Turn it on |
| Internet: Wi-Fi adapter off | Device Manager > Network adapters > your Wi-Fi > **Disable device** | **Enable device** |

**Please don't** uninstall drivers ("Uninstall device" with "Attempt to remove the driver" ticked), delete system
files, or try this on a work/school PC you don't own. "Uninstall device" *without* removing the driver is OK: if the
app doesn't fix it, Device Manager > Action > **Scan for hardware changes** brings it back.

## What to send back

For each try: what you broke, what the app said, whether it fixed it, and anything confusing. If it didn't fix it,
click the button to open the helper report and send that file. The app keeps everything on your PC; it doesn't upload
anything. Reports list your devices' names and IDs (no passwords, no files, no network names).

Your data is in `%LOCALAPPDATA%\AutoFixScout` (delete the folder to remove it). Driver save points, if any, are in
`C:\ProgramData\AutoFixScout`.
