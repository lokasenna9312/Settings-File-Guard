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
- Broadened `continue_game.json` path matching so shutdown deletion events reach the new continue-target restore path reliably.

### Verified

- Validated repeated shutdown recovery runs after disabling deep diagnostics; final `Settings.coc` remained healthy through multiple close/relaunch tests.
- Refreshed the local deployment from the current source tree using a direct Roslyn `csc.exe` build because `dotnet build` continues to time out in this environment.
- Built and deployed the new settings-UI deep diagnostics toggle with the same Roslyn-based local build flow.

## 2026-03-28

### Added

- Added a pre-main-menu launcher continue retry path that captures the startup `LoadGame` target and replays it during the unwind before the normal main menu appears.
- Expanded that retry path into a short bounded retry window so repeated startup load failures can keep retrying the same save target until the menu flow is finally allowed through.

### Fixed

- Disabled the experimental pre-main-menu launcher-continue retry path after it proved ineffective in this environment and started provoking `MenuUISystem.ExitToMainMenu()` null-reference failures during launcher startup.
- Refreshed the PDX publish metadata so the public-facing description now matches the current feature set, including `continue_game.json` protection, the in-game deep-diagnostics toggle, and the explicit launcher-continue limitation.
- Tightened `continue_game.json` health validation so semantically broken metadata, including implausible `1970-01-01` timestamps, no longer counts as healthy just because the file still looks like JSON.
- Added timestamp normalization for repairable `continue_game.json` files so backup and restore flows can rewrite the `date` field from the file's last-write time instead of preserving clearly invalid launcher metadata.
- Changed the startup continue retry success criterion to `onWorldReady` instead of trusting `Load()` task completion alone, so menu fallbacks after a superficially successful load can still stay inside the pre-main-menu retry gate.
- Stopped treating the initial main-menu startup lifecycle as a successful launcher continue load, so pre-main-menu retries now stay armed until an actual `LoadGame` path reports world readiness.
- Added tracking for `GameManager.Load(..., AsyncReadDescriptor, Hash128, Guid)` so launcher continue attempts that bypass the simpler `Load(Hash128)` hooks can still be identified and retried before the menu fully takes over.
- Extended the retry gate to `GameManager.OnMainMenuReached(Purpose.LoadGame, GameMode.Game)` so failed launcher continue loads that bypass `MainMenu()` interception can still be retried before the menu flow settles.
- Primed launcher-continue retries from `GameManager.configuration` and the latest matching `.cok.cid`, so retry fallback can still target the right save even when the launcher startup load never surfaces through the observed managed `Load(...)` hooks.
