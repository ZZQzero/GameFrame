using GameFrame.Audio;
using GameFrame.Localization;
using GameFrame.UI;
using TMPro;
using UnityEngine;

namespace Game.Hotfix
{
    public partial class SettingsPanel : UIPanel<UINone>
    {
        protected override void OnCreate()
        {
            masterSlider.onValueChanged.AddListener(ChangeMaster);
            musicSlider.onValueChanged.AddListener(ChangeMusic);
            sfxSlider.onValueChanged.AddListener(ChangeSfx);
            sfxToggle.onValueChanged.AddListener(ChangeSfxEnabled);
            chineseButton.onClick.AddListener(SelectChinese);
            englishButton.onClick.AddListener(SelectEnglish);
            backButton.onClick.AddListener(GoBack);
            LifetimeScope.Register(() => masterSlider.onValueChanged.RemoveListener(ChangeMaster));
            LifetimeScope.Register(() => musicSlider.onValueChanged.RemoveListener(ChangeMusic));
            LifetimeScope.Register(() => sfxSlider.onValueChanged.RemoveListener(ChangeSfx));
            LifetimeScope.Register(() => sfxToggle.onValueChanged.RemoveListener(ChangeSfxEnabled));
            LifetimeScope.Register(() => chineseButton.onClick.RemoveListener(SelectChinese));
            LifetimeScope.Register(() => englishButton.onClick.RemoveListener(SelectEnglish));
            LifetimeScope.Register(() => backButton.onClick.RemoveListener(GoBack));
        }

        protected override void OnOpen(UINone args)
        {
            masterSlider.SetValueWithoutNotify(AudioPreferences.Master);
            musicSlider.SetValueWithoutNotify(AudioPreferences.Music);
            sfxSlider.SetValueWithoutNotify(AudioPreferences.Sfx);
            sfxToggle.SetIsOnWithoutNotify(AudioPreferences.SfxEnabled);
            masterSlider.interactable = GameAudio.IsInited;
            musicSlider.interactable = GameAudio.IsInited;
            sfxToggle.interactable = GameAudio.IsInited;
            sfxSlider.interactable = GameAudio.IsInited && sfxToggle.isOn;
            LanguageManager.LanguageChanged += RefreshLanguage;
            OpenScope.Register(() => LanguageManager.LanguageChanged -= RefreshLanguage);
            RefreshLanguage(LanguageManager.Current);
            RefreshValues();
        }

        protected override void OnClose()
        {
            PlayerPrefs.Save();
        }

        private void ChangeMaster(float value)
        {
            AudioPreferences.SetMaster(value);
            RefreshValues();
        }

        private void ChangeMusic(float value)
        {
            AudioPreferences.SetMusic(value);
            RefreshValues();
        }

        private void ChangeSfx(float value)
        {
            AudioPreferences.SetSfx(value);
            RefreshValues();
        }

        private void ChangeSfxEnabled(bool value)
        {
            AudioPreferences.SetSfxEnabled(value);
            sfxSlider.interactable = GameAudio.IsInited && value;
        }

        private void SelectChinese()
        {
            LanguageManager.SetLanguage(GameLanguage.ZhCN);
        }

        private void SelectEnglish()
        {
            LanguageManager.SetLanguage(GameLanguage.EnUS);
        }

        private void GoBack()
        {
            CloseSelf();
        }

        private void RefreshValues()
        {
            masterValue.text = Mathf.RoundToInt(masterSlider.value * 100f) + "%";
            musicValue.text = Mathf.RoundToInt(musicSlider.value * 100f) + "%";
            sfxValue.text = Mathf.RoundToInt(sfxSlider.value * 100f) + "%";
        }

        private void RefreshLanguage(GameLanguage language)
        {
            SetText(title, "settings.title");
            SetText(masterLabel, "settings.master");
            SetText(musicLabel, "settings.music");
            SetText(sfxLabel, "settings.sfx");
            SetText(sfxToggleLabel, "settings.enabled");
            SetText(languageLabel, "settings.language");
            SetText(backLabel, "settings.back");
            SetText(hint, GameAudio.IsInited ? "settings.hint" : "settings.noAudio");
            chineseButton.interactable = language != GameLanguage.ZhCN;
            englishButton.interactable = language != GameLanguage.EnUS;
        }

        private static void SetText(TMP_Text text, string key)
        {
            LanguageManager.SetText(text, key);
        }
    }
}
