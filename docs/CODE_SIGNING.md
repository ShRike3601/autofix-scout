# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## What is signed

Only files built by GitHub Actions from the source code in this repository
([.github/workflows/build.yml](../.github/workflows/build.yml)) are signed: the AutoFix Scout app, its administrator
helper (`AutoFixScout.Executor.exe`) and the installer. Nothing built on a personal computer is signed. Every release
must be approved by hand before it is signed.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [ShRike3601](https://github.com/ShRike3601) |
| Approvers | [ShRike3601](https://github.com/ShRike3601) |

Changes from anyone outside this list are reviewed by a committer before they are merged. Build scripts and the
workflow file are reviewed like any other source code. All members use multi-factor authentication on GitHub and
SignPath.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user
or the person installing or operating it.

The only network connections it makes are: Microsoft's connection test address (`www.msftconnecttest.com`) and a DNS
lookup of it, to check whether the internet works, and, only when the user chooses that repair, a download of the
Microsoft Visual C++ runtime installer from `aka.ms`. No personal data, device data or usage data is sent.

## System changes

AutoFix Scout changes Windows settings only after the user selects a fix. Changes that need administrator rights
are made by `AutoFixScout.Executor.exe`, which Windows asks the user to approve each time, and which accepts only a
fixed list of repairs. Where possible the previous state is saved first and restored if the fix does not help.
The installer has a matching uninstaller (Settings > Apps > Installed apps > AutoFix Scout > Uninstall).
