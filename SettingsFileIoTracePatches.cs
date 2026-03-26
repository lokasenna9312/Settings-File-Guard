using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace Settings_File_Guard
{
    internal static class SettingsFileIoTracePatches
    {
        private const string HarmonyId = "Settings_File_Guard.SettingsFileIoTracePatches";
        private const int MaxLoggedWritesPerStream = 8;

        private static readonly object s_Gate = new object();
        private static readonly string s_SettingsFilePath = NormalizePath(GuardPaths.SettingsFilePath);
        private static readonly ConstructorInfo[] s_FileStreamConstructors = typeof(FileStream)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(
                constructor =>
                {
                    ParameterInfo[] parameters = constructor.GetParameters();
                    return parameters.Length > 0 && parameters[0].ParameterType == typeof(string);
                })
            .ToArray();
        private static readonly MethodInfo s_FileStreamWriteMethod =
            AccessTools.Method(typeof(FileStream), nameof(FileStream.Write), new[] { typeof(byte[]), typeof(int), typeof(int) });
        private static readonly MethodInfo s_FileStreamSetLengthMethod =
            AccessTools.Method(typeof(FileStream), nameof(FileStream.SetLength), new[] { typeof(long) });
        private static readonly MethodInfo s_FileStreamDisposeMethod =
            AccessTools.Method(typeof(FileStream), "Dispose", new[] { typeof(bool) });
        private static readonly MethodInfo[] s_FileOperations = typeof(File)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Where(
                method =>
                    string.Equals(method.Name, nameof(File.Move), StringComparison.Ordinal) ||
                    string.Equals(method.Name, nameof(File.Replace), StringComparison.Ordinal) ||
                    string.Equals(method.Name, nameof(File.Copy), StringComparison.Ordinal) ||
                    string.Equals(method.Name, nameof(File.Delete), StringComparison.Ordinal))
            .ToArray();

        private static readonly Dictionary<object, TrackedStreamState> s_TrackedStreams = new Dictionary<object, TrackedStreamState>();

        private static Harmony s_Harmony;
        private static int s_NextStreamId;

        public static void Apply()
        {
            if (s_Harmony != null)
            {
                return;
            }

            try
            {
                s_Harmony = new Harmony(HarmonyId);

                HarmonyMethod constructorPrefix = new HarmonyMethod(typeof(SettingsFileIoTracePatches), nameof(FileStreamConstructorPrefix));
                HarmonyMethod constructorPostfix = new HarmonyMethod(typeof(SettingsFileIoTracePatches), nameof(FileStreamConstructorPostfix));
                foreach (ConstructorInfo constructor in s_FileStreamConstructors)
                {
                    s_Harmony.Patch(constructor, prefix: constructorPrefix, postfix: constructorPostfix);
                }

                if (s_FileStreamWriteMethod != null)
                {
                    s_Harmony.Patch(
                        s_FileStreamWriteMethod,
                        prefix: new HarmonyMethod(typeof(SettingsFileIoTracePatches), nameof(FileStreamWritePrefix)));
                }

                if (s_FileStreamSetLengthMethod != null)
                {
                    s_Harmony.Patch(
                        s_FileStreamSetLengthMethod,
                        prefix: new HarmonyMethod(typeof(SettingsFileIoTracePatches), nameof(FileStreamSetLengthPrefix)));
                }

                if (s_FileStreamDisposeMethod != null)
                {
                    s_Harmony.Patch(
                        s_FileStreamDisposeMethod,
                        prefix: new HarmonyMethod(typeof(SettingsFileIoTracePatches), nameof(FileStreamDisposePrefix)));
                }

                HarmonyMethod fileOperationPrefix = new HarmonyMethod(typeof(SettingsFileIoTracePatches), nameof(FileOperationPrefix));
                foreach (MethodInfo method in s_FileOperations)
                {
                    s_Harmony.Patch(method, prefix: fileOperationPrefix);
                }

                string targets =
                    $"constructors={s_FileStreamConstructors.Length}, writePatched={s_FileStreamWriteMethod != null}, " +
                    $"setLengthPatched={s_FileStreamSetLengthMethod != null}, disposePatched={s_FileStreamDisposeMethod != null}, fileOps={s_FileOperations.Length}";
                Mod.log.Info($"[KEYBIND_TRACE] Low-level Settings.coc file I/O trace patches applied. {targets}");
                GuardDiagnostics.WriteEvent("FILE_TRACE", $"Low-level Settings.coc file I/O trace patches applied. {targets}");
            }
            catch (Exception ex)
            {
                s_Harmony = null;
                Mod.log.Error(ex, "[KEYBIND_TRACE] Failed to apply low-level Settings.coc file I/O trace patches.");
                GuardDiagnostics.WriteEvent("FILE_TRACE", $"Failed to apply low-level Settings.coc file I/O trace patches. exception={ex}");
            }
        }

        private static void FileStreamConstructorPrefix(MethodBase __originalMethod, object[] __args, ref PendingStreamTrace __state)
        {
            __state = CreatePendingStreamTrace(__originalMethod, __args);
        }

        private static void FileStreamConstructorPostfix(object __instance, PendingStreamTrace __state)
        {
            if (__state == null || __instance == null)
            {
                return;
            }

            FileStream stream = __instance as FileStream;
            if (stream == null)
            {
                return;
            }

            TrackedStreamState state = new TrackedStreamState(
                __state.StreamId,
                __state.Path,
                __state.ConstructorDescription,
                __state.AccessDescription,
                __state.ShareDescription,
                __state.OpenedUtc,
                __state.OpenThreadId,
                __state.OpenStackTrace);

            lock (s_Gate)
            {
                s_TrackedStreams[stream] = state;
            }

            LogTrackedEvent(
                "Opened tracked FileStream for Settings.coc.",
                state,
                $"tracking={ShutdownWriteTracker.DescribeTrackingState()}, currentLength={TryGetStreamLength(stream)}, currentPosition={TryGetStreamPosition(stream)}, stack={state.OpenStackTrace}");
        }

        private static void FileStreamWritePrefix(object __instance, byte[] array, int offset, int count)
        {
            if (!TryGetTrackedStreamState(__instance, out TrackedStreamState state))
            {
                return;
            }

            bool shouldLog = false;
            bool shouldLogSuppression = false;
            int writeIndex = 0;
            long totalBytesWritten = 0;

            lock (s_Gate)
            {
                state.WriteCount += 1;
                state.TotalBytesWritten += count;
                writeIndex = state.WriteCount;
                totalBytesWritten = state.TotalBytesWritten;

                if (state.WriteCount <= MaxLoggedWritesPerStream)
                {
                    shouldLog = true;
                }
                else if (!state.SuppressedWriteLog)
                {
                    state.SuppressedWriteLog = true;
                    shouldLogSuppression = true;
                }
            }

            if (shouldLog)
            {
                LogTrackedEvent(
                    "Tracked FileStream.Write on Settings.coc.",
                    state,
                    $"tracking={ShutdownWriteTracker.DescribeTrackingState()}, writeIndex={writeIndex}, count={count}, offset={offset}, totalBytesWritten={totalBytesWritten}, currentLength={TryGetStreamLength(__instance as FileStream)}, currentPosition={TryGetStreamPosition(__instance as FileStream)}");
            }
            else if (shouldLogSuppression)
            {
                LogTrackedEvent(
                    "Further FileStream.Write logs for this tracked Settings.coc stream will be suppressed.",
                    state,
                    $"tracking={ShutdownWriteTracker.DescribeTrackingState()}, writesObserved={writeIndex}, totalBytesWritten={totalBytesWritten}");
            }
        }

        private static void FileStreamSetLengthPrefix(object __instance, long value)
        {
            if (!TryGetTrackedStreamState(__instance, out TrackedStreamState state))
            {
                return;
            }

            LogTrackedEvent(
                "Tracked FileStream.SetLength on Settings.coc.",
                state,
                $"tracking={ShutdownWriteTracker.DescribeTrackingState()}, requestedLength={value}, currentLength={TryGetStreamLength(__instance as FileStream)}, currentPosition={TryGetStreamPosition(__instance as FileStream)}");
        }

        private static void FileStreamDisposePrefix(object __instance, bool disposing)
        {
            if (!TryRemoveTrackedStreamState(__instance, out TrackedStreamState state))
            {
                return;
            }

            LogTrackedEvent(
                "Tracked FileStream.Dispose on Settings.coc.",
                state,
                $"disposing={disposing}, tracking={ShutdownWriteTracker.DescribeTrackingState()}, writesObserved={state.WriteCount}, totalBytesWritten={state.TotalBytesWritten}, finalLength={TryGetStreamLength(__instance as FileStream)}, finalPosition={TryGetStreamPosition(__instance as FileStream)}");
        }

        private static void FileOperationPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (!ShutdownWriteTracker.IsTracking || __originalMethod == null)
            {
                return;
            }

            if (!TryDescribeInterestingFileOperation(__originalMethod, __args, out string details))
            {
                return;
            }

            string message =
                $"Tracked file operation touching Settings.coc. tracking={ShutdownWriteTracker.DescribeTrackingState()}, " +
                $"operation={DescribeMethod(__originalMethod)}, details={details}, thread={Thread.CurrentThread.ManagedThreadId}, stack={CaptureCompactStackTrace()}";
            Mod.log.Warn($"[KEYBIND_TRACE] {message}");
            GuardDiagnostics.WriteEvent("FILE_TRACE", message);
        }

        private static PendingStreamTrace CreatePendingStreamTrace(MethodBase originalMethod, object[] args)
        {
            if (!ShutdownWriteTracker.IsTracking ||
                args == null ||
                args.Length == 0 ||
                !(args[0] is string path) ||
                !IsSettingsFilePath(path))
            {
                return null;
            }

            string constructorDescription = DescribeConstructorArguments(originalMethod, args, out bool isWriteCandidate, out string accessDescription, out string shareDescription);
            if (!isWriteCandidate)
            {
                return null;
            }

            return new PendingStreamTrace
            {
                StreamId = Interlocked.Increment(ref s_NextStreamId),
                Path = NormalizePath(path),
                ConstructorDescription = constructorDescription,
                AccessDescription = accessDescription,
                ShareDescription = shareDescription,
                OpenedUtc = DateTime.UtcNow,
                OpenThreadId = Thread.CurrentThread.ManagedThreadId,
                OpenStackTrace = CaptureCompactStackTrace(),
            };
        }

        private static string DescribeConstructorArguments(
            MethodBase originalMethod,
            object[] args,
            out bool isWriteCandidate,
            out string accessDescription,
            out string shareDescription)
        {
            string modeDescription = "unknown";
            accessDescription = "unknown";
            shareDescription = "unknown";
            isWriteCandidate = true;

            if (args.Length > 1)
            {
                object secondArgument = args[1];
                if (secondArgument is FileMode mode)
                {
                    modeDescription = mode.ToString();
                }
                else if (TryDescribeFileStreamOptions(secondArgument, out string optionMode, out string optionAccess, out string optionShare, out bool optionIsWriteCandidate))
                {
                    modeDescription = optionMode;
                    accessDescription = optionAccess;
                    shareDescription = optionShare;
                    isWriteCandidate = optionIsWriteCandidate;
                }
            }

            if (args.Length > 2 && args[2] is FileAccess access)
            {
                accessDescription = access.ToString();
                isWriteCandidate = access != FileAccess.Read;
            }

            if (args.Length > 3 && args[3] is FileShare share)
            {
                shareDescription = share.ToString();
            }

            return $"{DescribeMethod(originalMethod)}, mode={modeDescription}, access={accessDescription}, share={shareDescription}";
        }

        private static bool TryDescribeFileStreamOptions(
            object options,
            out string modeDescription,
            out string accessDescription,
            out string shareDescription,
            out bool isWriteCandidate)
        {
            modeDescription = "unknown";
            accessDescription = "unknown";
            shareDescription = "unknown";
            isWriteCandidate = true;

            if (options == null)
            {
                return false;
            }

            Type type = options.GetType();
            if (!string.Equals(type.FullName, "System.IO.FileStreamOptions", StringComparison.Ordinal))
            {
                return false;
            }

            modeDescription = GetPropertyValueText(options, "Mode");
            accessDescription = GetPropertyValueText(options, "Access");
            shareDescription = GetPropertyValueText(options, "Share");
            isWriteCandidate = !string.Equals(accessDescription, FileAccess.Read.ToString(), StringComparison.Ordinal);
            return true;
        }

        private static string GetPropertyValueText(object instance, string propertyName)
        {
            try
            {
                object value = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance, null);
                return value?.ToString() ?? "null";
            }
            catch
            {
                return "unavailable";
            }
        }

        private static bool TryDescribeInterestingFileOperation(MethodBase originalMethod, object[] args, out string details)
        {
            details = null;
            string methodName = originalMethod.Name;
            if (args == null)
            {
                return false;
            }

            if (string.Equals(methodName, nameof(File.Delete), StringComparison.Ordinal))
            {
                string path = GetStringArgument(args, 0);
                if (!IsSettingsFilePath(path))
                {
                    return false;
                }

                details = $"path={NormalizePath(path)}";
                return true;
            }

            if (string.Equals(methodName, nameof(File.Copy), StringComparison.Ordinal))
            {
                string sourcePath = GetStringArgument(args, 0);
                string destinationPath = GetStringArgument(args, 1);
                if (!IsSettingsFilePath(destinationPath))
                {
                    return false;
                }

                details = $"source={NormalizePath(sourcePath)}, destination={NormalizePath(destinationPath)}, overwrite={GetOptionalArgumentText(args, 2)}";
                return true;
            }

            if (string.Equals(methodName, nameof(File.Move), StringComparison.Ordinal))
            {
                string sourcePath = GetStringArgument(args, 0);
                string destinationPath = GetStringArgument(args, 1);
                if (!IsSettingsFilePath(sourcePath) && !IsSettingsFilePath(destinationPath))
                {
                    return false;
                }

                details = $"source={NormalizePath(sourcePath)}, destination={NormalizePath(destinationPath)}, overwrite={GetOptionalArgumentText(args, 2)}";
                return true;
            }

            if (string.Equals(methodName, nameof(File.Replace), StringComparison.Ordinal))
            {
                string sourcePath = GetStringArgument(args, 0);
                string destinationPath = GetStringArgument(args, 1);
                string backupPath = GetStringArgument(args, 2);
                if (!IsSettingsFilePath(sourcePath) && !IsSettingsFilePath(destinationPath) && !IsSettingsFilePath(backupPath))
                {
                    return false;
                }

                details =
                    $"source={NormalizePath(sourcePath)}, destination={NormalizePath(destinationPath)}, backup={NormalizePath(backupPath)}, ignoreMetadataErrors={GetOptionalArgumentText(args, 3)}";
                return true;
            }

            return false;
        }

        private static string GetStringArgument(object[] args, int index)
        {
            return args.Length > index ? args[index] as string : null;
        }

        private static string GetOptionalArgumentText(object[] args, int index)
        {
            return args.Length > index ? args[index]?.ToString() ?? "null" : "n/a";
        }

        private static bool TryGetTrackedStreamState(object instance, out TrackedStreamState state)
        {
            lock (s_Gate)
            {
                return s_TrackedStreams.TryGetValue(instance, out state);
            }
        }

        private static bool TryRemoveTrackedStreamState(object instance, out TrackedStreamState state)
        {
            lock (s_Gate)
            {
                if (!s_TrackedStreams.TryGetValue(instance, out state))
                {
                    return false;
                }

                s_TrackedStreams.Remove(instance);
                return true;
            }
        }

        private static void LogTrackedEvent(string title, TrackedStreamState state, string details)
        {
            string message =
                $"{title} streamId={state.StreamId}, path={state.Path}, open={state.ConstructorDescription}, openedUtc={state.OpenedUtc:O}, " +
                $"openThread={state.OpenThreadId}, details={details}";
            Mod.log.Warn($"[KEYBIND_TRACE] {message}");
            GuardDiagnostics.WriteEvent("FILE_TRACE", message);
        }

        private static long TryGetStreamLength(FileStream stream)
        {
            try
            {
                return stream?.Length ?? -1;
            }
            catch
            {
                return -1;
            }
        }

        private static long TryGetStreamPosition(FileStream stream)
        {
            try
            {
                return stream?.Position ?? -1;
            }
            catch
            {
                return -1;
            }
        }

        private static bool IsSettingsFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return string.Equals(NormalizePath(path), s_SettingsFilePath, StringComparison.OrdinalIgnoreCase);
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

        private static string DescribeMethod(MethodBase method)
        {
            if (method == null)
            {
                return "unknown";
            }

            string parameters = string.Join(
                ", ",
                method.GetParameters().Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}").ToArray());
            return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
        }

        private static string CaptureCompactStackTrace()
        {
            try
            {
                string[] lines = new StackTrace(2, false)
                    .ToString()
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !line.Contains(nameof(SettingsFileIoTracePatches)))
                    .Take(18)
                    .ToArray();
                return lines.Length > 0 ? string.Join(" <= ", lines) : "unavailable";
            }
            catch
            {
                return "unavailable";
            }
        }

        private sealed class PendingStreamTrace
        {
            public int StreamId { get; set; }

            public string Path { get; set; }

            public string ConstructorDescription { get; set; }

            public string AccessDescription { get; set; }

            public string ShareDescription { get; set; }

            public DateTime OpenedUtc { get; set; }

            public int OpenThreadId { get; set; }

            public string OpenStackTrace { get; set; }
        }

        private sealed class TrackedStreamState
        {
            public TrackedStreamState(
                int streamId,
                string path,
                string constructorDescription,
                string accessDescription,
                string shareDescription,
                DateTime openedUtc,
                int openThreadId,
                string openStackTrace)
            {
                StreamId = streamId;
                Path = path;
                ConstructorDescription = constructorDescription;
                AccessDescription = accessDescription;
                ShareDescription = shareDescription;
                OpenedUtc = openedUtc;
                OpenThreadId = openThreadId;
                OpenStackTrace = openStackTrace;
            }

            public int StreamId { get; }

            public string Path { get; }

            public string ConstructorDescription { get; }

            public string AccessDescription { get; }

            public string ShareDescription { get; }

            public DateTime OpenedUtc { get; }

            public int OpenThreadId { get; }

            public string OpenStackTrace { get; }

            public int WriteCount { get; set; }

            public long TotalBytesWritten { get; set; }

            public bool SuppressedWriteLog { get; set; }
        }
    }
}
