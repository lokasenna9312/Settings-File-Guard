using System;
using System.IO;
using System.Text.RegularExpressions;
using Colossal;

namespace Settings_File_Guard
{
    internal static class ContinueGameProtectionService
    {
        private const string BackupFileName = "continue_game.json.settings_file_guard.bak";
        private const long MinimumHealthyFileLengthBytes = 16;
        private static readonly DateTime MinimumReasonableContinueGameDateLocal = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Local);
        private static readonly Regex TitleRegex = CreateJsonStringPropertyRegex("title");
        private static readonly Regex DescriptionRegex = CreateJsonStringPropertyRegex("desc");
        private static readonly Regex DateRegex = CreateJsonStringPropertyRegex("date");
        private static readonly Regex RawGameVersionRegex = CreateJsonStringPropertyRegex("rawGameVersion");

        private static readonly object s_Gate = new object();

        private static bool s_SessionInitialized;
        private static bool s_DeletedDuringShutdown;
        private static bool s_LoggedMissingBackupAfterDeletion;
        private static int s_RestoreAttempts;
        private static DateTime s_SessionStartedUtc;
        private static string s_LastHealthyDescription;

        public static void InitializeSession()
        {
            lock (s_Gate)
            {
                s_SessionInitialized = true;
                s_SessionStartedUtc = DateTime.UtcNow;
                s_DeletedDuringShutdown = false;
                s_LoggedMissingBackupAfterDeletion = false;
                s_RestoreAttempts = 0;
                s_LastHealthyDescription = "none";
                CaptureHealthyContinueGameLocked("startup baseline", logMissing: false);
            }
        }

        public static void CaptureHealthyContinueGame(string reason)
        {
            lock (s_Gate)
            {
                if (!s_SessionInitialized)
                {
                    InitializeSession();
                }

                CaptureHealthyContinueGameLocked(reason, logMissing: true);
            }
        }

        public static void ObserveSettingsDirectoryEvent(string kind, string path, string oldPath)
        {
            if (!TouchesContinueGame(path) && !TouchesContinueGame(oldPath))
            {
                return;
            }

            lock (s_Gate)
            {
                if (!s_SessionInitialized)
                {
                    return;
                }

                bool deletedCurrentPath =
                    string.Equals(kind, "Deleted", StringComparison.OrdinalIgnoreCase) &&
                    TouchesContinueGame(path);
                bool renamedAway =
                    string.Equals(kind, "Renamed", StringComparison.OrdinalIgnoreCase) &&
                    TouchesContinueGame(oldPath) &&
                    !TouchesContinueGame(path);

                if (deletedCurrentPath || renamedAway)
                {
                    s_DeletedDuringShutdown = true;
                    string message =
                        "[CONTINUE_GAME] Observed continue_game.json disappearing during shutdown tracking. " +
                        $"kind={kind}, path={path ?? "null"}, oldPath={oldPath ?? "null"}, sessionStartedUtc={s_SessionStartedUtc:O}, " +
                        $"lastHealthy={s_LastHealthyDescription}, backup={DescribeFileState(BackupFilePath)}";
                    Mod.log.Warn(message);
                    GuardDiagnostics.WriteEvent("CONTINUE_GAME", message);
                    return;
                }

                if (TouchesContinueGame(path))
                {
                    CaptureHealthyContinueGameLocked($"watcher-{kind}", logMissing: false);
                }
            }
        }

