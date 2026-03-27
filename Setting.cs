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
            EnableDeepDiagnostics = GuardDiagnostics.GetDefaultDeepDiagnosticsEnabled();
        }
    }

    internal sealed class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Settings File Guard" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kDiagnosticsGroup), "Diagnostics" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EnableDeepDiagnostics)), "Enable deep diagnostics" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EnableDeepDiagnostics)), "Writes per-session deep diagnostics logs and targeted Settings.coc snapshots for investigation. Leave it disabled unless you are actively troubleshooting shutdown corruption or keybinding loss." },
            };
        }

        public void Unload()
        {
        }
    }

    internal sealed class LocaleKO : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleKO(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Settings File Guard" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "메인" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kDiagnosticsGroup), "진단" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EnableDeepDiagnostics)), "Deep diagnostics 사용" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EnableDeepDiagnostics)), "세션별 deep diagnostics 로그와 Settings.coc 스냅샷을 기록합니다. 종료 손상이나 키바인딩 손실을 조사할 때만 켜 두는 편이 좋습니다." },
            };
        }

        public void Unload()
        {
        }
    }
}
