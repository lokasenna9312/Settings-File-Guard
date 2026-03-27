using System;
using System.IO;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace Settings_File_Guard
{
    public class Mod : IMod
    {
        public static readonly ILog log =
            LogManager.GetLogger($"{nameof(Settings_File_Guard)}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        public static Setting Settings { get; private set; }

        private Setting m_Setting;

        public void OnLoad(UpdateSystem updateSystem)
        {
            GuardPaths.Initialize();
            m_Setting = new Setting(this);
            AssetDatabase.global.LoadSettings(nameof(Settings_File_Guard), m_Setting, new Setting(this));
            Settings = m_Setting;
            RegisterLocalization();
            m_Setting.RegisterInOptionsUI();
            GuardDiagnostics.Initialize();
            ContinueGameProtectionService.InitializeSession();
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
            ContinueGameProtectionService.CaptureHealthyContinueGame("pre-dispose");
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

            if (m_Setting != null)
            {
                m_Setting.UnregisterInOptionsUI();
                m_Setting = null;
                Settings = null;
            }
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

        private void RegisterLocalization()
        {
            if (m_Setting == null || GameManager.instance?.localizationManager == null)
            {
                return;
            }

            RegisterLocalizationSource(
                "en-US",
                "Settings File Guard",
                "Main",
                "Diagnostics",
                "Enable deep diagnostics",
                "Writes per-session deep diagnostics logs and targeted Settings.coc snapshots for investigation. Leave it disabled unless you are actively troubleshooting shutdown corruption or keybinding loss.");
            RegisterLocalizationSource(
                "ko-KR",
                "Settings File Guard",
                "메인",
                "진단",
                "Deep diagnostics 사용",
                "세션별 deep diagnostics 로그와 Settings.coc 스냅샷을 기록합니다. 종료 손상이나 키바인딩 손실을 조사할 때만 켜 두는 편이 좋습니다.");
            RegisterLocalizationSource(
                "de-DE",
                "Settings File Guard",
                "Haupt",
                "Diagnose",
                "Erweiterte Diagnose aktivieren",
                "Schreibt sitzungsbezogene Deep-Diagnostics-Protokolle und gezielte Settings.coc-Snapshots für Untersuchungen. Lass diese Option deaktiviert, solange du nicht aktiv Shutdown-Beschädigungen oder Keybinding-Verluste untersuchst.");
            RegisterLocalizationSource(
                "es-ES",
                "Settings File Guard",
                "Principal",
                "Diagnóstico",
                "Activar diagnósticos profundos",
                "Escribe registros de diagnóstico profundo por sesión y capturas dirigidas de Settings.coc para investigación. Déjalo desactivado salvo que estés investigando activamente corrupción al cerrar o pérdida de atajos.");
            RegisterLocalizationSource(
                "fr-FR",
                "Settings File Guard",
                "Principal",
                "Diagnostic",
                "Activer les diagnostics approfondis",
                "Écrit des journaux de diagnostic approfondi par session et des instantanés ciblés de Settings.coc pour l'investigation. Laisse cette option désactivée sauf si tu enquêtes activement sur une corruption à la fermeture ou une perte de raccourcis.");
            RegisterLocalizationSource(
                "it-IT",
                "Settings File Guard",
                "Principale",
                "Diagnostica",
                "Abilita diagnostica approfondita",
                "Scrive log di diagnostica approfondita per sessione e snapshot mirati di Settings.coc per l'analisi. Lasciala disattivata a meno che tu non stia indagando attivamente corruzioni in chiusura o perdita delle scorciatoie.");
            RegisterLocalizationSource(
                "ja-JP",
                "Settings File Guard",
                "メイン",
                "診断",
                "詳細診断を有効化",
                "調査用に、セッションごとの詳細診断ログと対象を絞った Settings.coc スナップショットを書き出します。終了時の破損やキーバインド消失を実際に調査しているとき以外は無効のままにしてください。");
            RegisterLocalizationSource(
                "pl-PL",
                "Settings File Guard",
                "Główne",
                "Diagnostyka",
                "Włącz rozszerzoną diagnostykę",
                "Zapisuje szczegółowe logi diagnostyczne dla każdej sesji oraz ukierunkowane migawki pliku Settings.coc do analizy. Pozostaw wyłączone, chyba że aktywnie badzasz uszkodzenia przy zamykaniu gry lub utratę skrótów.");
            RegisterLocalizationSource(
                "pt-BR",
                "Settings File Guard",
                "Principal",
                "Diagnóstico",
                "Ativar diagnósticos aprofundados",
                "Grava logs de diagnóstico aprofundado por sessão e snapshots direcionados de Settings.coc para investigação. Deixe desativado, a menos que você esteja investigando ativamente corrupção no encerramento ou perda de atalhos.");
            RegisterLocalizationSource(
                "ru-RU",
                "Settings File Guard",
                "Основное",
                "Диагностика",
                "Включить расширенную диагностику",
                "Записывает подробные диагностические журналы по сессиям и целевые снимки Settings.coc для расследования. Оставляй этот параметр выключенным, если ты не исследуешь повреждение при выходе или потерю сочетаний клавиш.");
            RegisterLocalizationSource(
                "zh-HANS",
                "Settings File Guard",
                "主要",
                "诊断",
                "启用深度诊断",
                "为排查问题写入按会话划分的深度诊断日志和有针对性的 Settings.coc 快照。除非你正在主动调查退出时损坏或快捷键丢失，否则请保持关闭。");
            RegisterLocalizationSource(
                "zh-HANT",
                "Settings File Guard",
                "主要",
                "診斷",
                "啟用深度診斷",
                "會為調查用途寫入每次工作階段的深度診斷記錄與針對性的 Settings.coc 快照。除非你正在主動調查結束時損壞或快捷鍵遺失，否則請保持關閉。");
        }

        private void RegisterLocalizationSource(
            string localeId,
            string settingsTitle,
            string mainTab,
            string diagnosticsGroup,
            string enableLabel,
            string enableDescription)
        {
            GameManager.instance.localizationManager.AddSource(
                localeId,
                new DiagnosticsLocaleSource(
                    m_Setting,
                    settingsTitle,
                    mainTab,
                    diagnosticsGroup,
                    enableLabel,
                    enableDescription));
        }
    }
}
