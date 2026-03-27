using System;
using System.IO;
using System.Threading;

namespace Settings_File_Guard
{
    internal static class ShutdownWriteTracker
    {
        private const int PollIntervalMilliseconds = 100;
        private const int TrackingLongRunNoticeMilliseconds = 15000;
        private const int MaxSnapshotsPerSession = 12;
        private const int FailSafeQuietPeriodMilliseconds = 350;
        private const int EpisodeGapMilliseconds = 250;

        private static readonly object s_Gate = new object();

        private static bool s_Initialized;
        private static bool s_IsTracking;
        private static string s_Reason;
        private static DateTime s_StartedUtc;
        private static TrackedFileState s_LastObservedState;
        private static int s_ChangeCount;
        private static int s_SnapshotCount;
        private static Timer s_Timer;
        private static DateTime s_LastChangeObservedUtc;
        private static bool s_FailSafePending;
        private static bool s_FailSafeWaitingForLock;
        private static bool s_FailSafeAttemptInProgress;
        private static int s_FailSafeRecoveryAttempts;
        private static int s_CurrentEpisodeId;
        private static int s_CurrentEpisodeChangeCount;
        private static int s_LastSeenSettingsSessionSerial;
        private static int s_LastSeenStreamSessionSerial;
        private static bool s_LoggedLongRunningTrackingNotice;

        public static bool IsTracking
        {
            get
            {
                lock (s_Gate)
                {
                    return s_IsTracking;
                }
            }
        }

        public static string DescribeTrackingState()
        {
            lock (s_Gate)
            {
                return DescribeTrackingStateLocked();
            }
        }

        public static void Initialize()
        {
            lock (s_Gate)
            {
                if (s_Initialized)
                {
                    return;
                }

                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                AppDomain.CurrentDomain.DomainUnload += OnDomainUnload;
                s_Initialized = true;
            }
        }

        public static void Arm(string reason)
        {
            lock (s_Gate)
            {
                Initialize();

                if (s_IsTracking)
                {
                    UpdateTrackingReason(reason, "arm-refresh");
                    return;
                }

                s_IsTracking = true;
                s_Reason = reason;
                s_StartedUtc = DateTime.UtcNow;
                s_ChangeCount = 0;
                s_SnapshotCount = 0;
                s_LastChangeObservedUtc = DateTime.MinValue;
                s_FailSafePending = false;
                s_FailSafeWaitingForLock = false;
                s_FailSafeAttemptInProgress = false;
                s_FailSafeRecoveryAttempts = 0;
                s_CurrentEpisodeId = 0;
                s_CurrentEpisodeChangeCount = 0;
                s_LastSeenSettingsSessionSerial = AssetDatabaseSettingsTracePatches.CurrentSettingsSessionSerial;
                s_LastSeenStreamSessionSerial = SettingsFileIoTracePatches.CurrentStreamSessionSerial;
                s_LastObservedState = CaptureCurrentState();
                s_LoggedLongRunningTrackingNotice = false;
                SettingsDirectoryTraceWatcher.Arm(reason);
                s_Timer = new Timer(PollForLateWrites, null, PollIntervalMilliseconds, PollIntervalMilliseconds);

                string message =
                    $"[KEYBIND_TRACE] Armed shutdown write tracking. reason={reason}, state={DescribeTrackingStateLocked()}, baseline={s_LastObservedState}, saveWindow={AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow()}, streamWindow={SettingsFileIoTracePatches.DescribeSettingsWriteWindow()}, directoryWindow={SettingsDirectoryTraceWatcher.DescribeRecentActivity()}";
                Mod.log.Info(message);
                GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
                DumpSnapshotIfPossible("shutdown-track-armed", s_LastObservedState);
            }
        }

        public static void NoteCheckpoint(string reason)
        {
            lock (s_Gate)
            {
                Initialize();

                if (!s_IsTracking)
                {
                    Arm(reason);
                    return;
                }

                UpdateTrackingReason(reason, "checkpoint");
            }
        }

        private static void OnProcessExit(object sender, EventArgs args)
        {
            CaptureTerminalEvent("ProcessExit");
        }

        private static void OnDomainUnload(object sender, EventArgs args)
        {
            CaptureTerminalEvent("DomainUnload");
        }

