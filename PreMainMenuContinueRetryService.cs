using System;
using System.Threading.Tasks;
using Colossal;
using Colossal.Core;
using Colossal.Serialization.Entities;
using Game;
using Game.SceneFlow;

namespace Settings_File_Guard
{
    internal static class PreMainMenuContinueRetryService
    {
        private static readonly object s_Gate = new object();
        private static readonly TimeSpan StartupTrackingWindow = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan PreMainMenuRetryWindow = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PreMainMenuRetryBackoff = TimeSpan.FromMilliseconds(250);

        private static bool s_SessionInitialized;
        private static bool s_MainMenuSeen;
        private static bool s_StartupLoadObserved;
        private static bool s_StartupLoadFailed;
        private static bool s_LoadCompletedSuccessfully;
        private static bool s_RetryInProgress;
        private static bool s_IssuingRetryLoad;
        private static DateTime s_SessionStartedUtc;
        private static DateTime s_FirstFailureUtc;
        private static DateTime s_LastRetryAttemptUtc;
        private static int s_RetryAttemptCount;
        private static Hash128 s_TargetGuid;
        private static string s_TargetSource;
        private static string s_TargetSummary;

        public static void InitializeSession()
        {
            lock (s_Gate)
            {
                s_SessionInitialized = true;
                s_MainMenuSeen = false;
                s_StartupLoadObserved = false;
                s_StartupLoadFailed = false;
                s_LoadCompletedSuccessfully = false;
                s_RetryInProgress = false;
                s_IssuingRetryLoad = false;
                s_SessionStartedUtc = DateTime.UtcNow;
                s_FirstFailureUtc = DateTime.MinValue;
                s_LastRetryAttemptUtc = DateTime.MinValue;
                s_RetryAttemptCount = 0;
                s_TargetGuid = default;
                s_TargetSource = "none";
                s_TargetSummary = "none";
            }
        }

        public static void RecordAutoLoadGuid(Hash128 guid, string source)
        {
            RecordLoadGuid(GameMode.Game, Purpose.LoadGame, guid, source);
        }

        public static void RecordLoadGuid(GameMode mode, Purpose purpose, Hash128 guid, string source)
        {
            if (!IsTrackedLoad(mode, purpose) || IsDefaultGuid(guid))
            {
                return;
            }

            lock (s_Gate)
            {
                if (!ShouldTrackStartupLoadLocked())
                {
                    return;
                }

                s_StartupLoadObserved = true;
                s_TargetGuid = guid;
                s_TargetSource = source ?? "unknown";
                s_TargetSummary = guid.ToString();

                string message =
                    "[CONTINUE_RETRY] Observed startup save-load target. " +
                    $"source={s_TargetSource}, guid={s_TargetSummary}, state={DescribeStateLocked()}";
                Mod.log.Info(message);
                GuardDiagnostics.WriteEvent("CONTINUE_RETRY", message);
            }
        }

        public static void ObserveLoadTask(Task<bool> loadTask, string source, bool isRetry)
        {
            if (loadTask == null)
            {
                ObserveLoadResult(success: false, source, isRetry, "task-null");
                return;
            }

            loadTask.ContinueWith(
                task =>
                {
                    if (task.IsCanceled)
                    {
                        ObserveLoadResult(success: false, source, isRetry, "canceled");
                        return;
                    }

                    if (task.IsFaulted)
                    {
                        ObserveLoadResult(success: false, source, isRetry, task.Exception?.GetBaseException().ToString() ?? "faulted");
                        return;
                    }

                    ObserveLoadResult(task.Result, source, isRetry, task.Result ? null : "returned-false");
                },
                TaskContinuationOptions.ExecuteSynchronously);
        }

        public static bool ShouldIgnorePatchedLoadObservation()
        {
            lock (s_Gate)
            {
                return s_IssuingRetryLoad;
            }
        }

