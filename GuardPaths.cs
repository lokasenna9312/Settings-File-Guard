using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Settings_File_Guard
{
    internal static class GuardPaths
    {
        private static readonly object s_Gate = new object();
        private static string s_SettingsDirectoryPath;

        public static void Initialize()
        {
            _ = SettingsDirectoryPath;
        }

        public static string SettingsDirectoryPath
        {
            get
            {
                string cachedPath = Volatile.Read(ref s_SettingsDirectoryPath);
                if (!string.IsNullOrWhiteSpace(cachedPath))
                {
                    return cachedPath;
                }

                lock (s_Gate)
                {
                    if (!string.IsNullOrWhiteSpace(s_SettingsDirectoryPath))
                    {
                        return s_SettingsDirectoryPath;
                    }

                    s_SettingsDirectoryPath = ResolveSettingsDirectoryPath();
                    return s_SettingsDirectoryPath;
                }
            }
        }

        public static string SettingsFilePath => Path.Combine(SettingsDirectoryPath, "Settings.coc");

        public static string LogsDirectoryPath => Path.Combine(SettingsDirectoryPath, "Logs");

        private static string ResolveSettingsDirectoryPath()
        {
            if (!Environment.HasShutdownStarted)
            {
                try
                {
                    string persistentDataPath = Application.persistentDataPath;
                    if (!string.IsNullOrWhiteSpace(persistentDataPath))
                    {
                        return persistentDataPath;
                    }
                }
                catch
                {
                }
            }

            return BuildFallbackSettingsDirectoryPath();
        }

        private static string BuildFallbackSettingsDirectoryPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "LocalLow",
                "Colossal Order",
                "Cities Skylines II");
        }
    }
}
