using System;
using System.IO;
using UnityEngine;

namespace Settings_File_Guard
{
    internal static class GuardPaths
    {
        public static string SettingsDirectoryPath
        {
            get
            {
                string persistentDataPath = Application.persistentDataPath;
                if (!string.IsNullOrWhiteSpace(persistentDataPath))
                {
                    return persistentDataPath;
                }

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow",
                    "Colossal Order",
                    "Cities Skylines II");
            }
        }

        public static string SettingsFilePath => Path.Combine(SettingsDirectoryPath, "Settings.coc");

        public static string LogsDirectoryPath => Path.Combine(SettingsDirectoryPath, "Logs");
    }
}
