using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace Settings_File_Guard
{
    [FileLocation(nameof(Settings_File_Guard))]
    [SettingsUIGroupOrder(kDiagnosticsGroup)]
    [SettingsUIShowGroupName(kDiagnosticsGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kDiagnosticsGroup = "Diagnostics";

        public Setting(IMod mod)
            : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(kSection, kDiagnosticsGroup)]
        public bool EnableDeepDiagnostics { get; set; }

        public override void SetDefaults()
        {
            EnableDeepDiagnostics = false;
        }
    }

    internal sealed class DiagnosticsLocaleSource : IDictionarySource
    {
        private readonly Setting m_Setting;
        private readonly string m_SettingsTitle;
        private readonly string m_MainTab;
        private readonly string m_DiagnosticsGroup;
        private readonly string m_EnableLabel;
        private readonly string m_EnableDescription;

        public DiagnosticsLocaleSource(
            Setting setting,
            string settingsTitle,
            string mainTab,
            string diagnosticsGroup,
            string enableLabel,
            string enableDescription)
        {
            m_Setting = setting;
            m_SettingsTitle = settingsTitle;
            m_MainTab = mainTab;
            m_DiagnosticsGroup = diagnosticsGroup;
            m_EnableLabel = enableLabel;
            m_EnableDescription = enableDescription;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), m_SettingsTitle },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), m_MainTab },
                { m_Setting.GetOptionGroupLocaleID(Setting.kDiagnosticsGroup), m_DiagnosticsGroup },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EnableDeepDiagnostics)), m_EnableLabel },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EnableDeepDiagnostics)), m_EnableDescription },
            };
        }

        public void Unload()
        {
        }
    }
}
