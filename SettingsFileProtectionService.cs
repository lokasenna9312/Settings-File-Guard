using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Settings_File_Guard
{
    internal static class SettingsFileProtectionService
    {
        private const string BackupFileName = "Settings.coc.settings_file_guard.bak";
        private const string HealthySnapshotFilePrefix = "Settings.coc.settings_file_guard.healthy.";
        private const string HealthySnapshotFileSuffix = ".bak";
        private const string CorruptSnapshotFilePrefix = "Settings.coc.settings_guard.corrupt.";
        private const string CorruptSnapshotFileSuffix = ".bak";
        private const int MaxHealthySnapshots = 5;
        private const long MinimumHealthyFileLengthBytes = 512;

        private static readonly object s_FileGate = new object();
        private static bool s_LoggedMissingHealthyBackup;

        public static void BackupHealthySettingsFile(string reason)
        {
            lock (s_FileGate)
            {
                try
                {
                    SettingsFileAnalysis currentAnalysis = AnalyzeSettingsFile(GuardPaths.SettingsFilePath);
                    SettingsFileAnalysis bestHealthyBackup = SelectBestHealthyBackupCandidate(out string candidateSummary);
                    GuardDiagnostics.WriteEvent(
                        "BACKUP",
                        $"BackupHealthySettingsFile start. reason={reason}, current={currentAnalysis.Describe()}, candidates={candidateSummary}");

                    if (!currentAnalysis.LooksHealthy)
                    {
                        Mod.log.Warn(
                            $"[KEYBIND_BACKUP] Skipped backup because current Settings.coc does not look healthy. reason={reason}, current={currentAnalysis.Describe()}, candidates={candidateSummary}");
                        GuardDiagnostics.DumpFileSnapshot(
                            "BACKUP",
                            $"backup-skip-{reason}",
                            GuardPaths.SettingsFilePath,
                            currentAnalysis.Describe());
                        return;
                    }

                    if (IsSignificantlyWeakerThanReference(currentAnalysis, bestHealthyBackup))
                    {
                        Mod.log.Warn(
                            "[KEYBIND_BACKUP] Skipped backup because current Settings.coc is materially weaker than the strongest healthy backup. " +
                            $"reason={reason}, current={currentAnalysis.Describe()}, strongestHealthy={bestHealthyBackup.Describe()}");
                        GuardDiagnostics.DumpFileSnapshot(
                            "BACKUP",
                            $"backup-skip-weaker-current-{reason}",
                            GuardPaths.SettingsFilePath,
                            currentAnalysis.Describe());
                        GuardDiagnostics.DumpFileSnapshot(
                            "BACKUP",
                            $"backup-skip-weaker-reference-{reason}",
                            bestHealthyBackup.FilePath,
                            bestHealthyBackup.Describe());
                        return;
                    }

                    Directory.CreateDirectory(GuardPaths.SettingsDirectoryPath);
                    File.Copy(GuardPaths.SettingsFilePath, BackupFilePath, overwrite: true);

                    string healthySnapshotPath = CreateHealthySnapshotPath();
                    SafeCopyWithRetries(GuardPaths.SettingsFilePath, healthySnapshotPath, overwrite: true);
                    PruneHealthySnapshots();

                    s_LoggedMissingHealthyBackup = false;

                    Mod.log.Info(
                        $"[KEYBIND_BACKUP] Backed up healthy Settings.coc. reason={reason}, primary={BackupFilePath}, snapshot={healthySnapshotPath}, current={currentAnalysis.Describe()}, candidatesBeforeWrite={candidateSummary}");
                    GuardDiagnostics.WriteEvent(
                        "BACKUP",
                        $"BackupHealthySettingsFile completed. reason={reason}, primary={BackupFilePath}, snapshot={healthySnapshotPath}, current={currentAnalysis.Describe()}");
                }
                catch (Exception ex)
                {
                    Mod.log.Error(ex, $"[KEYBIND_BACKUP] Failed to back up Settings.coc. reason={reason}");
                    GuardDiagnostics.WriteEvent(
                        "BACKUP",
                        $"BackupHealthySettingsFile failed. reason={reason}, exception={ex}");
                }
            }
        }

        public static void RestoreBackupIfCurrentLooksCorrupted(string reason)
        {
            lock (s_FileGate)
            {
                try
                {
                    SettingsFileAnalysis currentAnalysis = AnalyzeSettingsFile(GuardPaths.SettingsFilePath);
                    SettingsFileAnalysis bestHealthyBackup = SelectBestHealthyBackupCandidate(out string candidateSummary);
                    GuardDiagnostics.WriteEvent(
                        "RESTORE",
                        $"RestoreBackupIfCurrentLooksCorrupted start. reason={reason}, current={currentAnalysis.Describe()}, candidates={candidateSummary}");

                    if (bestHealthyBackup == null)
                    {
                        if (!s_LoggedMissingHealthyBackup)
                        {
                            s_LoggedMissingHealthyBackup = true;
                            Mod.log.Warn(
                                $"[KEYBIND_BACKUP] No healthy backup candidate is available. restoreReason={reason}, current={currentAnalysis.Describe()}, candidates={candidateSummary}");
                        }

                        GuardDiagnostics.DumpFileSnapshot(
                            "RESTORE",
                            $"restore-missing-candidate-{reason}",
                            GuardPaths.SettingsFilePath,
                            currentAnalysis.Describe());
                        return;
                    }

                    s_LoggedMissingHealthyBackup = false;

                    if (currentAnalysis.LooksHealthy)
                    {
                        if (IsSignificantlyWeakerThanReference(currentAnalysis, bestHealthyBackup))
                        {
                            Mod.log.Warn(
                                "[KEYBIND_BACKUP] Current Settings.coc passed baseline health checks but is materially weaker " +
                                $"than the strongest healthy backup. Conservative mode preserved the current file. restoreReason={reason}, current={currentAnalysis.Describe()}, strongestHealthy={bestHealthyBackup.Describe()}");
                            GuardDiagnostics.DumpFileSnapshot(
                                "RESTORE",
                                $"restore-conservative-skip-current-{reason}",
                                GuardPaths.SettingsFilePath,
                                currentAnalysis.Describe());
                            GuardDiagnostics.DumpFileSnapshot(
                                "RESTORE",
                                $"restore-conservative-skip-reference-{reason}",
                                bestHealthyBackup.FilePath,
                                bestHealthyBackup.Describe());
                            GuardDiagnostics.WriteEvent(
                                "RESTORE",
                                "Conservative restore skip: current Settings.coc is weaker than the strongest healthy backup " +
                                $"but still structurally healthy. reason={reason}, current={currentAnalysis.Describe()}, strongestHealthy={bestHealthyBackup.Describe()}");
                        }
                        else
                        {
                            Mod.log.Info(
                                $"[KEYBIND_BACKUP] Current Settings.coc passed validation. restoreReason={reason}, current={currentAnalysis.Describe()}, strongestHealthy={bestHealthyBackup.DescribeCompact()}");
                            GuardDiagnostics.WriteEvent(
                                "RESTORE",
                                $"Restore skipped because current Settings.coc looks healthy enough. reason={reason}, current={currentAnalysis.Describe()}, strongestHealthy={bestHealthyBackup.DescribeCompact()}");
                        }

                        return;
                    }

                    if (!currentAnalysis.HasHardRestoreFailure)
                    {
                        Mod.log.Warn(
                            "[KEYBIND_BACKUP] Current Settings.coc failed baseline health checks but did not meet the hard-restore threshold. " +
                            $"Conservative mode preserved the current file. restoreReason={reason}, current={currentAnalysis.Describe()}, strongestHealthy={bestHealthyBackup.Describe()}");
                        GuardDiagnostics.DumpFileSnapshot(
                            "RESTORE",
                            $"restore-conservative-preserve-current-{reason}",
                            GuardPaths.SettingsFilePath,
                            currentAnalysis.Describe());
                        GuardDiagnostics.DumpFileSnapshot(
                            "RESTORE",
                            $"restore-conservative-preserve-reference-{reason}",
                            bestHealthyBackup.FilePath,
                            bestHealthyBackup.Describe());
                        GuardDiagnostics.WriteEvent(
                            "RESTORE",
                            "Conservative restore skip: current Settings.coc is suspicious or incomplete but did not meet the hard-restore threshold. " +
                            $"reason={reason}, current={currentAnalysis.Describe()}, strongestHealthy={bestHealthyBackup.Describe()}");
                        return;
                    }

                    Directory.CreateDirectory(GuardPaths.SettingsDirectoryPath);

                    string corruptSnapshotPath = null;
                    if (currentAnalysis.Exists)
                    {
                        corruptSnapshotPath = CreateCorruptSnapshotPath();
                        GuardDiagnostics.DumpFileSnapshot(
                            "RESTORE",
                            $"restore-current-before-replace-{reason}",
                            GuardPaths.SettingsFilePath,
                            currentAnalysis.Describe());
                        SafeCopyWithRetries(GuardPaths.SettingsFilePath, corruptSnapshotPath, overwrite: true);
                    }

                    GuardDiagnostics.DumpFileSnapshot(
                        "RESTORE",
                        $"restore-selected-backup-{reason}",
                        bestHealthyBackup.FilePath,
                        bestHealthyBackup.Describe());
                    SafeCopyWithRetries(bestHealthyBackup.FilePath, GuardPaths.SettingsFilePath, overwrite: true);
                    Mod.log.Warn(
                        $"[KEYBIND_BACKUP] Restored Settings.coc from backup. reason={reason}, chosenHealthy={bestHealthyBackup.Describe()}, currentBeforeRestore={currentAnalysis.Describe()}, corruptSnapshot={corruptSnapshotPath ?? "none"}, candidates={candidateSummary}");
                    GuardDiagnostics.DumpFileSnapshot(
                        "RESTORE",
                        $"restore-current-after-replace-{reason}",
                        GuardPaths.SettingsFilePath,
                        "Captured after restoring Settings.coc from the selected healthy backup.");
                }
                catch (Exception ex)
                {
                    Mod.log.Error(ex, $"[KEYBIND_BACKUP] Failed to restore Settings.coc from backup. reason={reason}");
                    GuardDiagnostics.WriteEvent(
                        "RESTORE",
                        $"RestoreBackupIfCurrentLooksCorrupted failed. reason={reason}, exception={ex}");
                }
            }
        }

        private static string BackupFilePath => Path.Combine(GuardPaths.SettingsDirectoryPath, BackupFileName);

        internal static string DescribeSettingsFileForDiagnostics(string path)
        {
            return AnalyzeSettingsFile(path).Describe();
        }

        private static SettingsFileAnalysis AnalyzeSettingsFile(string path)
        {
            SettingsFileAnalysis analysis = new SettingsFileAnalysis(path);
            if (!File.Exists(path))
            {
                return analysis;
            }

            try
            {
                FileInfo info = new FileInfo(path);
                analysis.Exists = true;
                analysis.Length = info.Length;
                analysis.LastWriteTimeUtc = info.LastWriteTimeUtc;

                string text = File.ReadAllText(path);
                analysis.HasGeneralSettings = text.IndexOf("General Settings", StringComparison.Ordinal) >= 0;
                analysis.HasGraphicsSettings = text.IndexOf("Graphics Settings", StringComparison.Ordinal) >= 0;
                analysis.HasKeybindingSettingsSection =
                    text.IndexOf("Keybinding Settings", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("Input Settings", StringComparison.Ordinal) >= 0;
                analysis.HasBindingsProperty = text.IndexOf("\"bindings\"", StringComparison.Ordinal) >= 0;
                analysis.BindingMapCount = CountOccurrences(text, "\"m_MapName\"");
                analysis.ActionNameCount = CountOccurrences(text, "\"m_ActionName\"");
                analysis.DeviceCount = CountOccurrences(text, "\"m_Device\"");
                analysis.BindingPathCount = CountOccurrences(text, "\"m_Path\"");
                analysis.ModifierCount = CountOccurrences(text, "\"m_Modifiers\"");
                analysis.SectionHeaderCount = CountSettingsSectionHeaders(text);
            }
            catch (Exception ex)
            {
                analysis.ReadFailure = $"{ex.GetType().Name}: {ex.Message}";
            }

            return analysis;
        }

        private static SettingsFileAnalysis SelectBestHealthyBackupCandidate(out string candidateSummary)
        {
            List<SettingsFileAnalysis> analyses = new List<SettingsFileAnalysis>();

            foreach (string candidatePath in EnumerateBackupCandidatePaths())
            {
                analyses.Add(AnalyzeSettingsFile(candidatePath));
            }

            candidateSummary = BuildCandidateSummary(analyses);

            SettingsFileAnalysis bestHealthyBackup = null;
            foreach (SettingsFileAnalysis analysis in analyses)
            {
                if (!analysis.LooksHealthy)
                {
                    continue;
                }

                if (IsBetterRestoreCandidate(analysis, bestHealthyBackup))
                {
                    bestHealthyBackup = analysis;
                }
            }

            return bestHealthyBackup;
        }

        private static IEnumerable<string> EnumerateBackupCandidatePaths()
        {
            yield return BackupFilePath;

            if (!Directory.Exists(GuardPaths.SettingsDirectoryPath))
            {
                yield break;
            }

            string[] snapshotPaths = Directory.GetFiles(
                GuardPaths.SettingsDirectoryPath,
                $"{HealthySnapshotFilePrefix}*{HealthySnapshotFileSuffix}");

            foreach (string snapshotPath in snapshotPaths)
            {
                yield return snapshotPath;
            }
        }

        private static string BuildCandidateSummary(List<SettingsFileAnalysis> analyses)
        {
            if (analyses.Count == 0)
            {
                return "none";
            }

            List<string> descriptions = new List<string>(analyses.Count);
            foreach (SettingsFileAnalysis analysis in analyses)
            {
                descriptions.Add(analysis.DescribeCompact());
            }

            return string.Join(" | ", descriptions.ToArray());
        }

        private static bool IsBetterRestoreCandidate(SettingsFileAnalysis candidate, SettingsFileAnalysis currentBest)
        {
            if (candidate == null)
            {
                return false;
            }

            if (currentBest == null)
            {
                return true;
            }

            if (candidate.StrengthScore != currentBest.StrengthScore)
            {
                return candidate.StrengthScore > currentBest.StrengthScore;
            }

            return candidate.LastWriteTimeUtc > currentBest.LastWriteTimeUtc;
        }

        private static bool IsSignificantlyWeakerThanReference(
            SettingsFileAnalysis candidate,
            SettingsFileAnalysis reference)
        {
            if (candidate == null ||
                reference == null ||
                !candidate.LooksHealthy ||
                !reference.LooksHealthy ||
                string.Equals(candidate.FilePath, reference.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool substantiallyLowerScore = candidate.StrengthScore * 100 < reference.StrengthScore * 80;
            bool materiallySmallerFile = candidate.Length * 100 < reference.Length * 85;
            bool materiallyFewerSections = candidate.SectionHeaderCount + 1 < reference.SectionHeaderCount;

            return substantiallyLowerScore ||
                   (materiallySmallerFile && materiallyFewerSections);
        }

        private static void PruneHealthySnapshots()
        {
            if (!Directory.Exists(GuardPaths.SettingsDirectoryPath))
            {
                return;
            }

            FileInfo[] snapshotFiles = new DirectoryInfo(GuardPaths.SettingsDirectoryPath).GetFiles(
                $"{HealthySnapshotFilePrefix}*{HealthySnapshotFileSuffix}");
            Array.Sort(snapshotFiles, CompareByLastWriteDescending);

            for (int i = MaxHealthySnapshots; i < snapshotFiles.Length; i += 1)
            {
                try
                {
                    string path = snapshotFiles[i].FullName;
                    snapshotFiles[i].Delete();
                    Mod.log.Info($"[KEYBIND_BACKUP] Pruned old healthy snapshot. path={path}");
                }
                catch (Exception ex)
                {
                    Mod.log.Warn(
                        $"[KEYBIND_BACKUP] Failed to prune old healthy snapshot. path={snapshotFiles[i].FullName}, reason={ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static int CompareByLastWriteDescending(FileInfo left, FileInfo right)
        {
            int comparison = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
            if (comparison != 0)
            {
                return comparison;
            }

            return string.Compare(left.FullName, right.FullName, StringComparison.Ordinal);
        }

        private static string CreateHealthySnapshotPath()
        {
            return CreateTimestampedSnapshotPath(HealthySnapshotFilePrefix, HealthySnapshotFileSuffix);
        }

        private static string CreateCorruptSnapshotPath()
        {
            return CreateTimestampedSnapshotPath(CorruptSnapshotFilePrefix, CorruptSnapshotFileSuffix);
        }

        private static string CreateTimestampedSnapshotPath(string prefix, string suffix)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            string path = Path.Combine(GuardPaths.SettingsDirectoryPath, $"{prefix}{timestamp}{suffix}");
            int discriminator = 1;

            while (File.Exists(path))
            {
                path = Path.Combine(GuardPaths.SettingsDirectoryPath, $"{prefix}{timestamp}_{discriminator:00}{suffix}");
                discriminator += 1;
            }

            return path;
        }

        private static int CountOccurrences(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            {
                return 0;
            }

            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
            {
                count += 1;
                index += pattern.Length;
            }

            return count;
        }

        private static int CountSettingsSectionHeaders(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int count = 0;
            using (StringReader reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length > 0 && line.EndsWith(" Settings", StringComparison.Ordinal))
                    {
                        count += 1;
                    }
                }
            }

            return count;
        }

        private static void SafeCopyWithRetries(string sourcePath, string destinationPath, bool overwrite)
        {
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt += 1)
            {
                try
                {
                    File.Copy(sourcePath, destinationPath, overwrite);
                    return;
                }
                catch when (attempt < maxAttempts)
                {
                    Thread.Sleep(50);
                }
            }

            File.Copy(sourcePath, destinationPath, overwrite);
        }

        private sealed class SettingsFileAnalysis
        {
            public SettingsFileAnalysis(string filePath)
            {
                FilePath = filePath;
                LastWriteTimeUtc = DateTime.MinValue;
            }

            public string FilePath { get; }

            public bool Exists { get; set; }

            public long Length { get; set; }

            public DateTime LastWriteTimeUtc { get; set; }

            public string ReadFailure { get; set; }

            public bool HasGeneralSettings { get; set; }

            public bool HasGraphicsSettings { get; set; }

            public bool HasKeybindingSettingsSection { get; set; }

            public bool HasBindingsProperty { get; set; }

            public int BindingMapCount { get; set; }

            public int ActionNameCount { get; set; }

            public int DeviceCount { get; set; }

            public int BindingPathCount { get; set; }

            public int ModifierCount { get; set; }

            public int SectionHeaderCount { get; set; }

            public bool LooksHealthy =>
                Exists &&
                string.IsNullOrEmpty(ReadFailure) &&
                Length >= MinimumHealthyFileLengthBytes &&
                HasGeneralSettings &&
                HasGraphicsSettings &&
                HasKeybindingSettingsSection &&
                HasBindingsProperty;

            public bool HasHardRestoreFailure =>
                !Exists ||
                !string.IsNullOrEmpty(ReadFailure) ||
                Length < MinimumHealthyFileLengthBytes ||
                !HasKeybindingSettingsSection ||
                !HasBindingsProperty;

            public int StrengthScore
            {
                get
                {
                    int score = LooksHealthy ? 10000 : 0;
                    if (HasGeneralSettings)
                    {
                        score += 250;
                    }

                    if (HasGraphicsSettings)
                    {
                        score += 250;
                    }

                    if (HasKeybindingSettingsSection)
                    {
                        score += 400;
                    }

                    if (HasBindingsProperty)
                    {
                        score += 250;
                    }

                    score += Math.Min(SectionHeaderCount, 50) * 80;
                    score += (int)Math.Min(Length / 256L, 200L);
                    return score;
                }
            }

            public string Describe()
            {
                return
                    $"file={FilePath}, exists={Exists}, length={Length}, general={HasGeneralSettings}, graphics={HasGraphicsSettings}, " +
                    $"keybindingSection={HasKeybindingSettingsSection}, bindingsProperty={HasBindingsProperty}, bindingMaps={BindingMapCount}, " +
                    $"actions={ActionNameCount}, devices={DeviceCount}, bindingPaths={BindingPathCount}, modifiers={ModifierCount}, sections={SectionHeaderCount}, " +
                    $"healthy={LooksHealthy}, score={StrengthScore}, reason={GetHealthReason()}, lastWriteUtc={LastWriteTimeUtc:O}";
            }

            public string DescribeCompact()
            {
                return
                    $"{Path.GetFileName(FilePath)}(healthy={LooksHealthy}, len={Length}, sections={SectionHeaderCount}, maps={BindingMapCount}, actions={ActionNameCount}, " +
                    $"score={StrengthScore}, reason={GetHealthReason()})";
            }

            private string GetHealthReason()
            {
                if (!Exists)
                {
                    return "missing";
                }

                if (!string.IsNullOrEmpty(ReadFailure))
                {
                    return $"read-failed:{ReadFailure}";
                }

                if (LooksHealthy)
                {
                    return "healthy";
                }

                List<string> reasons = new List<string>();
                if (Length < MinimumHealthyFileLengthBytes)
                {
                    reasons.Add($"length<{MinimumHealthyFileLengthBytes}");
                }

                if (!HasGeneralSettings)
                {
                    reasons.Add("missing-general");
                }

                if (!HasGraphicsSettings)
                {
                    reasons.Add("missing-graphics");
                }

                if (!HasKeybindingSettingsSection)
                {
                    reasons.Add("missing-keybinding-section");
                }

                if (!HasBindingsProperty)
                {
                    reasons.Add("missing-bindings-property");
                }

                return reasons.Count > 0
                    ? string.Join(",", reasons.ToArray())
                    : "insufficient-structure";
            }
        }
    }
}