        private static void CaptureTerminalEvent(string phase)
        {
            RunFailSafeRecovery($"terminal-{phase}", ignoreQuietPeriod: true, isTerminalPhase: true);
            ContinueGameProtectionService.TryRestoreDeletedContinueGame($"terminal-{phase}", isTerminalPhase: true);

            lock (s_Gate)
            {
                if (!s_IsTracking)
                {
                    return;
                }

                TrackedFileState currentState = CaptureCurrentState();
                s_LastObservedState = currentState;
                string message =
                    $"[KEYBIND_TRACE] Shutdown lifecycle event. phase={phase}, state={DescribeTrackingStateLocked()}, current={currentState}, saveWindow={AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow()}, streamWindow={SettingsFileIoTracePatches.DescribeSettingsWriteWindow()}, directoryWindow={SettingsDirectoryTraceWatcher.DescribeRecentActivity()}";
                Mod.log.Info(message);
                GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
                DumpSnapshotIfPossible($"shutdown-track-{phase}", currentState);
                StopTrackingTimerLocked();
            }
        }

        private static void PollForLateWrites(object state)
        {
            bool shouldAttemptImmediate = false;
            string immediateTrigger = null;

            lock (s_Gate)
            {
                if (!s_IsTracking)
                {
                    return;
                }

                TrackedFileState currentState = CaptureCurrentState();
                if (!currentState.Equals(s_LastObservedState))
                {
                    DateTime observedUtc = DateTime.UtcNow;
                    long gapMilliseconds = s_LastChangeObservedUtc == DateTime.MinValue
                        ? -1
                        : (long)Math.Max(0, (observedUtc - s_LastChangeObservedUtc).TotalMilliseconds);
                    int currentSettingsSessionSerial = AssetDatabaseSettingsTracePatches.CurrentSettingsSessionSerial;
                    int currentStreamSessionSerial = SettingsFileIoTracePatches.CurrentStreamSessionSerial;
                    bool newSettingsSessionSeen = currentSettingsSessionSerial != s_LastSeenSettingsSessionSerial;
                    bool newStreamSessionSeen = currentStreamSessionSerial != s_LastSeenStreamSessionSerial;
                    string episodeReason = ClassifyEpisodeBoundaryLocked(gapMilliseconds, newSettingsSessionSeen, newStreamSessionSeen);
                    if (!string.IsNullOrEmpty(episodeReason))
                    {
                        s_CurrentEpisodeId += 1;
                        s_CurrentEpisodeChangeCount = 0;
                    }

                    s_ChangeCount += 1;
                    s_CurrentEpisodeChangeCount += 1;
                    s_LastChangeObservedUtc = observedUtc;
                    s_FailSafePending = true;
                    string saveWindow = AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow();
                    string streamWindow = SettingsFileIoTracePatches.DescribeSettingsWriteWindow();
                    string directoryWindow = SettingsDirectoryTraceWatcher.DescribeRecentActivity();
                    TrackedFileState previousState = s_LastObservedState;
                    bool currentHasHardRestoreFailure =
                        SettingsFileProtectionService.CurrentSettingsFileHasHardRestoreFailure(out string currentAnalysisDescription);
                    s_LastObservedState = currentState;
                    s_LastSeenSettingsSessionSerial = currentSettingsSessionSerial;
                    s_LastSeenStreamSessionSerial = currentStreamSessionSerial;
                    string message =
                        "[KEYBIND_TRACE] Detected Settings.coc change during shutdown tracking. " +
                        $"state={DescribeTrackingStateLocked()}, episode={s_CurrentEpisodeId}:{s_CurrentEpisodeChangeCount}, episodeReason={episodeReason ?? "same-episode"}, " +
                        $"gapMs={gapMilliseconds}, newSettingsSessionSeen={newSettingsSessionSeen}, newStreamSessionSeen={newStreamSessionSeen}, " +
                        $"saveWindow={saveWindow}, streamWindow={streamWindow}, directoryWindow={directoryWindow}, previous={previousState}, current={currentState}, currentAnalysis={currentAnalysisDescription}";
                    Mod.log.Warn(message);
                    GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
                    DumpSnapshotIfPossible($"shutdown-track-change-{s_ChangeCount:00}", currentState);

                    if (currentHasHardRestoreFailure)
                    {
                        string immediateMessage =
                            "[KEYBIND_TRACE] Detected hard-restore corruption during shutdown tracking and scheduled " +
                            $"an immediate restore attempt outside the tracker lock. trigger=change-immediate-hard-failure, state={DescribeTrackingStateLocked()}, " +
                            $"episode={s_CurrentEpisodeId}:{s_CurrentEpisodeChangeCount}, currentAnalysis={currentAnalysisDescription}, " +
                            $"saveWindow={saveWindow}, streamWindow={streamWindow}, directoryWindow={directoryWindow}";
                        Mod.log.Warn(immediateMessage);
                        GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", immediateMessage);
                        shouldAttemptImmediate = true;
                        immediateTrigger = "change-immediate-hard-failure";
                    }
                }

                TryLogLongRunningTrackingNoticeLocked();
            }

            if (shouldAttemptImmediate)
            {
                RunFailSafeRecovery(immediateTrigger, ignoreQuietPeriod: true, isTerminalPhase: false);
                ContinueGameProtectionService.TryRestoreDeletedContinueGame(immediateTrigger, isTerminalPhase: false);
                return;
            }

            RunFailSafeRecovery("poll", ignoreQuietPeriod: false, isTerminalPhase: false);
            ContinueGameProtectionService.TryRestoreDeletedContinueGame("poll", isTerminalPhase: false);
        }