        public static void TryRestoreDeletedContinueGame(string reason, bool isTerminalPhase)
        {
            lock (s_Gate)
            {
                if (!s_SessionInitialized || !s_DeletedDuringShutdown)
                {
                    return;
                }

                ContinueGameFileAnalysis currentAnalysis = AnalyzeContinueGameFile(GuardPaths.ContinueGameFilePath);
                if (currentAnalysis.LooksHealthy)
                {
                    s_DeletedDuringShutdown = false;
                    s_LoggedMissingBackupAfterDeletion = false;

                    string skipMessage =
                        "[CONTINUE_GAME] Skipped continue_game.json restore because the file is healthy again. " +
                        $"reason={reason}, current={currentAnalysis.Describe()}, backup={DescribeFileState(BackupFilePath)}";
                    Mod.log.Info(skipMessage);
                    GuardDiagnostics.WriteEvent("CONTINUE_GAME", skipMessage);
                    return;
                }

                if (currentAnalysis.Exists)
                {
                    if (TryRepairContinueGameInPlace(
                            GuardPaths.ContinueGameFilePath,
                            currentAnalysis,
                            reason,
                            isTerminalPhase,
                            "current"))
                    {
                        s_DeletedDuringShutdown = false;
                        s_LoggedMissingBackupAfterDeletion = false;
                    }

                    return;
                }

                ContinueGameFileAnalysis backupAnalysis = EnsureHealthyBackupAnalysisLocked(reason);
                if (!backupAnalysis.LooksHealthy)
                {
                    if (!s_LoggedMissingBackupAfterDeletion)
                    {
                        s_LoggedMissingBackupAfterDeletion = true;
                        string missingBackupMessage =
                            "[CONTINUE_GAME] Unable to restore continue_game.json because no healthy backup is available. " +
                            $"reason={reason}, current={currentAnalysis.Describe()}, backup={backupAnalysis.Describe()}, lastHealthy={s_LastHealthyDescription}";
                        Mod.log.Warn(missingBackupMessage);
                        GuardDiagnostics.WriteEvent("CONTINUE_GAME", missingBackupMessage);
                    }

                    return;
                }

                s_RestoreAttempts += 1;
                try
                {
                    Directory.CreateDirectory(GuardPaths.SettingsDirectoryPath);
                    File.Copy(BackupFilePath, GuardPaths.ContinueGameFilePath, overwrite: true);

                    ContinueGameFileAnalysis restoredAnalysis = AnalyzeContinueGameFile(GuardPaths.ContinueGameFilePath);
                    s_DeletedDuringShutdown = false;
                    s_LoggedMissingBackupAfterDeletion = false;
                    s_LastHealthyDescription = restoredAnalysis.Describe();

                    string restoredMessage =
                        "[CONTINUE_GAME] Restored continue_game.json from backup. " +
                        $"reason={reason}, terminalPhase={isTerminalPhase}, attempt={s_RestoreAttempts}, restored={restoredAnalysis.Describe()}, backup={DescribeFileState(BackupFilePath)}";
                    Mod.log.Warn(restoredMessage);
                    GuardDiagnostics.WriteEvent("CONTINUE_GAME", restoredMessage);
                    GuardDiagnostics.DumpFileSnapshot(
                        "CONTINUE_GAME",
                        $"continue-game-restored-{s_RestoreAttempts:00}",
                        GuardPaths.ContinueGameFilePath,
                        restoredAnalysis.Describe());
                }
                catch (IOException ex) when (SettingsFileProtectionService.IsSharingViolation(ex))
                {
                    string deferredMessage =
                        "[CONTINUE_GAME] Deferred continue_game.json restore because the file is still locked. " +
                        $"reason={reason}, terminalPhase={isTerminalPhase}, attempt={s_RestoreAttempts}, exception={ex.Message}";
                    Mod.log.Warn(deferredMessage);
                    GuardDiagnostics.WriteEvent("CONTINUE_GAME", deferredMessage);
                }
                catch (Exception ex)
                {
                    string failureMessage =
                        "[CONTINUE_GAME] Failed to restore continue_game.json from backup. " +
                        $"reason={reason}, terminalPhase={isTerminalPhase}, attempt={s_RestoreAttempts}, exception={ex}";
                    Mod.log.Error(ex, failureMessage);
                    GuardDiagnostics.WriteEvent("CONTINUE_GAME", failureMessage);
                }
            }
        }

