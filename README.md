# Settings File Guard

Settings File Guard is a standalone Cities: Skylines II utility mod that protects the global `Settings.coc` file from known keybinding-save corruption.

이 모드는 Cities: Skylines II의 전역 설정 파일 `Settings.coc`를 보호하기 위한 독립 유틸리티 모드입니다.

## Core Behavior

- Suppresses the known `Game.Settings.KeybindingSettings.bindings` save failure during global settings save.
- Seeds and updates a healthy primary `Settings.coc` backup plus rotating healthy snapshots when the current file looks valid.
- Restores the strongest healthy backup candidate on startup only when the current `Settings.coc` hits a hard corruption threshold.
- Preserves the broken file as a timestamped corrupt snapshot before restore.
- Captures a best-effort backup of `continue_game.json` and restores it during shutdown if the launcher continue target disappears after being valid earlier in the session.
- Emits detailed file-health diagnostics to `Settings_File_Guard.Mod.log` so backup and restore decisions are explainable after the fact.
- Supports optional deep diagnostics via the mod options UI for session logs and `Settings.coc` snapshots around restore and fallback events.
- Uses conservative recovery by default: weaker-but-still-parseable files are preserved for diagnosis instead of being aggressively replaced.

## 한국어 요약

- 전역 설정 저장 중 알려진 `KeybindingSettings.bindings` 예외를 우회합니다.
- 현재 파일이 정상으로 보일 때 건강한 `Settings.coc` 주 백업과 회전 스냅샷을 생성하고 갱신합니다.
- 시작 시 현재 `Settings.coc`가 하드 손상 기준에 걸릴 때만 가장 강한 건강 백업 후보로 복원합니다.
- 복원 전에 손상된 파일을 타임스탬프가 붙은 스냅샷으로 남깁니다.
- 세션 중 정상으로 보였던 `continue_game.json`을 best-effort로 백업해 두고, 종료 중 런처 이어하기 대상이 사라지면 복원합니다.
- 백업/복원 판단 근거가 보이도록 상세 진단 로그를 남깁니다.
- 모드 옵션에서 deep diagnostics를 켜면 복구/우회 시점의 세션 로그와 `Settings.coc` 스냅샷을 남길 수 있습니다.
- 기본 복구 정책은 보수적이며, 약하지만 아직 읽을 수 있는 파일은 자동 교체보다 진단을 우선합니다.

## Limitations

- It cannot protect sessions where **all code mods are disabled**, because this utility itself is a code mod.
- It protects `Settings.coc` plus the launcher continue-target metadata file `continue_game.json`, but it does not modify actual `.cok` savegame contents.
- It cannot restore anything until a healthy backup has been created at least once.
- The diagnostic log helps explain failures, but it is not itself a restorable source of keybindings. Actual recovery still depends on healthy backups or snapshots.
- Conservative recovery may intentionally leave a suspicious but still parseable `Settings.coc` in place so that evidence is preserved for diagnosis.
- Binding entry counts in logs can reflect only user-overridden built-in shortcuts, so a low count by itself is not treated as corruption.

## Files and Paths

- Settings file: `Settings.coc` in the game's user data directory.
- Launcher continue target file: `continue_game.json` in the same directory.
- Primary backup file: `Settings.coc.settings_file_guard.bak` in the same directory.
- Continue target backup file: `continue_game.json.settings_file_guard.bak` in the same directory.
- Healthy snapshot: `Settings.coc.settings_file_guard.healthy.YYYYMMDD_HHMMSSfff.bak` in the same directory.
- Corrupt snapshot: `Settings.coc.settings_guard.corrupt.YYYYMMDD_HHMMSSfff.bak` in the same directory.
- Log file: `Settings_File_Guard.Mod.log` under the game's user data `Logs` directory.
- Deep diagnostics session log: `Logs\Settings_File_Guard.DeepDiagnostics.YYYYMMDD_HHMMSSfff.log`.
- Deep diagnostics snapshots: `Logs\Settings_File_Guard.DeepDiagnostics.YYYYMMDD_HHMMSSfff\`.

## Deep Diagnostics

- Open the mod's options page and toggle `Enable deep diagnostics`.
- When enabled, the mod writes a separate per-session diagnostic log plus targeted `Settings.coc` snapshots before restore, after restore, and around suspicious backup or keybinding fallback events.
- Startup logs also record the loaded mod assembly path, size, and timestamp so you can confirm which build the game actually loaded.
- Shutdown tracking starts at `OnDispose`, records late `Settings.coc` writes through process shutdown, traces `SettingAsset.Save(...)` / `SaveWithPersist(...)`, `AssetDatabase.ProcessSingleSettingsFile(...)`, and `SaveSettingsHelper.Write*` activity, adds low-level `FileStream` / `File.Move` / `File.Replace` tracing for direct `Settings.coc` writes that bypass `SettingAsset`, keeps `Settings.coc` stream lifetime state across the session so shutdown logs can reveal handles opened before `OnDispose`, watches the settings directory for create/change/delete/rename events so temp-file or replace patterns are visible even when the writer bypasses managed I/O hooks, groups late file changes into write episodes, logs the latest `Settings.coc` asset-session and stream-session context so you can distinguish one multi-step write from newly opened save sessions, attempts an immediate restore as soon as shutdown tracking sees a hard-corruption state, retries that restore on later shutdown polls while the file is still lock-blocked and shutdown has not yet started, stays armed until `ProcessExit` / `DomainUnload` instead of timing out after a fixed restore window, and performs one final immediate restore attempt during the terminal shutdown events without sleeping there.
- Turn the option back off to return to the normal lightweight logging mode.
- The settings UI default is `off`.

## Installation and First Run

1. Build the project or place the release files in the CS2 mods directory.
2. Enable `Settings File Guard` in your playset.
3. Launch the game once with a healthy keybinding state so the mod can seed the first healthy backup and rotating snapshots.
4. If hard corruption is detected on a later startup, the mod restores the strongest healthy backup candidate and keeps the broken file as a corrupt snapshot.
5. If the current file is suspicious but still parseable, the mod keeps it in place, skips poisoning the backup, and emits diagnostics for investigation.

## Build

```powershell
dotnet build "Settings File Guard.csproj"
```

## PDX Release Notes

- Leave `Properties/PublishConfiguration.xml` `ModId` empty for the first PDX upload.
- After the first upload assigns a `ModId`, write that value back into `PublishConfiguration.xml` and publish updates from then on.
- Public v1 is a background protection mod with an optional deep diagnostics toggle in the mod options UI.
