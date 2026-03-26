# Settings File Guard

Settings File Guard is a standalone Cities: Skylines II utility mod that protects the global `Settings.coc` file from known keybinding-save corruption.

## What It Does

- Suppresses the known `Game.Settings.KeybindingSettings.bindings` save failure during global settings save.
- Creates a backup of a healthy `Settings.coc`.
- Restores the backup when the current `Settings.coc` is missing keybinding entries or otherwise looks corrupted.

## What It Does Not Do

- It does not protect sessions where **all code mods are disabled**, because this utility itself is a code mod.
- It does not modify savegame data.
- It does not add a settings UI in v1.

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
