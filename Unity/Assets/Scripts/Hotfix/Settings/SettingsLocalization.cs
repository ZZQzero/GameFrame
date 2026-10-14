using System.Collections.Generic;
using GameFrame.Localization;

namespace Game.Hotfix
{
    public static class SettingsLocalization
    {
        public static Dictionary<string, LanguageTexts> CreateTable() => new Dictionary<string, LanguageTexts>
        {
            ["settings.title"] = new LanguageTexts("设置", "Settings", "الإعدادات"),
            ["settings.master"] = new LanguageTexts("总音量", "Master volume", "مستوى الصوت العام"),
            ["settings.music"] = new LanguageTexts("音乐音量", "Music volume", "مستوى الموسيقى"),
            ["settings.sfx"] = new LanguageTexts("音效音量", "Sound volume", "مستوى المؤثرات"),
            ["settings.enabled"] = new LanguageTexts("启用音效", "Enable sound effects", "تفعيل المؤثرات الصوتية"),
            ["settings.language"] = new LanguageTexts("语言", "Language", "اللغة"),
            ["settings.back"] = new LanguageTexts("返回", "Back", "رجوع"),
            ["settings.hint"] = new LanguageTexts("调整立即生效，返回时保存", "Changes apply now and save on return", "تطبق التغييرات فوراً وتحفظ عند الرجوع"),
            ["settings.noAudio"] = new LanguageTexts("音频未启用，暂不能调整音量", "Audio is disabled; volume is unavailable", "الصوت غير مفعل")
        };
    }
}
