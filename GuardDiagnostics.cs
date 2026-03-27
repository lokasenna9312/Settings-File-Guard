using System;
using System.IO;
using System.Text;

namespace Settings_File_Guard
{
    internal static class GuardDiagnostics
    {
        private const string LegacyToggleFileName = "Settings_File_Guard.DeepDiagnostics.enabled";
        private const int SnapshotHeadLineCount = 80;
        private const int SnapshotTailLineCount = 40;
        private const int SnapshotFocusContextBefore = 20;
        private const int SnapshotFocusContextAfter = 120;
        private const int SnapshotFullFileLineThreshold = 240;
        private const int SnapshotLabelMaxLength = 56;
        private const int SnapshotSourceNameMaxLength = 28;

        private static readonly object s_Gate = new object();

        private static bool s_Initialized;
        private static bool s_HasLastKnownEnabledState;
        private static bool s_LastKnownEnabledState;
        private static string s_SessionId;
        private static string s_LogFilePath;
        private static string s_SnapshotDirectoryPath;

        public static bool IsEnabled
        {
            get
            {
                EnsureInitialized();
                lock (s_Gate)
                {
                    return GetCurrentEnabledStateCore();
                }
            }
        }

        public static string LegacyToggleFilePath => Path.Combine(GuardPaths.SettingsDirectoryPath, LegacyToggleFileName);

        public static bool GetDefaultDeepDiagnosticsEnabled()
        {
            try
            {
                return File.Exists(LegacyToggleFilePath);
            }
            catch
            {
                return false;
            }
        }

        public static void Initialize()
        {
            EnsureInitialized();
        }

        public static void WriteEvent(string category, string message)
        {
            if (!IsEnabled)
            {
                return;
            }

            EnsureArtifactsInitialized();
            AppendLogLine(category, message);
        }

