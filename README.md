# AutoFix Scout

AutoFix Scout is a free, open-source Windows app that finds and fixes common computer problems in plain language,
for people who are not technical. It checks the computer, explains what it found, and offers fixes one step at a time.
Every change that needs administrator rights asks Windows for permission first, and changes that can be undone are
undone automatically when they do not help.

**Download:** [latest release](https://github.com/ShRike3601/autofix-scout/releases/latest) (one installer for Intel,
AMD and ARM PCs running Windows 10 or 11). Testing instructions: [docs/TESTERS.md](docs/TESTERS.md).

## What it can fix

- Sound and microphone: muted or wrong device, disabled or missing drivers, the audio service, call ducking.
- Internet, Wi-Fi and Bluetooth: no connection, DNS and proxy problems, Wi-Fi that keeps dropping (including old
  drivers and campus or office networks), Bluetooth devices that do not pair.
- Display and camera: wrong resolution or scaling, camera privacy switches and drivers.
- Keyboard, mouse and touchpad: accessibility keys left on, touchpad turned off, wrong keyboard layout.
- Printers, a slow or full computer, slow start-up, Windows Update, damaged Windows files, and apps that fail to start.

The full list is in [docs/CAPABILITIES.md](docs/CAPABILITIES.md).

## Privacy

AutoFix Scout runs entirely on your computer. It has no account, no telemetry and no advertising, and it sends no
information about you or your computer anywhere. To test the internet connection it contacts Microsoft's connection
test address (`www.msftconnecttest.com`), the same one Windows itself uses. When you choose to repair the Microsoft
Visual C++ runtime, it downloads Microsoft's installer from `aka.ms`. Its history, reports and undo data are stored
only on the computer (`%LOCALAPPDATA%\AutoFixScout` and `%ProgramData%\AutoFixScout`).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org). (Applied for; until it is granted, releases are unsigned.)

Details, team roles and the privacy statement: [docs/CODE_SIGNING.md](docs/CODE_SIGNING.md).

## Building from source

Requirements: Windows and the .NET 8 SDK.

```powershell
dotnet test AutoFixScout.sln -c Release
dotnet publish src/AutoFixScout.UI/AutoFixScout.UI.csproj -c Release -r win-x64 --self-contained true -o publish
```

Release installers are built only by GitHub Actions from this repository
([.github/workflows/build.yml](.github/workflows/build.yml)) when a version tag is pushed.

## Licence

[MIT](LICENSE). Third-party components keep their own licences (all open source; NAudio and the .NET libraries are MIT).