        private static void UpdateTrackingReason(string reason, string source)
        {
            s_Reason = reason;
            TrackedFileState currentState = CaptureCurrentState();
            s_LastObservedState = currentState;

            string message =
                $"[KEYBIND_TRACE] Updated shutdown tracking checkpoint. source={source}, state={DescribeTrackingStateLocked()}, current={currentState}, saveWindow={AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow()}, streamWindow={SettingsFileIoTracePatches.DescribeSettingsWriteWindow()}, directoryWindow={SettingsDirectoryTraceWatcher.DescribeRecentActivity()}";
            Mod.log.Info(message);
            GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
            DumpSnapshotIfPossible($"shutdown-track-{SanitizeLabel(source)}", currentState);
        }

        private static void RunFailSafeRecovery(string trigger, bool ignoreQuietPeriod, bool isTerminalPhase)
        {
            FailSafeAttemptContext attemptContext;
            lock (s_Gate)
            {
                if (!TryPrepareFailSafeRecoveryAttemptLocked(trigger, ignoreQuietPeriod, isTerminalPhase, out attemptContext))
                {
                    return;
                }

                string beginMessage =
                    "[KEYBIND_TRACE] Shutdown fail-safe is attempting restore from healthy backup. " +
                    $"reason={attemptContext.Reason}, trigger={trigger}, state={DescribeTrackingStateLocked()}, currentBefore={attemptContext.CurrentBeforeDescription}, " +
                    $"terminalPhase={isTerminalPhase}, waitingForLock={attemptContext.WasWaitingForLock}, saveWindow={attemptContext.SaveWindow}, " +
                    $"streamWindow={attemptContext.StreamWindow}, directoryWindow={attemptContext.DirectoryWindow}";
                Mod.log.Warn(beginMessage);
                GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", beginMessage);
                DumpSnapshotIfPossible($"shutdown-failsafe-before-{attemptContext.AttemptNumber:00}", attemptContext.BeforeState);
            }

            SettingsFileProtectionService.RestoreAttemptResult result =
                SettingsFileProtectionService.TryRestoreBackupIfCurrentLooksCorrupted(attemptContext.Reason);

            lock (s_Gate)
            {
                CompleteFailSafeRecoveryAttemptLocked(attemptContext, trigger, isTerminalPhase, result);
            }
        }

