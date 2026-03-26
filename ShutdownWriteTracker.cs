using System;
using System.IO;
using System.Threading;

namespace Settings_File_Guard
{
    internal static class ShutdownWriteTracker
    {
        private const int PollIntervalMilliseconds = 100;
        private const int MaxTrackingDurationMilliseconds = 15000;
        private const int MaxSnapshotsPerSession = 12;
        private const int FailSafeQuietPeriodMilliseconds = 350;
        private const int MaxFailSafeRecoveryAttempts = 3;

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
        private static int s_FailSafeRecoveryAttempts;

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
                s_FailSafeRecoveryAttempts = 0;
                s_LastObservedState = CaptureCurrentState();
                s_Timer = new Timer(PollForLateWrites, null, PollIntervalMilliseconds, PollIntervalMilliseconds);

                string message =
                    $"[KEYBIND_TRACE] Armed shutdown write tracking. reason={reason}, state={DescribeTrackingStateLocked()}, baseline={s_LastObservedState}";
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
            lock (s_Gate)
            {
                TryRunFailSafeRecoveryLocked($"terminal-{phase}", ignoreQuietPeriod: true);
                TrackedFileState currentState = CaptureCurrentState();
                string message =
                    $"[KEYBIND_TRACE] Shutdown lifecycle event. phase={phase}, state={DescribeTrackingStateLocked()}, current={currentState}";
                Mod.log.Info(message);
                GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
                DumpSnapshotIfPossible($"shutdown-track-{phase}", currentState);
                StopTrackingTimer();
            }
        }

        private static void PollForLateWrites(object state)
        {
            lock (s_Gate)
            {
                if (!s_IsTracking)
                {
                    return;
                }

                TrackedFileState currentState = CaptureCurrentState();
                if (!currentState.Equals(s_LastObservedState))
                {
                    s_ChangeCount += 1;
                    s_LastChangeObservedUtc = DateTime.UtcNow;
                    s_FailSafePending = true;
                    string message =
                        $"[KEYBIND_TRACE] Detected Settings.coc change during shutdown tracking. state={DescribeTrackingStateLocked()}, previous={s_LastObservedState}, current={currentState}";
                    Mod.log.Warn(message);
                    GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
                    DumpSnapshotIfPossible($"shutdown-track-change-{s_ChangeCount:00}", currentState);
                    s_LastObservedState = currentState;
                }

                TryRunFailSafeRecoveryLocked("poll", ignoreQuietPeriod: false);

                if ((DateTime.UtcNow - s_StartedUtc).TotalMilliseconds >= MaxTrackingDurationMilliseconds)
                {
                    TryRunFailSafeRecoveryLocked("timeout", ignoreQuietPeriod: true);
                    string message =
                        $"[KEYBIND_TRACE] Shutdown write tracking expired. state={DescribeTrackingStateLocked()}, final={s_LastObservedState}";
                    Mod.log.Info(message);
                    GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
                    StopTrackingTimer();
                }
            }
        }

        private static void UpdateTrackingReason(string reason, string source)
        {
            s_Reason = reason;
            TrackedFileState currentState = CaptureCurrentState();
            s_LastObservedState = currentState;

            string message =
                $"[KEYBIND_TRACE] Updated shutdown tracking checkpoint. source={source}, state={DescribeTrackingStateLocked()}, current={currentState}";
            Mod.log.Info(message);
            GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", message);
            DumpSnapshotIfPossible($"shutdown-track-{SanitizeLabel(source)}", currentState);
        }

        private static void TryRunFailSafeRecoveryLocked(string trigger, bool ignoreQuietPeriod)
        {
            if (!s_FailSafePending && !ignoreQuietPeriod)
            {
                return;
            }

            if (s_FailSafeRecoveryAttempts >= MaxFailSafeRecoveryAttempts)
            {
                string exhaustedMessage =
                    "[KEYBIND_TRACE] Shutdown fail-safe reached the maximum recovery attempts and will stop retrying. " +
                    $"trigger={trigger}, state={DescribeTrackingStateLocked()}, current={SettingsFileProtectionService.DescribeSettingsFileForDiagnostics(GuardPaths.SettingsFilePath)}";
                Mod.log.Warn(exhaustedMessage);
                GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", exhaustedMessage);
                s_FailSafePending = false;
                return;
            }

            if (!ignoreQuietPeriod &&
                s_LastChangeObservedUtc != DateTime.MinValue &&
                (DateTime.UtcNow - s_LastChangeObservedUtc).TotalMilliseconds < FailSafeQuietPeriodMilliseconds)
            {
                return;
            }

            if (!SettingsFileProtectionService.CurrentSettingsFileHasHardRestoreFailure())
            {
                if (s_FailSafePending)
                {
                    string skipMessage =
                        "[KEYBIND_TRACE] Shutdown fail-safe skipped automatic restore because the file stabilized " +
                        $"without meeting the hard-restore threshold. trigger={trigger}, state={DescribeTrackingStateLocked()}, current={SettingsFileProtectionService.DescribeSettingsFileForDiagnostics(GuardPaths.SettingsFilePath)}";
                    Mod.log.Info(skipMessage);
                    GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", skipMessage);
                    s_FailSafePending = false;
                }

                return;
            }

            s_FailSafeRecoveryAttempts += 1;
            string reason = $"shutdown fail-safe attempt {s_FailSafeRecoveryAttempts} ({trigger})";
            string beforeDescription = SettingsFileProtectionService.DescribeSettingsFileForDiagnostics(GuardPaths.SettingsFilePath);
            string beginMessage =
                "[KEYBIND_TRACE] Shutdown fail-safe is attempting restore from healthy backup. " +
                $"reason={reason}, state={DescribeTrackingStateLocked()}, currentBefore={beforeDescription}";
            Mod.log.Warn(beginMessage);
            GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", beginMessage);
            DumpSnapshotIfPossible($"shutdown-failsafe-before-{s_FailSafeRecoveryAttempts:00}", CaptureCurrentState());

            SettingsFileProtectionService.RestoreBackupIfCurrentLooksCorrupted(reason);

            TrackedFileState recoveredState = CaptureCurrentState();
            s_LastObservedState = recoveredState;
            s_LastChangeObservedUtc = DateTime.UtcNow;
            s_FailSafePending = SettingsFileProtectionService.CurrentSettingsFileHasHardRestoreFailure();

            string endMessage =
                "[KEYBIND_TRACE] Shutdown fail-safe restore attempt finished. " +
                $"reason={reason}, state={DescribeTrackingStateLocked()}, currentAfter={SettingsFileProtectionService.DescribeSettingsFileForDiagnostics(GuardPaths.SettingsFilePath)}";
            if (s_FailSafePending)
            {
                Mod.log.Warn(endMessage);
            }
            else
            {
                Mod.log.Info(endMessage);
            }

            GuardDiagnostics.WriteEvent("SHUTDOWN_TRACE", endMessage);
            DumpSnapshotIfPossible($"shutdown-failsafe-after-{s_FailSafeRecoveryAttempts:00}", recoveredState);
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

        private static void StopTrackingTimer()
        {
            s_IsTracking = false;
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
                $"tracking={s_IsTracking}, reason={s_Reason ?? "none"}, elapsedMs={elapsedMilliseconds}, changeCount={s_ChangeCount}, snapshotCount={s_SnapshotCount}, failSafePending={s_FailSafePending}, failSafeAttempts={s_FailSafeRecoveryAttempts}";
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
