# Changelog

This file records build-by-build changes for `Settings File Guard`.

Historical commits before `2026-03-27` were not backfilled into this file.

## 2026-03-27

### Fixed

- Cached the Cities: Skylines II settings directory during startup so shutdown recovery no longer calls `Application.persistentDataPath` during `ProcessExit`.
- Initialized `GuardPaths` in `OnLoad` before diagnostics and shutdown recovery begin.

### Verified

- Validated repeated shutdown recovery runs after disabling deep diagnostics; final `Settings.coc` remained healthy through multiple close/relaunch tests.
- Refreshed the local deployment from the current source tree using a direct Roslyn `csc.exe` build because `dotnet build` continues to time out in this environment.