        private static bool TryPrepareFailSafeRecoveryAttemptLocked(
            string trigger,
            bool ignoreQuietPeriod,
            bool isTerminalPhase,
            out FailSafeAttemptContext attemptContext)
        {
            attemptContext = default(FailSafeAttemptContext);
            if (!s_IsTracking || s_FailSafeAttemptInProgress)
            {
                return false;
            }

            if (!s_FailSafePending && !s_FailSafeWaitingForLock && !ignoreQuietPeriod)
            {
                return false;
            }

            if (!isTerminalPhase && Environment.HasShutdownStarted)
            {
                return false;
            }

            bool currentHasHardRestoreFailure =
                SettingsFileProtectionService.CurrentSettingsFileHasHardRestoreFailure(out string currentDescription);
            if (!currentHasHardRestoreFailure)
            {
                if (s_FailSafePending || s_FailSafeWaitingForLock)
                {
                    string skipMessage =
                        "[KEYBIND_TRACE] Shutdown fail-safe cleared its pending restore because the file no longer meets the hard-restore threshold. " +
                        $"trigger={trigger}, state={DescribeTrackingStateLocked()}, current={currentDescription}, saveWindow={AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow()}, " +
                        $"streamWindow={SettingsFileIoTracePatches.DescribeSettingsWriteWindow()}, directoryWindow={SettingsDirectoryTraceWatcher.DescribeRecentActivity()}";
                    Mod.log.Info(skipMessage);
                    GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", skipMessage);
                }

                s_FailSafePending = false;
                s_FailSafeWaitingForLock = false;
                return false;
            }

            if (!s_FailSafeWaitingForLock &&
                !ignoreQuietPeriod &&
                s_LastChangeObservedUtc != DateTime.MinValue &&
                (DateTime.UtcNow - s_LastChangeObservedUtc).TotalMilliseconds < FailSafeQuietPeriodMilliseconds)
            {
                return false;
            }

            s_FailSafeAttemptInProgress = true;
            s_FailSafeRecoveryAttempts += 1;
            attemptContext = new FailSafeAttemptContext(
                attemptNumber: s_FailSafeRecoveryAttempts,
                reason: $"shutdown fail-safe attempt {s_FailSafeRecoveryAttempts} ({trigger})",
                currentBeforeDescription: currentDescription,
                beforeState: CaptureCurrentState(),
                saveWindow: AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow(),
                streamWindow: SettingsFileIoTracePatches.DescribeSettingsWriteWindow(),
                directoryWindow: SettingsDirectoryTraceWatcher.DescribeRecentActivity(),
                wasWaitingForLock: s_FailSafeWaitingForLock);
            return true;
        }

        private static void CompleteFailSafeRecoveryAttemptLocked(
            FailSafeAttemptContext attemptContext,
            string trigger,
            bool isTerminalPhase,
            SettingsFileProtectionService.RestoreAttemptResult result)
        {
            TrackedFileState recoveredState = CaptureCurrentState();
            s_LastObservedState = recoveredState;
            s_FailSafeAttemptInProgress = false;

            bool keepPending = result.CurrentHasHardRestoreFailure;
            bool keepWaitingForLock =
                result.Outcome == SettingsFileProtectionService.RestoreAttemptOutcome.DeferredSharingViolation &&
                keepPending &&
                !isTerminalPhase &&
                !Environment.HasShutdownStarted;

            if (result.Outcome == SettingsFileProtectionService.RestoreAttemptOutcome.Restored)
            {
                s_LastChangeObservedUtc = DateTime.UtcNow;
            }

            if (result.Outcome == SettingsFileProtectionService.RestoreAttemptOutcome.NoHealthyBackup ||
                result.Outcome == SettingsFileProtectionService.RestoreAttemptOutcome.Failed)
            {
                keepPending = false;
                keepWaitingForLock = false;
            }

            s_FailSafePending = keepPending;
            s_FailSafeWaitingForLock = keepWaitingForLock;

            string endMessage;
            switch (result.Outcome)
            {
                case SettingsFileProtectionService.RestoreAttemptOutcome.DeferredSharingViolation:
                    endMessage =
                        "[KEYBIND_TRACE] Shutdown fail-safe deferred restore because Settings.coc is still locked. " +
                        $"reason={attemptContext.Reason}, trigger={trigger}, state={DescribeTrackingStateLocked()}, currentAfter={result.CurrentDescription}, " +
                        $"willRetryWhileTracking={s_FailSafeWaitingForLock}, terminalPhase={isTerminalPhase}, saveWindow={AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow()}, " +
                        $"streamWindow={SettingsFileIoTracePatches.DescribeSettingsWriteWindow()}, directoryWindow={SettingsDirectoryTraceWatcher.DescribeRecentActivity()}";
                    Mod.log.Warn(endMessage);
                    break;
                case SettingsFileProtectionService.RestoreAttemptOutcome.Restored:
                    endMessage =
                        "[KEYBIND_TRACE] Shutdown fail-safe restore attempt finished. " +
                        $"reason={attemptContext.Reason}, trigger={trigger}, state={DescribeTrackingStateLocked()}, currentAfter={result.CurrentDescription}, " +
                        $"saveWindow={AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow()}, streamWindow={SettingsFileIoTracePatches.DescribeSettingsWriteWindow()}, directoryWindow={SettingsDirectoryTraceWatcher.DescribeRecentActivity()}";
                    if (s_FailSafePending)
                    {
                        Mod.log.Warn(endMessage);
                    }
                    else
                    {
                        Mod.log.Info(endMessage);
                    }

                    break;
                default:
                    endMessage =
                        "[KEYBIND_TRACE] Shutdown fail-safe attempt completed without performing a restore. " +
                        $"reason={attemptContext.Reason}, trigger={trigger}, outcome={result.Outcome}, state={DescribeTrackingStateLocked()}, currentAfter={result.CurrentDescription}, " +
                        $"detail={result.Detail ?? "none"}, saveWindow={AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow()}, streamWindow={SettingsFileIoTracePatches.DescribeSettingsWriteWindow()}, directoryWindow={SettingsDirectoryTraceWatcher.DescribeRecentActivity()}";
                    if (s_FailSafePending)
                    {
                        Mod.log.Warn(endMessage);
                    }
                    else
                    {
                        Mod.log.Info(endMessage);
                    }

                    break;
            }

            GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", endMessage);
            DumpSnapshotIfPossible($"shutdown-failsafe-after-{attemptContext.AttemptNumber:00}", recoveredState);
        }