        public static bool TryInterceptMainMenu(GameManager manager)
        {
            if (manager == null)
            {
                return false;
            }

            Hash128 retryGuid;
            string retrySummary;
            int retryAttempt;
            TimeSpan remainingWindow;

            lock (s_Gate)
            {
                if (!s_SessionInitialized)
                {
                    return false;
                }

                if (!CanRetryBeforeMainMenuLocked(DateTime.UtcNow))
                {
                    if (ShouldLogRetryExhaustedLocked())
                    {
                        string exhaustedMessage =
                            "[CONTINUE_RETRY] Startup continue retry window ended before a successful load. " +
                            $"Allowing the normal main menu flow. state={DescribeStateLocked()}";
                        Mod.log.Warn(exhaustedMessage);
                        GuardDiagnostics.WriteEvent("CONTINUE_RETRY", exhaustedMessage);
                    }

                    s_MainMenuSeen = true;
                    return false;
                }

                s_RetryInProgress = true;
                s_IssuingRetryLoad = true;
                s_RetryAttemptCount += 1;
                s_LastRetryAttemptUtc = DateTime.UtcNow;
                retryGuid = s_TargetGuid;
                retrySummary = s_TargetSummary;
                retryAttempt = s_RetryAttemptCount;
                remainingWindow = RemainingRetryWindowLocked(s_LastRetryAttemptUtc);
            }

            try
            {
                string message =
                    "[CONTINUE_RETRY] Intercepted GameManager.MainMenu before the menu was shown. " +
                    $"Re-trying the startup continue target. attempt={retryAttempt}, remainingWindowMs={Math.Max(0, (int)remainingWindow.TotalMilliseconds)}, guid={retrySummary}";
                Mod.log.Warn(message);
                GuardDiagnostics.WriteEvent("CONTINUE_RETRY", message);

                Task<bool> retryTask = manager.Load(GameMode.Game, Purpose.LoadGame, retryGuid);
                ObserveLoadTask(retryTask, "GameManager.MainMenu-preemptive-retry", isRetry: true);
                return true;
            }
            catch (Exception ex)
            {
                lock (s_Gate)
                {
                    s_RetryInProgress = false;
                    s_IssuingRetryLoad = false;
                    s_MainMenuSeen = true;
                }

                string failureMessage =
                    "[CONTINUE_RETRY] Failed to start the pre-main-menu continue retry. " +
                    $"guid={retrySummary}, exception={ex}";
                Mod.log.Error(ex, failureMessage);
                GuardDiagnostics.WriteEvent("CONTINUE_RETRY", failureMessage);
                return false;
            }
            finally
            {
                lock (s_Gate)
                {
                    s_IssuingRetryLoad = false;
                }
            }
        }

        public static void MarkMainMenuSeen(string source)
        {
            lock (s_Gate)
            {
                s_MainMenuSeen = true;
                string message =
                    "[CONTINUE_RETRY] Main menu transition was allowed to proceed. " +
                    $"source={source}, state={DescribeStateLocked()}";
                GuardDiagnostics.WriteEvent("CONTINUE_RETRY", message);
            }
        }

