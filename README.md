# Settings File Guard

Settings File Guard is a standalone Cities: Skylines II utility mod that protects the global `Settings.coc` file from known keybinding-save corruption.

이 모드는 Cities: Skylines II의 전역 설정 파일 `Settings.coc`를 보호하기 위한 독립 유틸리티 모드입니다.

## Core Behavior

- Suppresses the known `Game.Settings.KeybindingSettings.bindings` save failure during global settings save.
- Seeds and updates a healthy `Settings.coc` backup when the current file looks valid.
- Restores the backup on startup when the current `Settings.coc` is missing keybinding entries or otherwise looks corrupted.
- Preserves the broken file as a timestamped corrupt snapshot before restore.

## 한국어 요약

- 전역 설정 저장 중 알려진 `KeybindingSettings.bindings` 예외를 우회합니다.
- 현재 파일이 정상으로 보일 때 건강한 `Settings.coc` 백업을 생성하고 갱신합니다.
- 시작 시 현재 `Settings.coc`가 손상되었거나 키바인딩 항목을 잃어버린 경우 백업본으로 복원합니다.
- 복원 전에 손상된 파일을 타임스탬프가 붙은 스냅샷으로 남깁니다.

## Limitations

- It cannot protect sessions where **all code mods are disabled**, because this utility itself is a code mod.
- It only protects the global `Settings.coc` file and does not touch savegame data.
- v1 is a background protection mod only and does not include a settings UI.
- It cannot restore anything until a healthy backup has been created at least once.

## Files and Paths

- Settings file: `Settings.coc` in the game's user data directory.
- Backup file: `Settings.coc.settings_file_guard.bak` in the same directory.
- Corrupt snapshot: `Settings.coc.settings_guard.corrupt.YYYYMMDD_HHMMSS.bak` in the same directory.
- Log file: `Settings_File_Guard.Mod.log` under the game's user data `Logs` directory.

## Installation and First Run

1. Build the project or place the release files in the CS2 mods directory.
2. Enable `Settings File Guard` in your playset.
3. Launch the game once with a healthy keybinding state so the mod can seed the first healthy backup.
4. If corruption is detected on a later startup, the mod restores the backup and keeps the broken file as a corrupt snapshot.

## Build

```powershell
dotnet build "Settings File Guard.csproj"
```

## PDX Release Notes

- Leave `Properties/PublishConfiguration.xml` `ModId` empty for the first PDX upload.
- After the first upload assigns a `ModId`, write that value back into `PublishConfiguration.xml` and publish updates from then on.
- Public v1 is a background protection mod only. No diagnostics UI or advanced analysis features are included.