        private static void TryLogLongRunningTrackingNoticeLocked()
        {
            if (s_LoggedLongRunningTrackingNotice ||
                !s_IsTracking ||
                (DateTime.UtcNow - s_StartedUtc).TotalMilliseconds < TrackingLongRunNoticeMilliseconds)
            {
                return;
            }

            s_LoggedLongRunningTrackingNotice = true;
            string message =
                $"[KEYBIND_TRACE] Shutdown write tracking is still active after {TrackingLongRunNoticeMilliseconds}ms and will remain armed until process shutdown events arrive. state={DescribeTrackingStateLocked()}, current={s_LastObservedState}, saveWindow={AssetDatabaseSettingsTracePatches.DescribeSettingsSaveWindow()}, streamWindow={SettingsFileIoTracePatches.DescribeSettingsWriteWindow()}, directoryWindow={SettingsDirectoryTraceWatcher.DescribeRecentActivity()}";
            Mod.log.Info(message);
            GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
        }

        private static void DumpSnapshotIfPossible(string label, TrackedFileState state)
        {
            if (s_SnapshotCount >= MaxSnapshotsPerSession)
            {
                return;
            }

            s_SnapshotCount += 1;
            GuardDiagnostics.DumpFileSnapshot(
                "SHUTDOWN_TRACE",
                label,
                GuardPaths.SettingsFilePath,
                $"{state}, analysis={SettingsFileProtectionService.DescribeSettingsFileForDiagnostics(GuardPaths.SettingsFilePath)}");
        }

        private static void StopTrackingTimerLocked()
        {
            s_IsTracking = false;
            s_FailSafePending = false;
            s_FailSafeWaitingForLock = false;
            s_FailSafeAttemptInProgress = false;
            SettingsDirectoryTraceWatcher.Disarm();
            Timer timer = s_Timer;
            s_Timer = null;
            timer?.Dispose();
        }

        private static string DescribeTrackingStateLocked()
        {
            long elapsedMilliseconds = s_IsTracking
                ? (long)Math.Max(0, (DateTime.UtcNow - s_StartedUtc).TotalMilliseconds)
                : 0;
            return
                $"tracking={s_IsTracking}, reason={s_Reason ?? "none"}, elapsedMs={elapsedMilliseconds}, changeCount={s_ChangeCount}, " +
                $"episode={s_CurrentEpisodeId}:{s_CurrentEpisodeChangeCount}, snapshotCount={s_SnapshotCount}, failSafePending={s_FailSafePending}, " +
                $"failSafeWaitingForLock={s_FailSafeWaitingForLock}, failSafeAttemptInProgress={s_FailSafeAttemptInProgress}, " +
                $"failSafeAttempts={s_FailSafeRecoveryAttempts}, settingsSessionSerial={s_LastSeenSettingsSessionSerial}, streamSessionSerial={s_LastSeenStreamSessionSerial}, shutdownStarted={Environment.HasShutdownStarted}";
        }

