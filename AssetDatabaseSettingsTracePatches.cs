using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace Settings_File_Guard
{
    internal static class AssetDatabaseSettingsTracePatches
    {
        private const string HarmonyId = "Settings_File_Guard.AssetDatabaseSettingsTracePatches";
        private const int MaxLoggedWritesPerHelper = 24;
        private const int MaxLoggedFragmentsPerHelper = 32;

        private static readonly object s_Gate = new object();
        private static readonly string[] s_InterestingNameFragments =
        {
            "gameplay",
            "general",
            "graphics",
            "input",
            "keybinding",
            "user settings",
            "settings.coc",
        };

        private static readonly Type s_AssetDatabaseType =
            AccessTools.TypeByName("Colossal.IO.AssetDatabase.AssetDatabase");
        private static readonly Type s_SaveSettingsHelperType =
            AccessTools.TypeByName("Colossal.IO.AssetDatabase.Internal.SaveSettingsHelper");
        private static readonly Type s_FragmentType =
            AccessTools.TypeByName("Colossal.IO.AssetDatabase.SettingAsset+Fragment");
        private static readonly MethodInfo s_SaveSettingsMethod =
            AccessTools.Method(s_AssetDatabaseType, "SaveSettings");
        private static readonly MethodInfo s_SaveAllSettingsMethod =
            AccessTools.Method(s_AssetDatabaseType, "SaveAllSettings");
        private static readonly MethodInfo s_SaveSpecificSettingMethod =
            AccessTools.Method(s_AssetDatabaseType, "SaveSpecificSetting", new[] { typeof(string) });
        private static readonly MethodInfo s_ProcessAllSettingsFilesMethod =
            AccessTools.Method(s_AssetDatabaseType, "ProcessAllSettingsFiles");
        private static readonly MethodInfo s_ProcessSingleSettingsFileMethod =
            AccessTools.Method(s_AssetDatabaseType, "ProcessSingleSettingsFile");
        private static readonly MethodInfo s_SaveSettingsHelperGetWriteStreamMethod =
            AccessTools.Method(s_SaveSettingsHelperType, "GetWriteStream");
        private static readonly MethodInfo s_SaveSettingsHelperWriteMethod =
            AccessTools.Method(s_SaveSettingsHelperType, "Write");
        private static readonly MethodInfo s_SaveSettingsHelperWriteAsyncMethod =
            AccessTools.Method(s_SaveSettingsHelperType, "WriteAsync");
        private static readonly MethodInfo s_SaveSettingsHelperDisposeMethod =
            AccessTools.Method(s_SaveSettingsHelperType, "Dispose", Type.EmptyTypes);
        private static readonly MethodInfo s_SaveSettingsHelperDisposeAsyncMethod =
            AccessTools.Method(s_SaveSettingsHelperType, "DisposeAsync", Type.EmptyTypes);

        private static readonly Dictionary<object, HelperTraceState> s_HelperStates = new Dictionary<object, HelperTraceState>();

        private static Harmony s_Harmony;
        private static int s_NextHelperId;

        public static void Apply()
        {
            if (s_Harmony != null)
            {
                return;
            }

            try
            {
                s_Harmony = new Harmony(HarmonyId);

                PatchIfPresent(s_SaveSettingsMethod, nameof(SaveSettingsPrefix));
                PatchIfPresent(s_SaveAllSettingsMethod, nameof(SaveAllSettingsPrefix));
                PatchIfPresent(s_SaveSpecificSettingMethod, nameof(SaveSpecificSettingPrefix));
                PatchIfPresent(s_ProcessAllSettingsFilesMethod, nameof(ProcessAllSettingsFilesPrefix));
                PatchIfPresent(s_ProcessSingleSettingsFileMethod, nameof(ProcessSingleSettingsFilePrefix));
                PatchIfPresent(s_SaveSettingsHelperGetWriteStreamMethod, nameof(GetWriteStreamPrefix));
                PatchIfPresent(s_SaveSettingsHelperWriteMethod, nameof(WritePrefix));
                PatchIfPresent(s_SaveSettingsHelperWriteAsyncMethod, nameof(WriteAsyncPrefix));
                PatchIfPresent(s_SaveSettingsHelperDisposeMethod, nameof(DisposePrefix));
                PatchIfPresent(s_SaveSettingsHelperDisposeAsyncMethod, nameof(DisposeAsyncPrefix));

                string message =
                    "[KEYBIND_TRACE] AssetDatabase settings trace patches applied. " +
                    $"saveSettingsPatched={s_SaveSettingsMethod != null}, saveAllSettingsPatched={s_SaveAllSettingsMethod != null}, " +
                    $"saveSpecificSettingPatched={s_SaveSpecificSettingMethod != null}, processAllPatched={s_ProcessAllSettingsFilesMethod != null}, " +
                    $"processSinglePatched={s_ProcessSingleSettingsFileMethod != null}, helperGetWriteStreamPatched={s_SaveSettingsHelperGetWriteStreamMethod != null}, " +
                    $"helperWritePatched={s_SaveSettingsHelperWriteMethod != null}, helperWriteAsyncPatched={s_SaveSettingsHelperWriteAsyncMethod != null}, " +
                    $"helperDisposePatched={s_SaveSettingsHelperDisposeMethod != null}, helperDisposeAsyncPatched={s_SaveSettingsHelperDisposeAsyncMethod != null}";
                Mod.log.Info(message);
                GuardDiagnostics.WriteEvent("ASSET_TRACE", message);
            }
            catch (Exception ex)
            {
                s_Harmony = null;
                Mod.log.Error(ex, "[KEYBIND_TRACE] Failed to apply AssetDatabase settings trace patches.");
                GuardDiagnostics.WriteEvent("ASSET_TRACE", $"Failed to apply AssetDatabase settings trace patches. exception={ex}");
            }
        }

        private static void SaveSettingsPrefix()
        {
            LogAssetEvent("AssetDatabase.SaveSettings entered.", logToMainLog: ShutdownWriteTracker.IsTracking);
        }

        private static void SaveAllSettingsPrefix()
        {
            LogAssetEvent("AssetDatabase.SaveAllSettings entered.", logToMainLog: ShutdownWriteTracker.IsTracking);
        }

        private static void SaveSpecificSettingPrefix(string settingName)
        {
            bool interesting = IsInterestingText(settingName);
            LogAssetEvent(
                $"AssetDatabase.SaveSpecificSetting entered. settingName={settingName ?? "null"}",
                logToMainLog: ShutdownWriteTracker.IsTracking || interesting);
        }

        private static void ProcessAllSettingsFilesPrefix(IDictionary settingsByFile)
        {
            if (settingsByFile == null)
            {
                LogAssetEvent("AssetDatabase.ProcessAllSettingsFiles entered with null settingsByFile.", logToMainLog: ShutdownWriteTracker.IsTracking);
                return;
            }

            List<string> fileSummaries = new List<string>();
            bool interesting = false;

            foreach (DictionaryEntry entry in settingsByFile)
            {
                string filePath = entry.Key as string;
                List<string> settingNames = ExtractSettingNames(entry.Value as IEnumerable);
                if (IsInterestingFilePath(filePath) || settingNames.Any(IsInterestingText))
                {
                    interesting = true;
                }

                fileSummaries.Add($"{filePath ?? "null"}[{settingNames.Count}]");
                if (fileSummaries.Count >= 8)
                {
                    break;
                }
            }

            LogAssetEvent(
                "AssetDatabase.ProcessAllSettingsFiles entered. " +
                $"tracking={ShutdownWriteTracker.DescribeTrackingState()}, fileCount={settingsByFile.Count}, files={string.Join(" | ", fileSummaries.ToArray())}",
                logToMainLog: ShutdownWriteTracker.IsTracking || interesting);
        }

        private static void ProcessSingleSettingsFilePrefix(string filePath, IEnumerable settingsInFile, object saveHelper)
        {
            List<string> settingNames = ExtractSettingNames(settingsInFile);
            bool interesting = IsInterestingFilePath(filePath) || settingNames.Any(IsInterestingText);
            HelperTraceState helperState = null;

            if (saveHelper != null)
            {
                helperState = RegisterHelperState(saveHelper, filePath, settingNames, interesting);
            }

            LogAssetEvent(
                "AssetDatabase.ProcessSingleSettingsFile entered. " +
                $"filePath={NormalizePath(filePath) ?? "null"}, settingsCount={settingNames.Count}, settings={BuildSettingsSummary(settingNames)}, " +
                $"helper={DescribeHelperState(helperState)}, tracking={ShutdownWriteTracker.DescribeTrackingState()}",
                logToMainLog: ShutdownWriteTracker.IsTracking || interesting);
        }

        private static void GetWriteStreamPrefix(object __instance, object fragment)
        {
            if (!TryGetHelperState(__instance, out HelperTraceState helperState))
            {
                return;
            }

            string fragmentName = DescribeFragment(fragment);
            int fragmentCount = RecordFragmentVisit(helperState, fragmentName);
            if (!helperState.IsInteresting && !IsInterestingText(fragmentName))
            {
                return;
            }

            if (fragmentCount > MaxLoggedFragmentsPerHelper)
            {
                return;
            }

            LogAssetEvent(
                "SaveSettingsHelper.GetWriteStream entered. " +
                $"helper={DescribeHelperState(helperState)}, fragment={fragmentName}, fragmentVisit={fragmentCount}, tracking={ShutdownWriteTracker.DescribeTrackingState()}",
                logToMainLog: ShutdownWriteTracker.IsTracking || helperState.IsInteresting);
        }

        private static void WritePrefix(object __instance, object fragment, string line)
        {
            LogFragmentWrite(__instance, fragment, line, isAsync: false);
        }

        private static void WriteAsyncPrefix(object __instance, object fragment, string line)
        {
            LogFragmentWrite(__instance, fragment, line, isAsync: true);
        }

        private static void DisposePrefix(object __instance)
        {
            if (!TryGetHelperState(__instance, out HelperTraceState helperState))
            {
                return;
            }

            LogAssetEvent(
                "SaveSettingsHelper.Dispose entered. " +
                $"helper={DescribeHelperState(helperState)}, fragments={BuildFragmentSummary(helperState)}, tracking={ShutdownWriteTracker.DescribeTrackingState()}",
                logToMainLog: ShutdownWriteTracker.IsTracking || helperState.IsInteresting);
        }

        private static void DisposeAsyncPrefix(object __instance)
        {
            if (!TryGetHelperState(__instance, out HelperTraceState helperState))
            {
                return;
            }

            LogAssetEvent(
                "SaveSettingsHelper.DisposeAsync entered. " +
                $"helper={DescribeHelperState(helperState)}, fragments={BuildFragmentSummary(helperState)}, tracking={ShutdownWriteTracker.DescribeTrackingState()}",
                logToMainLog: ShutdownWriteTracker.IsTracking || helperState.IsInteresting);
        }

        private static void LogFragmentWrite(object helper, object fragment, string line, bool isAsync)
        {
            if (!TryGetHelperState(helper, out HelperTraceState helperState))
            {
                return;
            }

            string fragmentName = DescribeFragment(fragment);
            int fragmentCount = RecordFragmentVisit(helperState, fragmentName);
            bool lineInteresting = IsInterestingLine(line) || IsInterestingText(fragmentName);
            bool shouldLog = helperState.IsInteresting || lineInteresting;
            if (!shouldLog)
            {
                return;
            }

            int writeCount = Interlocked.Increment(ref helperState.TotalLoggedWrites);
            if (writeCount > MaxLoggedWritesPerHelper)
            {
                if (!helperState.SuppressedWriteMessageLogged)
                {
                    helperState.SuppressedWriteMessageLogged = true;
                    LogAssetEvent(
                        "Further SaveSettingsHelper.Write logs for this helper are suppressed. " +
                        $"helper={DescribeHelperState(helperState)}, fragment={fragmentName}, tracking={ShutdownWriteTracker.DescribeTrackingState()}",
                        logToMainLog: ShutdownWriteTracker.IsTracking || helperState.IsInteresting);
                }

                return;
            }

            LogAssetEvent(
                $"{(isAsync ? "SaveSettingsHelper.WriteAsync" : "SaveSettingsHelper.Write")} entered. " +
                $"helper={DescribeHelperState(helperState)}, fragment={fragmentName}, fragmentVisit={fragmentCount}, line={SummarizeLine(line)}, tracking={ShutdownWriteTracker.DescribeTrackingState()}",
                logToMainLog: ShutdownWriteTracker.IsTracking || helperState.IsInteresting);
        }

        private static HelperTraceState RegisterHelperState(object helper, string filePath, List<string> settingNames, bool isInteresting)
        {
            lock (s_Gate)
            {
                if (!s_HelperStates.TryGetValue(helper, out HelperTraceState state))
                {
                    state = new HelperTraceState
                    {
                        HelperId = Interlocked.Increment(ref s_NextHelperId),
                    };
                    s_HelperStates[helper] = state;
                }

                state.FilePath = NormalizePath(filePath);
                state.SettingNames = settingNames;
                state.IsInteresting = isInteresting;
                state.LastUpdatedUtc = DateTime.UtcNow;
                return state;
            }
        }

        private static bool TryGetHelperState(object helper, out HelperTraceState state)
        {
            lock (s_Gate)
            {
                state = null;
                return helper != null && s_HelperStates.TryGetValue(helper, out state);
            }
        }

        private static int RecordFragmentVisit(HelperTraceState state, string fragmentName)
        {
            lock (s_Gate)
            {
                if (!state.FragmentVisitCounts.TryGetValue(fragmentName, out int count))
                {
                    count = 0;
                }

                count += 1;
                state.FragmentVisitCounts[fragmentName] = count;
                state.LastUpdatedUtc = DateTime.UtcNow;
                return count;
            }
        }

        private static void PatchIfPresent(MethodBase method, string prefixMethodName)
        {
            if (method == null)
            {
                return;
            }

            s_Harmony.Patch(method, prefix: new HarmonyMethod(typeof(AssetDatabaseSettingsTracePatches), prefixMethodName));
        }

        private static void LogAssetEvent(string message, bool logToMainLog)
        {
            if (logToMainLog)
            {
                Mod.log.Info($"[KEYBIND_TRACE] {message}");
            }

            GuardDiagnostics.WriteEvent("ASSET_TRACE", message);
        }

        private static string DescribeFragment(object fragment)
        {
            if (fragment == null)
            {
                return "null";
            }

            string name = ReadMemberValue(fragment, "name") ??
                          ReadMemberValue(fragment, "Name") ??
                          ReadFieldValue(fragment, "name") ??
                          ReadFieldValue(fragment, "guid");
            object asset = ReadField(fragment, "asset");
            string assetName = asset != null ? ReadMemberValue(asset, "name") ?? ReadFieldValue(asset, "<name>k__BackingField") : null;

            if (!string.IsNullOrWhiteSpace(assetName) && !string.IsNullOrWhiteSpace(name))
            {
                return $"{name} (asset={assetName})";
            }

            return name ?? fragment.GetType().FullName;
        }

        private static string BuildSettingsSummary(List<string> settingNames)
        {
            if (settingNames == null || settingNames.Count == 0)
            {
                return "none";
            }

            if (settingNames.Count <= 12)
            {
                return string.Join(" | ", settingNames.ToArray());
            }

            return string.Join(" | ", settingNames.Take(12).ToArray()) + $" | (+{settingNames.Count - 12} more)";
        }

        private static string BuildFragmentSummary(HelperTraceState helperState)
        {
            lock (s_Gate)
            {
                if (helperState.FragmentVisitCounts.Count == 0)
                {
                    return "none";
                }

                return string.Join(
                    " | ",
                    helperState.FragmentVisitCounts
                        .OrderByDescending(entry => entry.Value)
                        .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                        .Take(12)
                        .Select(entry => $"{entry.Key}={entry.Value}")
                        .ToArray());
            }
        }

        private static string DescribeHelperState(HelperTraceState helperState)
        {
            if (helperState == null)
            {
                return "none";
            }

            return
                $"id={helperState.HelperId}, filePath={helperState.FilePath ?? "null"}, settings={BuildSettingsSummary(helperState.SettingNames)}, " +
                $"interesting={helperState.IsInteresting}, writesLogged={helperState.TotalLoggedWrites}";
        }

        private static List<string> ExtractSettingNames(IEnumerable settingsInFile)
        {
            List<string> names = new List<string>();
            if (settingsInFile == null)
            {
                return names;
            }

            foreach (object setting in settingsInFile)
            {
                if (setting == null)
                {
                    continue;
                }

                string name = ReadMemberValue(setting, "name") ??
                              ReadMemberValue(setting, "Name") ??
                              ReadFieldValue(setting, "<name>k__BackingField");
                names.Add(name ?? setting.GetType().FullName);
            }

            return names;
        }

        private static bool IsInterestingFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string normalized = NormalizePath(filePath);
            return string.Equals(normalized, NormalizePath(GuardPaths.SettingsFilePath), StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(Path.DirectorySeparatorChar + "Settings.coc", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(Path.AltDirectorySeparatorChar + "Settings.coc", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith("Settings.coc", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInterestingText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string lower = text.ToLowerInvariant();
            return s_InterestingNameFragments.Any(fragment => lower.Contains(fragment));
        }

        private static bool IsInterestingLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string trimmed = line.Trim();
            return trimmed.EndsWith("Settings", StringComparison.Ordinal) ||
                   trimmed.IndexOf("\"bindings\"", StringComparison.Ordinal) >= 0 ||
                   trimmed.IndexOf("pausedAfterLoading", StringComparison.Ordinal) >= 0 ||
                   trimmed.IndexOf("showTutorials", StringComparison.Ordinal) >= 0;
        }

        private static string SummarizeLine(string line)
        {
            if (line == null)
            {
                return "null";
            }

            string trimmed = line.Trim();
            if (trimmed.Length <= 160)
            {
                return trimmed;
            }

            return trimmed.Substring(0, 160) + "...";
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

        private static object ReadField(object instance, string fieldName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            try
            {
                return instance.GetType()
                    .GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadFieldValue(object instance, string fieldName)
        {
            object value = ReadField(instance, fieldName);
            return value?.ToString();
        }

        private static string ReadMemberValue(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            try
            {
                object value = instance.GetType()
                    .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(instance, null);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private sealed class HelperTraceState
        {
            public int HelperId { get; set; }

            public string FilePath { get; set; }

            public List<string> SettingNames { get; set; } = new List<string>();

            public bool IsInteresting { get; set; }

            public int TotalLoggedWrites;

            public bool SuppressedWriteMessageLogged { get; set; }

            public DateTime LastUpdatedUtc { get; set; }

            public Dictionary<string, int> FragmentVisitCounts { get; } =
                new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }
}
