# Changelog

This file records build-by-build changes for `Settings File Guard`.

Historical commits before `2026-03-27` were not backfilled into this file.

## 2026-03-27

### Added

- Added a persisted mod options toggle for deep diagnostics so the feature can be enabled or disabled from the in-game settings UI instead of only through a marker file.
- Added English and Korean option text for the new deep diagnostics setting.
- Expanded the deep diagnostics option text to all 12 Cities: Skylines II supported UI locales so the setting no longer falls back to English outside the initial two languages.
- Added best-effort `continue_game.json` protection that captures a healthy launcher continue-target backup and restores it during shutdown if the file disappears after a valid save.

### Fixed

- Cached the Cities: Skylines II settings directory during startup so shutdown recovery no longer calls `Application.persistentDataPath` during `ProcessExit`.
- Initialized `GuardPaths` in `OnLoad` before diagnostics and shutdown recovery begin.
- Switched deep diagnostics enablement to read the loaded mod setting during runtime while still honoring the legacy marker file as the initial default for migrated installs.
- Removed the legacy deep-diagnostics marker-file migration so the setting now uses the options UI only.
- Kept deep diagnostics opt-in by default so normal releases do not accumulate per-session deep-diagnostics logs and snapshots unless the user explicitly enables them.

### Verified

- Validated repeated shutdown recovery runs after disabling deep diagnostics; final `Settings.coc` remained healthy through multiple close/relaunch tests.
- Refreshed the local deployment from the current source tree using a direct Roslyn `csc.exe` build because `dotnet build` continues to time out in this environment.
- Built and deployed the new settings-UI deep diagnostics toggle with the same Roslyn-based local build flow.