        private static string ClassifyEpisodeBoundaryLocked(long gapMilliseconds, bool newSettingsSessionSeen, bool newStreamSessionSeen)
        {
            if (s_ChangeCount == 0)
            {
                return "first-change";
            }

            if (newSettingsSessionSeen && newStreamSessionSeen)
            {
                return "new-settings-session-and-stream";
            }

            if (newSettingsSessionSeen)
            {
                return "new-settings-session";
            }

            if (newStreamSessionSeen)
            {
                return "new-stream-session";
            }

            if (gapMilliseconds >= EpisodeGapMilliseconds)
            {
                return "quiet-gap";
            }

            return null;
        }

        private static TrackedFileState CaptureCurrentState()
        {
            try
            {
                if (!File.Exists(GuardPaths.SettingsFilePath))
                {
                    return TrackedFileState.Missing;
                }

                FileInfo info = new FileInfo(GuardPaths.SettingsFilePath);
                return new TrackedFileState(true, info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception ex)
            {
                return new TrackedFileState(
                    exists: false,
                    length: 0,
                    lastWriteTimeUtc: DateTime.MinValue,
                    readFailure: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string SanitizeLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unnamed";
            }

            char[] characters = value.ToCharArray();
            for (int i = 0; i < characters.Length; i += 1)
            {
                char character = characters[i];
                if (!char.IsLetterOrDigit(character))
                {
                    characters[i] = '-';
                }
            }

            return new string(characters);
        }

        private readonly struct FailSafeAttemptContext
        {
            public FailSafeAttemptContext(
                int attemptNumber,
                string reason,
                string currentBeforeDescription,
                TrackedFileState beforeState,
                string saveWindow,
                string streamWindow,
                string directoryWindow,
                bool wasWaitingForLock)
            {
                AttemptNumber = attemptNumber;
                Reason = reason;
                CurrentBeforeDescription = currentBeforeDescription;
                BeforeState = beforeState;
                SaveWindow = saveWindow;
                StreamWindow = streamWindow;
                DirectoryWindow = directoryWindow;
                WasWaitingForLock = wasWaitingForLock;
            }

            public int AttemptNumber { get; }

            public string Reason { get; }

            public string CurrentBeforeDescription { get; }

            public TrackedFileState BeforeState { get; }

            public string SaveWindow { get; }

            public string StreamWindow { get; }

            public string DirectoryWindow { get; }

            public bool WasWaitingForLock { get; }
        }

        private readonly struct TrackedFileState
        {
            public static readonly TrackedFileState Missing = new TrackedFileState(false, 0, DateTime.MinValue);

            public TrackedFileState(bool exists, long length, DateTime lastWriteTimeUtc, string readFailure = null)
            {
                Exists = exists;
                Length = length;
                LastWriteTimeUtc = lastWriteTimeUtc;
                ReadFailure = readFailure;
            }

            public bool Exists { get; }

            public long Length { get; }

            public DateTime LastWriteTimeUtc { get; }

            public string ReadFailure { get; }

            public override bool Equals(object obj)
            {
                return obj is TrackedFileState other &&
                       Exists == other.Exists &&
                       Length == other.Length &&
                       LastWriteTimeUtc == other.LastWriteTimeUtc &&
                       string.Equals(ReadFailure, other.ReadFailure, StringComparison.Ordinal);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = Exists.GetHashCode();
                    hashCode = (hashCode * 397) ^ Length.GetHashCode();
                    hashCode = (hashCode * 397) ^ LastWriteTimeUtc.GetHashCode();
                    hashCode = (hashCode * 397) ^ (ReadFailure?.GetHashCode() ?? 0);
                    return hashCode;
                }
            }

            public override string ToString()
            {
                return
                    $"exists={Exists}, length={Length}, lastWriteUtc={LastWriteTimeUtc:O}, readFailure={ReadFailure ?? "none"}";
            }
        }
    }
}
