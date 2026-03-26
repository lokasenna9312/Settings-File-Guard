using Colossal.Logging;
using Game;
using Game.Modding;

namespace Settings_File_Guard
{
    public class Mod : IMod
    {
        public static readonly ILog log =
            LogManager.GetLogger($"{nameof(Settings_File_Guard)}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            SettingsFileProtectionService.RestoreBackupIfCurrentLooksCorrupted("startup validation");
            KeybindingPersistenceGuardPatches.Apply();
            KeybindingPersistenceGuardPatches.CaptureCurrentBindings();
            SettingsFileProtectionService.BackupHealthySettingsFile("post-load baseline");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            KeybindingPersistenceGuardPatches.CaptureCurrentBindings();
            SettingsFileProtectionService.BackupHealthySettingsFile("pre-dispose");
            log.Info("[KEYBIND_GUARD] Leaving guard patches applied until process exit to protect async settings-save after OnDispose.");
        }
    }
}