        public static bool TryResolveCurrentContinueSaveGuid(out Hash128 guid, out string sourceDescription)
        {
            lock (s_Gate)
            {
                ContinueGameFileAnalysis analysis = AnalyzeContinueGameFile(GuardPaths.ContinueGameFilePath);
                if (!analysis.LooksHealthy)
                {
                    analysis = EnsureHealthyBackupAnalysisLocked("resolve-guid");
                }

                if (!analysis.LooksHealthy)
                {
                    guid = default;
                    sourceDescription = $"no-healthy-continue-metadata:{analysis.Reason}";
                    return false;
                }

                if (!TryParseReasonableContinueGameDate(analysis.DateValue, out DateTime continueDateLocal))
                {
                    guid = default;
                    sourceDescription = $"invalid-continue-date:{analysis.DateValue ?? "null"}";
                    return false;
                }

                if (!TryResolveContinueSaveCandidate(analysis, continueDateLocal, out ContinueSaveCandidate candidate))
                {
                    guid = default;
                    sourceDescription =
                        $"no-matching-save:title={analysis.TitleValue ?? "null"}, date={analysis.DateValue ?? "null"}";
                    return false;
                }

                guid = candidate.Guid;
                sourceDescription =
                    $"continue_game.json -> {candidate.SavePath} (guid={candidate.Guid}, deltaMs={candidate.DateDelta.TotalMilliseconds:0}, titleMatch={candidate.TitleMatched})";
                return true;
            }
        }

