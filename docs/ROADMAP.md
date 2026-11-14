# AutoFix Scout roadmap (proposed 2026-11-14)

Released: 0.3.1. Collected for the next release on branch `next` (draft PR 15): battery health, connection quality,
blue-screen explainer, game settings, drive health and the optional administrator reads, weekly check-up, HTML helper
report, first-run tour, keyboard shortcuts.

## Next: from advice to one-click fixes
Many new checks only explain what to do. The next step is safe, undoable fixes for the simple cases.
1. One-click game settings fixes (per user, undoable from History): turn Game Mode on, set a game back to the graphics
   card, turn off background recording. [done on branch g21-gamefix: games.game_mode_on, games.use_graphics_card, games.background_recording_off, undo from History]
2. Driver updates from Windows Update for old Wi-Fi and graphics drivers (the executor already talks to Windows
   Update for drivers), with the existing driver save point and automatic undo.
3. Start-up clean-up driven by the measured boot data: offer to switch off the apps Windows named as slow.
4. Component store clean-up (DISM /StartComponentCleanup) for a full drive, through the elevated helper.

## Trust and support
5. AutoFix Scout's own problem log: unexpected errors in the app itself are written to a local log and included in the
   helper report, so testers' problems can be fixed. [DONE, merged into next]
6. A "beta" update channel in Settings for friends who test early builds (pre-releases).
7. Code signing (SignPath Foundation, applied for) so Smart App Control stops blocking the installer.

## Wider coverage
8. Browser problems: unwanted search engine or start page, extensions installed by programs, policy hijacks (read-only
   first, then reset per browser).
9. Sound crackles and drop-outs: check for high DPC latency and the drivers that cause it.
10. DNS check: compare the current DNS server with two public ones and offer a faster one (the person chooses).
11. Hindi and other languages (all wording is already in one style, which makes translation simpler).

## Look and feel
12. Light theme with glass, and a high-contrast pass with a screen reader.
13. A small tray icon that shows the Wi-Fi watcher and weekly check-up state.
14. A search box that also finds settings and history entries (Ctrl+K).
