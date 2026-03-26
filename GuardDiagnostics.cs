using System;
using System.IO;
using System.Text;

namespace Settings_File_Guard
{
    internal static class GuardDiagnostics
    {
        private const string ToggleFileName = "Settings_File_Guard.DeepDiagnostics.enabled";
        private const int SnapshotHeadLineCount = 80;
        private const int SnapshotTailLineCount = 40;
        private const int SnapshotFocusContextBefore = 20;
        private const int SnapshotFocusContextAfter = 120;
        private const int SnapshotFullFileLineThreshold = 240;

        private static readonly object s_Gate = new object();

        private static bool s_Initialized;
        private static bool s_IsEnabled;
        private static string s_SessionId;
        private static string s_LogFilePath;
        private static string s_SnapshotDirectoryPath;

        public static bool IsEnabled
        {
            get
            {
                EnsureInitialized();
                return s_IsEnabled;
            }
        }

        public static string ToggleFilePath => Path.Combine(GuardPaths.SettingsDirectoryPath, ToggleFileName);

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

            AppendLogLine(category, message);
        }

        public static void DumpFileSnapshot(string category, string label, string sourcePath, string summary)
        {
            if (!IsEnabled)
            {
                return;
            }

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

                string snapshotFileName =
                    $"{DateTime.Now:yyyyMMdd_HHmmssfff}.{SanitizeFileName(label)}.{SanitizeFileName(Path.GetFileName(sourcePath))}.txt";
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
                s_IsEnabled = File.Exists(ToggleFilePath);

                if (s_IsEnabled)
                {
                    Directory.CreateDirectory(GuardPaths.LogsDirectoryPath);
                    s_LogFilePath = Path.Combine(
                        GuardPaths.LogsDirectoryPath,
                        $"Settings_File_Guard.DeepDiagnostics.{s_SessionId}.log");
                    s_SnapshotDirectoryPath = Path.Combine(
                        GuardPaths.LogsDirectoryPath,
                        $"Settings_File_Guard.DeepDiagnostics.{s_SessionId}");
                    Directory.CreateDirectory(s_SnapshotDirectoryPath);
                    AppendLogLine(
                        "SYSTEM",
                        $"Deep diagnostics enabled. session={s_SessionId}, toggleFile={ToggleFilePath}, snapshotDirectory={s_SnapshotDirectoryPath}");
                }

                s_Initialized = true;
                Mod.log.Info(
                    $"[KEYBIND_DIAGNOSTICS] Deep diagnostics {(s_IsEnabled ? "enabled" : "disabled")}. session={s_SessionId}, toggleFile={ToggleFilePath}, logPath={s_LogFilePath ?? "none"}");
            }
        }

        private static void AppendLogLine(string category, string message)
        {
            lock (s_Gate)
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

        private static string SanitizeFileName(string value)
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

            return builder.ToString();
        }
    }
}
