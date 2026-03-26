# Settings File Guard

Settings File Guard is a standalone Cities: Skylines II utility mod that protects the global `Settings.coc` file from known keybinding-save corruption.

이 모드는 Cities: Skylines II의 전역 설정 파일 `Settings.coc`를 보호하기 위한 독립 유틸리티 모드입니다.

## Core Behavior

- Suppresses the known `Game.Settings.KeybindingSettings.bindings` save failure during global settings save.
- Seeds and updates a healthy primary `Settings.coc` backup plus rotating healthy snapshots when the current file looks valid.
- Restores the strongest healthy backup candidate on startup when the current `Settings.coc` is missing keybinding data or otherwise looks corrupted.
- Preserves the broken file as a timestamped corrupt snapshot before restore.
- Emits detailed file-health diagnostics to `Settings_File_Guard.Mod.log` so backup and restore decisions are explainable after the fact.
- Supports optional deep diagnostics via a toggle file for session logs and `Settings.coc` snapshots around restore and fallback events.

## 한국어 요약

- 전역 설정 저장 중 알려진 `KeybindingSettings.bindings` 예외를 우회합니다.
- 현재 파일이 정상으로 보일 때 건강한 `Settings.coc` 주 백업과 회전 스냅샷을 생성하고 갱신합니다.
- 시작 시 현재 `Settings.coc`가 손상되었거나 키바인딩 항목을 잃어버린 경우 가장 강한 건강 백업 후보로 복원합니다.
- 복원 전에 손상된 파일을 타임스탬프가 붙은 스냅샷으로 남깁니다.
- 백업/복원 판단 근거가 보이도록 상세 진단 로그를 남깁니다.
- 토글 파일을 두면 복구/우회 시점의 세션 로그와 `Settings.coc` 스냅샷을 남기는 deep diagnostics를 활성화할 수 있습니다.

## Limitations

- It cannot protect sessions where **all code mods are disabled**, because this utility itself is a code mod.
- It only protects the global `Settings.coc` file and does not touch savegame data.
- v1 is a background protection mod only and does not include a settings UI.
- It cannot restore anything until a healthy backup has been created at least once.
- The diagnostic log helps explain failures, but it is not itself a restorable source of keybindings. Actual recovery still depends on healthy backups or snapshots.

## Files and Paths

- Settings file: `Settings.coc` in the game's user data directory.
- Primary backup file: `Settings.coc.settings_file_guard.bak` in the same directory.
- Healthy snapshot: `Settings.coc.settings_file_guard.healthy.YYYYMMDD_HHMMSSfff.bak` in the same directory.
- Corrupt snapshot: `Settings.coc.settings_guard.corrupt.YYYYMMDD_HHMMSSfff.bak` in the same directory.
- Log file: `Settings_File_Guard.Mod.log` under the game's user data `Logs` directory.
- Deep diagnostics toggle file: `Settings_File_Guard.DeepDiagnostics.enabled` in the game's user data directory.
- Deep diagnostics session log: `Logs\Settings_File_Guard.DeepDiagnostics.YYYYMMDD_HHMMSSfff.log`.
- Deep diagnostics snapshots: `Logs\Settings_File_Guard.DeepDiagnostics.YYYYMMDD_HHMMSSfff\`.

## Deep Diagnostics

- Create an empty `Settings_File_Guard.DeepDiagnostics.enabled` file in the game's user data directory before launching the game.
- When enabled, the mod writes a separate per-session diagnostic log plus targeted `Settings.coc` snapshots before restore, after restore, and around suspicious backup or keybinding fallback events.
- Remove the toggle file to return to the normal lightweight logging mode.

## Installation and First Run

1. Build the project or place the release files in the CS2 mods directory.
2. Enable `Settings File Guard` in your playset.
3. Launch the game once with a healthy keybinding state so the mod can seed the first healthy backup and rotating snapshots.
4. If corruption is detected on a later startup, the mod restores the strongest healthy backup candidate and keeps the broken file as a corrupt snapshot.

## Build

```powershell
dotnet build "Settings File Guard.csproj"
```

## PDX Release Notes

- Leave `Properties/PublishConfiguration.xml` `ModId` empty for the first PDX upload.
- After the first upload assigns a `ModId`, write that value back into `PublishConfiguration.xml` and publish updates from then on.
- Public v1 is a background protection mod only. No diagnostics UI or advanced analysis features are included.
