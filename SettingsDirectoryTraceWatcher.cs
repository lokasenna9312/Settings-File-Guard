using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Settings_File_Guard
{
    internal static class SettingsDirectoryTraceWatcher
    {
        private const int MaxLoggedEventsPerSession = 48;
        private const int MaxRetainedRecentEvents = 16;
        private const int RecentEventWindowMilliseconds = 5000;

        private static readonly object s_Gate = new object();
        private static readonly List<DirectoryTraceEvent> s_RecentEvents = new List<DirectoryTraceEvent>();

        private static FileSystemWatcher s_Watcher;
        private static bool s_IsArmed;
        private static int s_LoggedEventCount;
        private static bool s_SuppressedMessageLogged;

        public static void Arm(string reason)
        {
            lock (s_Gate)
            {
                DisarmLocked();

                s_IsArmed = true;
                s_LoggedEventCount = 0;
                s_SuppressedMessageLogged = false;
                s_RecentEvents.Clear();

                try
                {
                    s_Watcher = new FileSystemWatcher(GuardPaths.SettingsDirectoryPath)
                    {
                        Filter = "*",
                        IncludeSubdirectories = false,
                        InternalBufferSize = 16 * 1024,
                        NotifyFilter =
                            NotifyFilters.FileName |
                            NotifyFilters.DirectoryName |
                            NotifyFilters.LastWrite |
                            NotifyFilters.Size |
                            NotifyFilters.CreationTime,
                    };
                    s_Watcher.Changed += OnChanged;
                    s_Watcher.Created += OnCreated;
                    s_Watcher.Deleted += OnDeleted;
                    s_Watcher.Renamed += OnRenamed;
                    s_Watcher.EnableRaisingEvents = true;

                    string message =
                        "[KEYBIND_TRACE] Armed settings-directory watcher. " +
                        $"reason={reason}, path={GuardPaths.SettingsDirectoryPath}, window={DescribeRecentActivityLocked(DateTime.UtcNow)}";
                    Mod.log.Info(message);
                    GuardDiagnostics.WriteEvent("DIR_TRACE", message);
                }
                catch (Exception ex)
                {
                    string message =
                        "[KEYBIND_TRACE] Failed to arm settings-directory watcher. " +
                        $"reason={reason}, path={GuardPaths.SettingsDirectoryPath}, exception={ex}";
                    Mod.log.Warn(message);
                    GuardDiagnostics.WriteEvent("DIR_TRACE", message);
                    DisarmLocked();
                }
            }
        }

        public static void Disarm()
        {
            lock (s_Gate)
            {
                DisarmLocked();
            }
        }

        public static string DescribeRecentActivity()
        {
            lock (s_Gate)
            {
                return DescribeRecentActivityLocked(DateTime.UtcNow);
            }
        }

        private static void OnChanged(object sender, FileSystemEventArgs args)
        {
            RecordEvent("Changed", args?.FullPath, null);
        }

        private static void OnCreated(object sender, FileSystemEventArgs args)
        {
            RecordEvent("Created", args?.FullPath, null);
        }

        private static void OnDeleted(object sender, FileSystemEventArgs args)
        {
            RecordEvent("Deleted", args?.FullPath, null);
        }

        private static void OnRenamed(object sender, RenamedEventArgs args)
        {
            RecordEvent("Renamed", args?.FullPath, args?.OldFullPath);
        }

        private static void RecordEvent(string kind, string fullPath, string oldFullPath)
        {
            lock (s_Gate)
            {
                if (!s_IsArmed)
                {
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                DirectoryTraceEvent traceEvent = new DirectoryTraceEvent(
                    observedUtc: nowUtc,
                    kind: kind,
                    path: NormalizePath(fullPath),
                    oldPath: NormalizePath(oldFullPath),
                    fileState: DescribeFileState(fullPath));

                s_RecentEvents.Add(traceEvent);
                PruneRecentEventsLocked(nowUtc);

                if (s_LoggedEventCount < MaxLoggedEventsPerSession)
                {
                    s_LoggedEventCount += 1;
                    string message =
                        "[KEYBIND_TRACE] Settings-directory watcher observed an event. " +
                        $"event={traceEvent}, tracking={ShutdownWriteTracker.DescribeTrackingState()}, window={DescribeRecentActivityLocked(nowUtc)}";
                    Mod.log.Info(message);
                    GuardDiagnostics.WriteEvent("DIR_TRACE", message);
                }
                else if (!s_SuppressedMessageLogged)
                {
                    s_SuppressedMessageLogged = true;
                    string message =
                        "[KEYBIND_TRACE] Settings-directory watcher reached the per-session event log limit. " +
                        $"window={DescribeRecentActivityLocked(nowUtc)}";
                    Mod.log.Info(message);
                    GuardDiagnostics.WriteEvent("DIR_TRACE", message);
                }
            }
        }

        private static string DescribeRecentActivityLocked(DateTime nowUtc)
        {
            PruneRecentEventsLocked(nowUtc);

            string[] recent = s_RecentEvents
                .OrderByDescending(entry => entry.ObservedUtc)
                .Take(6)
                .Select(entry => entry.Describe(nowUtc))
                .ToArray();

            return
                $"armed={s_IsArmed}, recentEventCount={s_RecentEvents.Count}, loggedEventCount={s_LoggedEventCount}, recent={JoinOrNone(recent)}";
        }

        private static void PruneRecentEventsLocked(DateTime nowUtc)
        {
            s_RecentEvents.RemoveAll(
                entry => (nowUtc - entry.ObservedUtc).TotalMilliseconds > RecentEventWindowMilliseconds);
            if (s_RecentEvents.Count <= MaxRetainedRecentEvents)
            {
                return;
            }

            s_RecentEvents.RemoveRange(0, s_RecentEvents.Count - MaxRetainedRecentEvents);
        }

        private static FileTraceState DescribeFileState(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return FileTraceState.Missing;
            }

            try
            {
                string normalizedPath = NormalizePath(path);
                if (!File.Exists(normalizedPath))
                {
                    return FileTraceState.Missing;
                }

                FileInfo info = new FileInfo(normalizedPath);
                return new FileTraceState(true, info.Length, info.LastWriteTimeUtc, readFailure: null);
            }
            catch (Exception ex)
            {
                return new FileTraceState(
                    exists: false,
                    length: 0,
                    lastWriteTimeUtc: DateTime.MinValue,
                    readFailure: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static string JoinOrNone(string[] values)
        {
            return values != null && values.Length > 0 ? string.Join(" ; ", values) : "none";
        }

        private static void DisarmLocked()
        {
            s_IsArmed = false;

            FileSystemWatcher watcher = s_Watcher;
            s_Watcher = null;
            if (watcher != null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnChanged;
                watcher.Created -= OnCreated;
                watcher.Deleted -= OnDeleted;
                watcher.Renamed -= OnRenamed;
                watcher.Dispose();
            }
        }

        private readonly struct DirectoryTraceEvent
        {
            public DirectoryTraceEvent(DateTime observedUtc, string kind, string path, string oldPath, FileTraceState fileState)
            {
                ObservedUtc = observedUtc;
                Kind = kind;
                Path = path;
                OldPath = oldPath;
                FileState = fileState;
            }

            public DateTime ObservedUtc { get; }

            public string Kind { get; }

            public string Path { get; }

            public string OldPath { get; }

            public FileTraceState FileState { get; }

            public string Describe(DateTime nowUtc)
            {
                long ageMilliseconds = (long)Math.Max(0, (nowUtc - ObservedUtc).TotalMilliseconds);
                string oldPathText = string.IsNullOrWhiteSpace(OldPath) ? string.Empty : $", oldPath={OldPath}";
                return $"{Kind}(ageMs={ageMilliseconds}, path={Path ?? "null"}{oldPathText}, file={FileState})";
            }

            public override string ToString()
            {
                return Describe(DateTime.UtcNow);
            }
        }

        private readonly struct FileTraceState
        {
            public static readonly FileTraceState Missing = new FileTraceState(false, 0, DateTime.MinValue, readFailure: null);

            public FileTraceState(bool exists, long length, DateTime lastWriteTimeUtc, string readFailure)
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

            public override string ToString()
            {
                return
                    $"exists={Exists}, length={Length}, lastWriteUtc={LastWriteTimeUtc:O}, readFailure={ReadFailure ?? "none"}";
            }
        }
    }
}