        private static void CaptureHealthyContinueGameLocked(string reason, bool logMissing)
        {
            ContinueGameFileAnalysis analysis = AnalyzeContinueGameFile(GuardPaths.ContinueGameFilePath);
            if (!analysis.LooksHealthy && analysis.CanRepairDate)
            {
                analysis = WriteRepairedContinueGameFile(
                    BackupFilePath,
                    analysis,
                    $"capture-repair:{reason}",
                    "backup",
                    logAsWarning: false);
            }

            if (!analysis.LooksHealthy)
            {
                if (logMissing)
                {
                    string skippedMessage =
                        "[CONTINUE_GAME] Skipped continue_game.json backup because the current file does not look healthy. " +
                        $"reason={reason}, current={analysis.Describe()}, backup={DescribeFileState(BackupFilePath)}";
                    Mod.log.Info(skippedMessage);
                    GuardDiagnostics.WriteEvent("CONTINUE_GAME", skippedMessage);
                }

                return;
            }

            try
            {
                Directory.CreateDirectory(GuardPaths.SettingsDirectoryPath);
                if (string.Equals(analysis.FilePath, BackupFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    s_LastHealthyDescription = analysis.Describe();
                }
                else
                {
                    File.Copy(GuardPaths.ContinueGameFilePath, BackupFilePath, overwrite: true);
                    analysis = AnalyzeContinueGameFile(BackupFilePath);
                    s_LastHealthyDescription = analysis.Describe();
                }

                string message =
                    "[CONTINUE_GAME] Captured healthy continue_game.json backup. " +
                    $"reason={reason}, current={DescribeFileState(GuardPaths.ContinueGameFilePath)}, backup={DescribeFileState(BackupFilePath)}";
                Mod.log.Info(message);
                GuardDiagnostics.WriteEvent("CONTINUE_GAME", message);
            }
            catch (IOException ex) when (SettingsFileProtectionService.IsSharingViolation(ex))
            {
                string deferredMessage =
                    "[CONTINUE_GAME] Deferred continue_game.json backup because the file is still locked. " +
                    $"reason={reason}, current={analysis.Describe()}, exception={ex.Message}";
                Mod.log.Warn(deferredMessage);
                GuardDiagnostics.WriteEvent("CONTINUE_GAME", deferredMessage);
            }
            catch (Exception ex)
            {
                string failureMessage =
                    "[CONTINUE_GAME] Failed to capture continue_game.json backup. " +
                    $"reason={reason}, current={analysis.Describe()}, exception={ex}";
                Mod.log.Error(ex, failureMessage);
                GuardDiagnostics.WriteEvent("CONTINUE_GAME", failureMessage);
            }
        }

        private static ContinueGameFileAnalysis AnalyzeContinueGameFile(string path)
        {
            ContinueGameFileAnalysis analysis = new ContinueGameFileAnalysis(path);
            if (!File.Exists(path))
            {
                analysis.Reason = "missing";
                return analysis;
            }

            try
            {
                FileInfo info = new FileInfo(path);
                analysis.Exists = true;
                analysis.Length = info.Length;
                analysis.LastWriteTimeUtc = info.LastWriteTimeUtc;

                string text = File.ReadAllText(path);
                string trimmed = text == null ? string.Empty : text.Trim();
                analysis.StartsWithJsonObject = trimmed.StartsWith("{", StringComparison.Ordinal);
                analysis.EndsWithJsonObject = trimmed.EndsWith("}", StringComparison.Ordinal);
                analysis.HasJsonSeparator = trimmed.IndexOf(':') >= 0;
                analysis.TitleToken = TryExtractJsonStringToken(trimmed, TitleRegex, out string titleValue);
                analysis.DescriptionToken = TryExtractJsonStringToken(trimmed, DescriptionRegex, out string descriptionValue);
                analysis.DateToken = TryExtractJsonStringToken(trimmed, DateRegex, out string dateValue);
                analysis.RawGameVersionToken = TryExtractJsonStringToken(trimmed, RawGameVersionRegex, out string rawGameVersionValue);
                analysis.HasTitle = !string.IsNullOrWhiteSpace(titleValue);
                analysis.HasDescription = !string.IsNullOrWhiteSpace(descriptionValue);
                analysis.HasDate = !string.IsNullOrWhiteSpace(dateValue);
                analysis.HasRawGameVersion = !string.IsNullOrWhiteSpace(rawGameVersionValue);
                analysis.TitleValue = titleValue ?? string.Empty;
                analysis.DescriptionValue = descriptionValue ?? string.Empty;
                analysis.DateValue = dateValue ?? string.Empty;
                analysis.RawGameVersionValue = rawGameVersionValue ?? string.Empty;
                analysis.DateLooksPlausible =
                    analysis.HasDate &&
                    TryParseReasonableContinueGameDate(dateValue, out _);
                analysis.LooksHealthy =
                    analysis.Length >= MinimumHealthyFileLengthBytes &&
                    analysis.StartsWithJsonObject &&
                    analysis.EndsWithJsonObject &&
                    analysis.HasJsonSeparator &&
                    analysis.HasTitle &&
                    analysis.HasDescription &&
                    analysis.HasDate &&
                    analysis.HasRawGameVersion &&
                    analysis.DateLooksPlausible;

                analysis.CanRepairDate =
                    analysis.Length >= MinimumHealthyFileLengthBytes &&
                    analysis.StartsWithJsonObject &&
                    analysis.EndsWithJsonObject &&
                    analysis.HasJsonSeparator &&
                    analysis.HasTitle &&
                    analysis.HasDescription &&
                    analysis.HasRawGameVersion &&
                    !analysis.DateLooksPlausible &&
                    info.LastWriteTime != DateTime.MinValue;

                if (analysis.CanRepairDate)
                {
                    analysis.RepairedContent = BuildNormalizedContinueGameJson(
                        analysis.TitleToken,
                        analysis.DescriptionToken,
                        info.LastWriteTime,
                        analysis.RawGameVersionToken);
                }

                analysis.Reason = GetContinueGameHealthReason(analysis);
            }
            catch (Exception ex)
            {
                analysis.Reason = $"read-failed:{ex.GetType().Name}:{ex.Message}";
            }

            return analysis;
        }

        private static bool TouchesContinueGame(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string normalizedPath = Path.GetFullPath(path);
                if (string.Equals(normalizedPath, GuardPaths.ContinueGameFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string fileName = Path.GetFileName(normalizedPath);
                if (string.Equals(fileName, "continue_game.json", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return
                    normalizedPath.EndsWith(Path.DirectorySeparatorChar + "continue_game.json", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.EndsWith(Path.AltDirectorySeparatorChar + "continue_game.json", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeFileState(string path)
        {
            return AnalyzeContinueGameFile(path).Describe();
        }

        private static bool TryResolveContinueSaveCandidate(
            ContinueGameFileAnalysis analysis,
            DateTime continueDateLocal,
            out ContinueSaveCandidate bestCandidate)
        {
            bestCandidate = default;
            if (!Directory.Exists(GuardPaths.SavesDirectoryPath))
            {
                return false;
            }

            string titleValue = analysis.TitleValue?.Trim();
            bool found = false;

            foreach (string cidPath in Directory.EnumerateFiles(GuardPaths.SavesDirectoryPath, "*.cok.cid", SearchOption.AllDirectories))
            {
                if (!TryReadContinueSaveCandidate(cidPath, titleValue, continueDateLocal, out ContinueSaveCandidate candidate))
                {
                    continue;
                }

                if (!found || IsBetterContinueSaveCandidate(candidate, bestCandidate))
                {
                    bestCandidate = candidate;
                    found = true;
                }
            }

            return found;
        }

        private static bool TryReadContinueSaveCandidate(
            string cidPath,
            string titleValue,
            DateTime continueDateLocal,
            out ContinueSaveCandidate candidate)
        {
            candidate = default;

            try
            {
                string savePath = cidPath.EndsWith(".cid", StringComparison.OrdinalIgnoreCase)
                    ? cidPath.Substring(0, cidPath.Length - 4)
                    : cidPath;
                if (!File.Exists(savePath))
                {
                    return false;
                }

                string cidText = File.ReadAllText(cidPath).Trim();
                if (string.IsNullOrWhiteSpace(cidText) || !Hash128.TryParse(cidText, out Hash128 guid))
                {
                    return false;
                }

                FileInfo saveInfo = new FileInfo(savePath);
                TimeSpan dateDelta = (saveInfo.LastWriteTime - continueDateLocal).Duration();
                string saveName = Path.GetFileNameWithoutExtension(savePath);
                bool titleMatched =
                    !string.IsNullOrWhiteSpace(titleValue) &&
                    saveName.IndexOf(titleValue, StringComparison.OrdinalIgnoreCase) >= 0;

                candidate = new ContinueSaveCandidate(
                    savePath,
                    cidPath,
                    guid,
                    saveInfo.LastWriteTime,
                    dateDelta,
                    titleMatched);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBetterContinueSaveCandidate(ContinueSaveCandidate candidate, ContinueSaveCandidate currentBest)
        {
            if (candidate.TitleMatched != currentBest.TitleMatched)
            {
                return candidate.TitleMatched;
            }

            int candidateDeltaComparison = candidate.DateDelta.CompareTo(currentBest.DateDelta);
            if (candidateDeltaComparison != 0)
            {
                return candidateDeltaComparison < 0;
            }

            return candidate.LastWriteTimeLocal > currentBest.LastWriteTimeLocal;
        }

        private static ContinueGameFileAnalysis EnsureHealthyBackupAnalysisLocked(string reason)
        {
            ContinueGameFileAnalysis backupAnalysis = AnalyzeContinueGameFile(BackupFilePath);
            if (!backupAnalysis.LooksHealthy && backupAnalysis.CanRepairDate)
            {
                backupAnalysis = WriteRepairedContinueGameFile(
                    BackupFilePath,
                    backupAnalysis,
                    $"backup-repair:{reason}",
                    "backup",
                    logAsWarning: true);
            }

            return backupAnalysis;
        }

        private static bool TryRepairContinueGameInPlace(
            string targetPath,
            ContinueGameFileAnalysis analysis,
            string reason,
            bool isTerminalPhase,
            string targetLabel)
        {
            if (!analysis.CanRepairDate)
            {
                return false;
            }

            ContinueGameFileAnalysis repairedAnalysis = WriteRepairedContinueGameFile(
                targetPath,
                analysis,
                $"repair:{reason}",
                targetLabel,
                logAsWarning: true);
            if (!repairedAnalysis.LooksHealthy)
            {
                return false;
            }

            string repairedMessage =
                "[CONTINUE_GAME] Repaired continue_game.json with a normalized timestamp. " +
                $"reason={reason}, terminalPhase={isTerminalPhase}, target={targetLabel}, repaired={repairedAnalysis.Describe()}";
            Mod.log.Warn(repairedMessage);
            GuardDiagnostics.WriteEvent("CONTINUE_GAME", repairedMessage);
            return true;
        }

        private static ContinueGameFileAnalysis WriteRepairedContinueGameFile(
            string targetPath,
            ContinueGameFileAnalysis sourceAnalysis,
            string reason,
            string targetLabel,
            bool logAsWarning)
        {
            if (!sourceAnalysis.CanRepairDate || string.IsNullOrWhiteSpace(sourceAnalysis.RepairedContent))
            {
                return sourceAnalysis;
            }

            try
            {
                string directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(targetPath, sourceAnalysis.RepairedContent);
                ContinueGameFileAnalysis repairedAnalysis = AnalyzeContinueGameFile(targetPath);
                string repairedMessage =
                    "[CONTINUE_GAME] Wrote repaired continue_game metadata with a normalized timestamp. " +
                    $"reason={reason}, target={targetLabel}, source={sourceAnalysis.Describe()}, repaired={repairedAnalysis.Describe()}";
                if (logAsWarning)
                {
                    Mod.log.Warn(repairedMessage);
                }
                else
                {
                    Mod.log.Info(repairedMessage);
                }

                GuardDiagnostics.WriteEvent("CONTINUE_GAME", repairedMessage);
                return repairedAnalysis;
            }
            catch (IOException ex) when (SettingsFileProtectionService.IsSharingViolation(ex))
            {
                string deferredMessage =
                    "[CONTINUE_GAME] Deferred continue_game repair because the file is still locked. " +
                    $"reason={reason}, target={targetLabel}, exception={ex.Message}";
                Mod.log.Warn(deferredMessage);
                GuardDiagnostics.WriteEvent("CONTINUE_GAME", deferredMessage);
            }
            catch (Exception ex)
            {
                string failureMessage =
                    "[CONTINUE_GAME] Failed to repair continue_game metadata. " +
                    $"reason={reason}, target={targetLabel}, exception={ex}";
                Mod.log.Error(ex, failureMessage);
                GuardDiagnostics.WriteEvent("CONTINUE_GAME", failureMessage);
            }

            return sourceAnalysis;
        }

        private static string BuildNormalizedContinueGameJson(
            string titleToken,
            string descriptionToken,
            DateTime lastWriteTimeLocal,
            string rawGameVersionToken)
        {
            return
                "{\n" +
                $"    \"title\": {titleToken},\n" +
                $"    \"desc\": {descriptionToken},\n" +
                $"    \"date\": \"{lastWriteTimeLocal:yyyy-MM-ddTHH:mm:ss}\",\n" +
                $"    \"rawGameVersion\": {rawGameVersionToken}\n" +
                "}\n";
        }

        private static string GetContinueGameHealthReason(ContinueGameFileAnalysis analysis)
        {
            if (analysis.LooksHealthy)
            {
                return "healthy";
            }

            if (!analysis.StartsWithJsonObject || !analysis.EndsWithJsonObject || !analysis.HasJsonSeparator)
            {
                return "invalid-json-shape";
            }

            if (!analysis.HasTitle || !analysis.HasDescription || !analysis.HasRawGameVersion)
            {
                return "missing-required-fields";
            }

            if (!analysis.HasDate)
            {
                return analysis.CanRepairDate ? "missing-date-repairable" : "missing-date";
            }

            if (!analysis.DateLooksPlausible)
            {
                return analysis.CanRepairDate ? "implausible-date-repairable" : "implausible-date";
            }

            return "invalid-json-content";
        }

        private static string TryExtractJsonStringToken(string text, Regex regex, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            Match match = regex.Match(text);
            if (!match.Success)
            {
                return null;
            }

            value = match.Groups["value"].Value;
            return match.Groups["token"].Value;
        }

        private static bool TryParseReasonableContinueGameDate(string value, out DateTime parsedLocal)
        {
            parsedLocal = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!DateTime.TryParse(value, out parsedLocal))
            {
                return false;
            }

            if (parsedLocal.Kind == DateTimeKind.Utc)
            {
                parsedLocal = parsedLocal.ToLocalTime();
            }

            return
                parsedLocal >= MinimumReasonableContinueGameDateLocal &&
                parsedLocal <= DateTime.Now.AddYears(1);
        }

        private static Regex CreateJsonStringPropertyRegex(string propertyName)
        {
            return new Regex(
                $"\\\"{Regex.Escape(propertyName)}\\\"\\s*:\\s*(?<token>\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\")",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        private static string BackupFilePath => Path.Combine(GuardPaths.SettingsDirectoryPath, BackupFileName);

        private struct ContinueGameFileAnalysis
        {
            public ContinueGameFileAnalysis(string filePath)
            {
                FilePath = filePath;
                Exists = false;
                Length = 0;
                LastWriteTimeUtc = DateTime.MinValue;
                StartsWithJsonObject = false;
                EndsWithJsonObject = false;
                HasJsonSeparator = false;
                HasTitle = false;
                HasDescription = false;
                HasDate = false;
                HasRawGameVersion = false;
                DateLooksPlausible = false;
                CanRepairDate = false;
                LooksHealthy = false;
                DateValue = string.Empty;
                TitleValue = string.Empty;
                DescriptionValue = string.Empty;
                RawGameVersionValue = string.Empty;
                TitleToken = null;
                DescriptionToken = null;
                DateToken = null;
                RawGameVersionToken = null;
                RepairedContent = null;
                Reason = "unknown";
            }

            public string FilePath { get; }

            public bool Exists { get; set; }

            public long Length { get; set; }

            public DateTime LastWriteTimeUtc { get; set; }

            public bool StartsWithJsonObject { get; set; }

            public bool EndsWithJsonObject { get; set; }

            public bool HasJsonSeparator { get; set; }

            public bool HasTitle { get; set; }

            public bool HasDescription { get; set; }

            public bool HasDate { get; set; }

            public bool HasRawGameVersion { get; set; }

            public bool DateLooksPlausible { get; set; }

            public bool CanRepairDate { get; set; }

            public bool LooksHealthy { get; set; }

            public string DateValue { get; set; }

            public string TitleValue { get; set; }

            public string DescriptionValue { get; set; }

            public string RawGameVersionValue { get; set; }

            public string TitleToken { get; set; }

            public string DescriptionToken { get; set; }

            public string DateToken { get; set; }

            public string RawGameVersionToken { get; set; }

            public string RepairedContent { get; set; }

            public string Reason { get; set; }

            public string Describe()
            {
                return
                    $"file={FilePath}, exists={Exists}, length={Length}, startsWithObject={StartsWithJsonObject}, " +
                    $"endsWithObject={EndsWithJsonObject}, hasSeparator={HasJsonSeparator}, hasTitle={HasTitle}, hasDescription={HasDescription}, " +
                    $"hasDate={HasDate}, dateLooksPlausible={DateLooksPlausible}, hasRawGameVersion={HasRawGameVersion}, canRepairDate={CanRepairDate}, " +
                    $"healthy={LooksHealthy}, reason={Reason}, titleValue={TitleValue ?? "null"}, dateValue={DateValue ?? "null"}, lastWriteUtc={LastWriteTimeUtc:O}";
            }
        }

        private readonly struct ContinueSaveCandidate
        {
            public ContinueSaveCandidate(
                string savePath,
                string cidPath,
                Hash128 guid,
                DateTime lastWriteTimeLocal,
                TimeSpan dateDelta,
                bool titleMatched)
            {
                SavePath = savePath;
                CidPath = cidPath;
                Guid = guid;
                LastWriteTimeLocal = lastWriteTimeLocal;
                DateDelta = dateDelta;
                TitleMatched = titleMatched;
            }

            public string SavePath { get; }

            public string CidPath { get; }

            public Hash128 Guid { get; }

            public DateTime LastWriteTimeLocal { get; }

            public TimeSpan DateDelta { get; }

            public bool TitleMatched { get; }
        }
    }
}