        private static void ObserveLoadResult(bool success, string source, bool isRetry, string detail)
        {
            bool requestMainMenuFallback = false;

            lock (s_Gate)
            {
                if (!s_SessionInitialized)
                {
                    return;
                }

                if (success)
                {
                    s_LoadCompletedSuccessfully = true;
                    s_StartupLoadFailed = false;
                    s_RetryInProgress = false;

                    string successMessage =
                        "[CONTINUE_RETRY] Save-load completed successfully. " +
                        $"source={source}, retry={isRetry}, state={DescribeStateLocked()}";
                    Mod.log.Info(successMessage);
                    GuardDiagnostics.WriteEvent("CONTINUE_RETRY", successMessage);
                    return;
                }

                if (isRetry)
                {
                    s_RetryInProgress = false;
                    s_StartupLoadFailed = true;
                    if (s_FirstFailureUtc == DateTime.MinValue)
                    {
                        s_FirstFailureUtc = DateTime.UtcNow;
                    }

                    requestMainMenuFallback = !s_MainMenuSeen;
                }
                else if (s_StartupLoadObserved && !s_LoadCompletedSuccessfully && !s_MainMenuSeen)
                {
                    s_StartupLoadFailed = true;
                    if (s_FirstFailureUtc == DateTime.MinValue)
                    {
                        s_FirstFailureUtc = DateTime.UtcNow;
                    }
                }

                string failureMessage =
                    "[CONTINUE_RETRY] Save-load attempt failed. " +
                    $"source={source}, retry={isRetry}, detail={detail ?? "none"}, state={DescribeStateLocked()}";
                Mod.log.Warn(failureMessage);
                GuardDiagnostics.WriteEvent("CONTINUE_RETRY", failureMessage);
            }

            if (requestMainMenuFallback)
            {
                GameManager manager = GameManager.instance;
                if (manager == null)
                {
                    return;
                }

                Task.Delay(PreMainMenuRetryBackoff).ContinueWith(
                    _ =>
                    {
                        MainThreadDispatcher.RunOnMainThread(
                            () =>
                            {
                                string message =
                                    "[CONTINUE_RETRY] The pre-main-menu retry failed. Re-entering the pre-main-menu retry gate.";
                                Mod.log.Warn(message);
                                GuardDiagnostics.WriteEvent("CONTINUE_RETRY", message);
                                _ = manager.MainMenu();
                            });
                    },
                    TaskScheduler.Default);
            }
        }

        private static bool ShouldTrackStartupLoadLocked()
        {
            return
                s_SessionInitialized &&
                !s_MainMenuSeen &&
                !s_LoadCompletedSuccessfully &&
                !s_IssuingRetryLoad &&
                DateTime.UtcNow - s_SessionStartedUtc <= StartupTrackingWindow;
        }

        private static bool CanRetryBeforeMainMenuLocked(DateTime nowUtc)
        {
            if (!s_StartupLoadObserved ||
                !s_StartupLoadFailed ||
                s_LoadCompletedSuccessfully ||
                s_MainMenuSeen ||
                s_RetryInProgress ||
                IsDefaultGuid(s_TargetGuid))
            {
                return false;
            }

            if (s_FirstFailureUtc == DateTime.MinValue)
            {
                return false;
            }

            if (nowUtc - s_FirstFailureUtc > PreMainMenuRetryWindow)
            {
                return false;
            }

            if (s_LastRetryAttemptUtc != DateTime.MinValue && nowUtc - s_LastRetryAttemptUtc < PreMainMenuRetryBackoff)
            {
                return false;
            }

            return true;
        }

        private static bool ShouldLogRetryExhaustedLocked()
        {
            return
                s_StartupLoadObserved &&
                s_StartupLoadFailed &&
                !s_LoadCompletedSuccessfully &&
                !s_MainMenuSeen &&
                !s_RetryInProgress;
        }

        private static TimeSpan RemainingRetryWindowLocked(DateTime nowUtc)
        {
            if (s_FirstFailureUtc == DateTime.MinValue)
            {
                return PreMainMenuRetryWindow;
            }

            TimeSpan remaining = PreMainMenuRetryWindow - (nowUtc - s_FirstFailureUtc);
            return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        private static bool IsTrackedLoad(GameMode mode, Purpose purpose)
        {
            return mode == GameMode.Game && purpose == Purpose.LoadGame;
        }

        private static bool IsDefaultGuid(Hash128 guid)
        {
            return string.Equals(guid.ToString(), default(Hash128).ToString(), StringComparison.Ordinal);
        }

        private static string DescribeStateLocked()
        {
            return
                $"mainMenuSeen={s_MainMenuSeen}, startupLoadObserved={s_StartupLoadObserved}, startupLoadFailed={s_StartupLoadFailed}, " +
                $"loadCompletedSuccessfully={s_LoadCompletedSuccessfully}, retryAttemptCount={s_RetryAttemptCount}, retryInProgress={s_RetryInProgress}, " +
                $"firstFailureUtc={(s_FirstFailureUtc == DateTime.MinValue ? "none" : s_FirstFailureUtc.ToString("O"))}, " +
                $"target={s_TargetSummary}, targetSource={s_TargetSource}";
        }
    }
}
