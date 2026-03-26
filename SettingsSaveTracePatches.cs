using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace Settings_File_Guard
{
    internal static class SettingsSaveTracePatches
    {
        private const string HarmonyId = "Settings_File_Guard.SettingsSaveTracePatches";

        private static readonly string[] s_InterestingMemberNameFragments =
        {
            "name",
            "path",
            "file",
            "guid",
            "asset",
            "setting",
        };

        private static readonly Type s_SettingAssetType =
            AccessTools.TypeByName("Colossal.IO.AssetDatabase.SettingAsset");

        private static Harmony s_Harmony;
        private static int s_SaveOperationId;
        private static MethodBase[] s_PatchedMethods;

        public static void Apply()
        {
            if (s_Harmony != null)
            {
                return;
            }

            try
            {
                if (s_SettingAssetType == null)
                {
                    Mod.log.Warn("[KEYBIND_TRACE] SettingAsset trace patch target was not found.");
                    return;
                }

                s_PatchedMethods = s_SettingAssetType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(
                        method =>
                            string.Equals(method.Name, "Save", StringComparison.Ordinal) ||
                            string.Equals(method.Name, "SaveWithPersist", StringComparison.Ordinal))
                    .Cast<MethodBase>()
                    .ToArray();

                if (s_PatchedMethods.Length == 0)
                {
                    Mod.log.Warn($"[KEYBIND_TRACE] No SettingAsset Save methods were found on {s_SettingAssetType.FullName}.");
                    return;
                }

                s_Harmony = new Harmony(HarmonyId);
                HarmonyMethod prefix = new HarmonyMethod(typeof(SettingsSaveTracePatches), nameof(SaveMethodPrefix));
                HarmonyMethod postfix = new HarmonyMethod(typeof(SettingsSaveTracePatches), nameof(SaveMethodPostfix));
                HarmonyMethod finalizer = new HarmonyMethod(typeof(SettingsSaveTracePatches), nameof(SaveMethodFinalizer));

                foreach (MethodBase method in s_PatchedMethods)
                {
                    s_Harmony.Patch(method, prefix: prefix, postfix: postfix, finalizer: finalizer);
                }

                string targets = string.Join(" | ", s_PatchedMethods.Select(DescribeMethod).ToArray());
                Mod.log.Info($"[KEYBIND_TRACE] SettingAsset save trace patches applied. targets={targets}");
                GuardDiagnostics.WriteEvent(
                    "SAVE_TRACE",
                    $"SettingAsset save trace patches applied. targets={targets}");
            }
            catch (Exception ex)
            {
                s_Harmony = null;
                Mod.log.Error(ex, "[KEYBIND_TRACE] Failed to apply SettingAsset save trace patches.");
                GuardDiagnostics.WriteEvent("SAVE_TRACE", $"Failed to apply SettingAsset save trace patches. exception={ex}");
            }
        }

        private static void SaveMethodPrefix(
            object __instance,
            MethodBase __originalMethod,
            object[] __args,
            ref int __state)
        {
            __state = Interlocked.Increment(ref s_SaveOperationId);
            LogSaveEvent("enter", __state, __instance, __originalMethod, __args, null);
        }

        private static void SaveMethodPostfix(
            object __instance,
            MethodBase __originalMethod,
            object[] __args,
            int __state)
        {
            LogSaveEvent("exit", __state, __instance, __originalMethod, __args, null);
        }

        private static Exception SaveMethodFinalizer(
            object __instance,
            MethodBase __originalMethod,
            object[] __args,
            int __state,
            Exception __exception)
        {
            if (__exception != null)
            {
                LogSaveEvent("exception", __state, __instance, __originalMethod, __args, __exception);
            }

            return __exception;
        }

        private static void LogSaveEvent(
            string phase,
            int operationId,
            object instance,
            MethodBase originalMethod,
            object[] args,
            Exception exception)
        {
            bool shouldLogToMainLog = ShutdownWriteTracker.IsTracking || exception != null;
            if (!shouldLogToMainLog && !GuardDiagnostics.IsEnabled)
            {
                return;
            }

            string message =
                $"SettingAsset save {phase}. id={operationId}, tracking={ShutdownWriteTracker.DescribeTrackingState()}, " +
                $"thread={Thread.CurrentThread.ManagedThreadId}, method={DescribeMethod(originalMethod)}, " +
                $"asset={SummarizeInstance(instance)}, args={SummarizeArguments(args)}";

            if (exception != null)
            {
                message += $", exception={exception.GetType().Name}: {exception.Message}";
            }

            if (shouldLogToMainLog)
            {
                if (exception != null)
                {
                    Mod.log.Warn($"[KEYBIND_TRACE] {message}");
                }
                else
                {
                    Mod.log.Info($"[KEYBIND_TRACE] {message}");
                }
            }

            GuardDiagnostics.WriteEvent("SAVE_TRACE", message);
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

        private static string SummarizeArguments(object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "none";
            }

            List<string> values = new List<string>(args.Length);
            for (int i = 0; i < args.Length; i += 1)
            {
                object value = args[i];
                values.Add($"arg{i}={SummarizeValue(value)}");
            }

            return string.Join(", ", values.ToArray());
        }

        private static string SummarizeInstance(object instance)
        {
            if (instance == null)
            {
                return "null";
            }

            Type type = instance.GetType();
            StringBuilder builder = new StringBuilder(type.FullName);
            List<string> interestingValues = new List<string>();

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!LooksInteresting(field.Name) || !IsSimpleType(field.FieldType))
                {
                    continue;
                }

                try
                {
                    interestingValues.Add($"{field.Name}={SummarizeValue(field.GetValue(instance))}");
                }
                catch
                {
                }
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!property.CanRead ||
                    property.GetIndexParameters().Length != 0 ||
                    !LooksInteresting(property.Name) ||
                    !IsSimpleType(property.PropertyType))
                {
                    continue;
                }

                try
                {
                    interestingValues.Add($"{property.Name}={SummarizeValue(property.GetValue(instance, null))}");
                }
                catch
                {
                }
            }

            if (interestingValues.Count > 0)
            {
                builder.Append(" {");
                builder.Append(string.Join(", ", interestingValues.ToArray()));
                builder.Append("}");
            }

            return builder.ToString();
        }

        private static bool LooksInteresting(string memberName)
        {
            if (string.IsNullOrWhiteSpace(memberName))
            {
                return false;
            }

            string lower = memberName.ToLowerInvariant();
            foreach (string fragment in s_InterestingMemberNameFragments)
            {
                if (lower.Contains(fragment))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSimpleType(Type type)
        {
            return type == typeof(string) ||
                   type == typeof(bool) ||
                   type == typeof(byte) ||
                   type == typeof(short) ||
                   type == typeof(int) ||
                   type == typeof(long) ||
                   type == typeof(float) ||
                   type == typeof(double) ||
                   type == typeof(decimal) ||
                   type.IsEnum;
        }

        private static string SummarizeValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            return value is string text
                ? $"\"{text}\""
                : value.ToString();
        }
    }
}
