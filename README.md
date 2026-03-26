# Settings File Guard

Settings File Guard is a standalone Cities: Skylines II utility mod that protects the global `Settings.coc` file from known keybinding-save corruption.

이 모드는 Cities: Skylines II의 전역 설정 파일 `Settings.coc`를 보호하기 위한 독립 유틸리티 모드입니다.

## What It Does

- Suppresses the known `Game.Settings.KeybindingSettings.bindings` save failure during global settings save.
- Creates a backup of a healthy `Settings.coc`.
- Restores the backup when the current `Settings.coc` is missing keybinding entries or otherwise looks corrupted.

## 한국어 요약

- 전역 설정 저장 중 알려진 `KeybindingSettings.bindings` 예외를 우회합니다.
- 건강한 `Settings.coc`를 백업합니다.
- 현재 `Settings.coc`가 손상되었거나 키바인딩 항목을 잃어버린 경우 백업본으로 복원합니다.

## What It Does Not Do

- It does not protect sessions where **all code mods are disabled**, because this utility itself is a code mod.
- It does not modify savegame data.
- It does not add a settings UI in v1.

## Limitations

- This mod cannot protect sessions where all code mods are disabled.
- It only protects the global settings file and does not touch savegame data.
- v1 is a background protection mod only.

## Files and Paths

- Settings file: `C:\Users\USERNAME\AppData\LocalLow\Colossal Order\Cities Skylines II\Settings.coc`
- Backup file: `Settings.coc.settings_file_guard.bak`
- Corrupt snapshot: `Settings.coc.settings_guard.corrupt.YYYYMMDD_HHMMSS.bak`
- Log file: `C:\Users\USERNAME\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\Settings_File_Guard.Mod.log`

## Installation

1. Build the project or place the release files in the CS2 mods directory.
2. Enable `Settings File Guard` in your playset.
3. Launch the game once with a healthy keybinding state so the mod can seed a healthy backup.

## Build

```powershell
dotnet build "Settings File Guard.csproj"
```

## Release Notes for v1

- Public v1 is a background protection mod only.
- No diagnostics UI or advanced analysis features are included.
- The primary goal is to reduce or recover from global keybinding-setting corruption.