        public static void DumpFileSnapshot(string category, string label, string sourcePath, string summary)
        {
            if (!IsEnabled)
            {
                return;
            }

            EnsureArtifactsInitialized();

            try
            {
                if (!File.Exists(sourcePath))
                {
                    AppendLogLine(
                        category,
                        $"Snapshot skipped because source file is missing. label={label}, source={sourcePath}");
                    return;
                }

                Directory.CreateDirectory(s_SnapshotDirectoryPath);

                string snapshotFileName = BuildSnapshotFileName(label, sourcePath);
                string snapshotPath = Path.Combine(s_SnapshotDirectoryPath, snapshotFileName);

                using (StreamWriter writer = new StreamWriter(snapshotPath, append: false, Encoding.UTF8))
                {
                    FileInfo info = new FileInfo(sourcePath);
                    string[] lines = File.ReadAllLines(sourcePath);

                    writer.WriteLine($"label={label}");
                    writer.WriteLine($"source={sourcePath}");
                    writer.WriteLine($"length={info.Length}");
                    writer.WriteLine($"lineCount={lines.Length}");
                    writer.WriteLine($"lastWriteUtc={info.LastWriteTimeUtc:O}");
                    writer.WriteLine($"summary={summary}");
                    writer.WriteLine();

                    WriteSnapshotBody(writer, lines);
                }

                AppendLogLine(
                    category,
                    $"Wrote file snapshot. label={label}, source={sourcePath}, snapshot={snapshotPath}, summary={summary}");
            }
            catch (Exception ex)
            {
                Mod.log.Warn(
                    $"[KEYBIND_DIAGNOSTICS] Failed to write deep diagnostics snapshot. label={label}, source={sourcePath}, reason={ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void EnsureInitialized()
        {
            if (s_Initialized)
            {
                return;
            }

            lock (s_Gate)
            {
                if (s_Initialized)
                {
                    return;
                }

                s_SessionId = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
                bool isEnabled = GetCurrentEnabledStateCore();

                if (isEnabled)
                {
                    EnsureArtifactsInitializedCore();
                }

                s_Initialized = true;
                Mod.log.Info(
                    $"[KEYBIND_DIAGNOSTICS] Deep diagnostics {(isEnabled ? "enabled" : "disabled")}. session={s_SessionId}, legacyToggleFile={LegacyToggleFilePath}, logPath={s_LogFilePath ?? "none"}");
            }
        }

        private static void AppendLogLine(string category, string message)
        {
            lock (s_Gate)
            {
                EnsureArtifactsInitializedCore();
                AppendLogLineCore(category, message);
            }
        }

        private static bool GetCurrentEnabledStateCore()
        {
            if (Mod.Settings != null)
            {
                s_LastKnownEnabledState = Mod.Settings.EnableDeepDiagnostics;
                s_HasLastKnownEnabledState = true;
                return s_LastKnownEnabledState;
            }

            if (!s_HasLastKnownEnabledState)
            {
                s_LastKnownEnabledState = GetDefaultDeepDiagnosticsEnabled();
                s_HasLastKnownEnabledState = true;
            }

            return s_LastKnownEnabledState;
        }

        private static void EnsureArtifactsInitialized()
        {
            lock (s_Gate)
            {
                EnsureArtifactsInitializedCore();
            }
        }

        private static void EnsureArtifactsInitializedCore()
        {
            if (!string.IsNullOrWhiteSpace(s_LogFilePath))
            {
                return;
            }

            Directory.CreateDirectory(GuardPaths.LogsDirectoryPath);
            s_LogFilePath = Path.Combine(
                GuardPaths.LogsDirectoryPath,
                $"Settings_File_Guard.DeepDiagnostics.{s_SessionId}.log");
            s_SnapshotDirectoryPath = Path.Combine(
                GuardPaths.LogsDirectoryPath,
                $"Settings_File_Guard.DeepDiagnostics.{s_SessionId}");
            Directory.CreateDirectory(s_SnapshotDirectoryPath);
            AppendLogLineCore(
                "SYSTEM",
                $"Deep diagnostics enabled. session={s_SessionId}, legacyToggleFile={LegacyToggleFilePath}, snapshotDirectory={s_SnapshotDirectoryPath}");
        }

        private static void AppendLogLineCore(string category, string message)
        {
            try
            {
                try
                {
                    using (StreamWriter writer = new StreamWriter(s_LogFilePath, append: true, Encoding.UTF8))
                    {
                        writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}");
                    }
                }
                catch (Exception ex)
                {
                    Mod.log.Warn(
                        $"[KEYBIND_DIAGNOSTICS] Failed to append deep diagnostics log. category={category}, reason={ex.GetType().Name}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn(
                    $"[KEYBIND_DIAGNOSTICS] Failed to prepare deep diagnostics log. category={category}, reason={ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void WriteSnapshotBody(StreamWriter writer, string[] lines)
        {
            if (lines.Length <= SnapshotFullFileLineThreshold)
            {
                WriteLineRange(writer, "full-file", lines, 0, lines.Length - 1);
                return;
            }

            WriteLineRange(writer, "head", lines, 0, Math.Min(lines.Length - 1, SnapshotHeadLineCount - 1));

            int focusIndex = FindFocusLineIndex(lines);
            if (focusIndex >= 0)
            {
                int start = Math.Max(0, focusIndex - SnapshotFocusContextBefore);
                int end = Math.Min(lines.Length - 1, focusIndex + SnapshotFocusContextAfter);
                WriteLineRange(writer, "focus", lines, start, end);
            }

            int tailStart = Math.Max(0, lines.Length - SnapshotTailLineCount);
            WriteLineRange(writer, "tail", lines, tailStart, lines.Length - 1);
        }

        private static void WriteLineRange(StreamWriter writer, string name, string[] lines, int start, int end)
        {
            if (lines.Length == 0 || start > end)
            {
                return;
            }

            writer.WriteLine($"--- {name} [{start + 1}..{end + 1}] ---");
            for (int i = start; i <= end; i += 1)
            {
                writer.WriteLine($"{i + 1,4}: {lines[i]}");
            }

            writer.WriteLine();
        }

        private static int FindFocusLineIndex(string[] lines)
        {
            for (int i = 0; i < lines.Length; i += 1)
            {
                string line = lines[i];
                if (line.IndexOf("Keybinding Settings", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("Input Settings", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("\"bindings\"", StringComparison.Ordinal) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string BuildSnapshotFileName(string label, string sourcePath)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            string safeLabel = SanitizeFileName(label, SnapshotLabelMaxLength);
            string safeSource = SanitizeFileName(Path.GetFileName(sourcePath), SnapshotSourceNameMaxLength);
            return $"{timestamp}.{safeLabel}.{safeSource}.txt";
        }

        private static string SanitizeFileName(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unnamed";
            }

            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '_' : character);
            }

            string sanitized = builder.ToString().Trim().TrimEnd('.');
            if (sanitized.Length == 0)
            {
                sanitized = "unnamed";
            }

            if (sanitized.Length <= maxLength)
            {
                return sanitized;
            }

            int hash = StringComparer.Ordinal.GetHashCode(sanitized);
            string suffix = $"~{hash & 0x7fffffff:x8}";
            int prefixLength = Math.Max(8, maxLength - suffix.Length);
            return sanitized.Substring(0, prefixLength) + suffix;
        }
    }
}
