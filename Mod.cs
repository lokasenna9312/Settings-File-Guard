using System;
using System.IO;
using System.Reflection;
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
            ShutdownWriteTracker.Initialize();
            log.Info(nameof(OnLoad));
            LogLoadedBuildIdentity();
            GuardDiagnostics.WriteEvent("LIFECYCLE", "OnLoad start.");
            GuardDiagnostics.DumpFileSnapshot(
                "LIFECYCLE",
                "startup-before-restore",
                GuardPaths.SettingsFilePath,
                "Captured at mod OnLoad before startup validation.");

            SettingsFileProtectionService.RestoreBackupIfCurrentLooksCorrupted("startup validation");
            KeybindingPersistenceGuardPatches.Apply();
            SettingsSaveTracePatches.Apply();
            AssetDatabaseSettingsTracePatches.Apply();
            SettingsFileIoTracePatches.Apply();
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
            ShutdownWriteTracker.Arm("OnDispose start");
            GuardDiagnostics.DumpFileSnapshot(
                "LIFECYCLE",
                "dispose-before-backup",
                GuardPaths.SettingsFilePath,
                "Captured at mod OnDispose before pre-dispose backup.");
            KeybindingPersistenceGuardPatches.CaptureCurrentBindings();
            SettingsFileProtectionService.BackupHealthySettingsFile("pre-dispose");
            ShutdownWriteTracker.NoteCheckpoint("OnDispose after pre-dispose backup");
            GuardDiagnostics.DumpFileSnapshot(
                "LIFECYCLE",
                "dispose-after-backup",
                GuardPaths.SettingsFilePath,
                "Captured at mod OnDispose after pre-dispose backup.");
            log.Info("[KEYBIND_GUARD] Leaving guard patches applied until process exit to protect async settings-save after OnDispose.");
            ShutdownWriteTracker.NoteCheckpoint("OnDispose completion");
        }

        private static void LogLoadedBuildIdentity()
        {
            Assembly assembly = typeof(Mod).Assembly;
            string assemblyPath = assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(assembly.CodeBase))
                    {
                        assemblyPath = new Uri(assembly.CodeBase).LocalPath;
                    }
                }
                catch
                {
                }
            }

            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                assemblyPath = assembly.ManifestModule?.FullyQualifiedName;
            }

            string assemblyLength = "unknown";
            string assemblyLastWriteUtc = "unknown";

            if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
            {
                FileInfo info = new FileInfo(assemblyPath);
                assemblyLength = info.Length.ToString();
                assemblyLastWriteUtc = info.LastWriteTimeUtc.ToString("O");
            }

            string message =
                "[KEYBIND_DIAGNOSTICS] Loaded build identity. " +
                $"assemblyPath={assemblyPath ?? "unknown"}, assemblyLength={assemblyLength}, assemblyLastWriteUtc={assemblyLastWriteUtc}, deepDiagnostics={GuardDiagnostics.IsEnabled}";

            log.Info(message);
            GuardDiagnostics.WriteEvent("SYSTEM", message);
        }
    }
}
