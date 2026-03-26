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
            GuardDiagnostics.Initialize();
            log.Info(nameof(OnLoad));
            GuardDiagnostics.WriteEvent("LIFECYCLE", "OnLoad start.");
            GuardDiagnostics.DumpFileSnapshot(
                "LIFECYCLE",
                "startup-before-restore",
                GuardPaths.SettingsFilePath,
                "Captured at mod OnLoad before startup validation.");

            SettingsFileProtectionService.RestoreBackupIfCurrentLooksCorrupted("startup validation");
            KeybindingPersistenceGuardPatches.Apply();
            KeybindingPersistenceGuardPatches.CaptureCurrentBindings();
            SettingsFileProtectionService.BackupHealthySettingsFile("post-load baseline");
            GuardDiagnostics.DumpFileSnapshot(
                "LIFECYCLE",
                "startup-after-baseline",
                GuardPaths.SettingsFilePath,
                "Captured at mod OnLoad after startup validation and baseline backup.");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            GuardDiagnostics.WriteEvent("LIFECYCLE", "OnDispose start.");
            GuardDiagnostics.DumpFileSnapshot(
                "LIFECYCLE",
                "dispose-before-backup",
                GuardPaths.SettingsFilePath,
                "Captured at mod OnDispose before pre-dispose backup.");
            KeybindingPersistenceGuardPatches.CaptureCurrentBindings();
            SettingsFileProtectionService.BackupHealthySettingsFile("pre-dispose");
            GuardDiagnostics.DumpFileSnapshot(
                "LIFECYCLE",
                "dispose-after-backup",
                GuardPaths.SettingsFilePath,
                "Captured at mod OnDispose after pre-dispose backup.");
            log.Info("[KEYBIND_GUARD] Leaving guard patches applied until process exit to protect async settings-save after OnDispose.");
        }
    }
}
