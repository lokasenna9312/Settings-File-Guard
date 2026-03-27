# Changelog

This file records build-by-build changes for `Settings File Guard`.

Historical commits before `2026-03-27` were not backfilled into this file.

## 2026-03-27

### Fixed

- Cached the Cities: Skylines II settings directory during startup so shutdown recovery no longer calls `Application.persistentDataPath` during `ProcessExit`.
- Initialized `GuardPaths` in `OnLoad` before diagnostics and shutdown recovery begin.
