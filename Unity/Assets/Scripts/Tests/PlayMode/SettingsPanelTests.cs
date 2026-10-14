using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.AOT;
using Game.Hotfix;
using GameFrame.Audio;
using GameFrame.Localization;
using GameFrame.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YooAsset;

namespace GameFrame.Tests
{
    public sealed class SettingsPanelTests
    {
        private readonly Dictionary<string, float?> savedVolumes = new Dictionary<string, float?>();
        private int? savedLanguage;
        private int? savedSfxEnabled;
        private ResourceLoadManager resources;
        private GlobalConfig config;
        private MainPanel main;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
#if UNITY_EDITOR
            foreach (string key in new[] { AudioPreferences.MasterKey, AudioPreferences.MusicKey, AudioPreferences.SfxKey })
            {
                savedVolumes[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetFloat(key) : (float?)null;
                PlayerPrefs.DeleteKey(key);
            }

            savedLanguage = PlayerPrefs.HasKey("game.language") ? PlayerPrefs.GetInt("game.language") : (int?)null;
            savedSfxEnabled = PlayerPrefs.HasKey(AudioPreferences.SfxEnabledKey) ? PlayerPrefs.GetInt(AudioPreferences.SfxEnabledKey) : (int?)null;
            PlayerPrefs.SetInt("game.language", (int)GameLanguage.ZhCN);
            PlayerPrefs.DeleteKey(AudioPreferences.SfxEnabledKey);
            LanguageManager.Init(SettingsLocalization.CreateTable());
            config = ScriptableObject.CreateInstance<GlobalConfig>();
            config.PlayMode = EPlayMode.EditorSimulateMode;
            resources = new ResourceLoadManager();
            await resources.InitializeAsync(config);
            await resources.UpdatePackageManifestAsync();
            GameUI.Init(resources.Package);
            GameUIRegistration.RegisterAll();
            await GameAudio.InitAsync(resources.Package, UnityEditor.AssetDatabase.LoadAssetAtPath<AudioRuntimeConfig>(
                "Assets/Config/Audio/GameAudioConfig.asset"));
            AudioPreferences.ApplySaved();
            main = await GameUI.Push<MainPanel, MainPanelArgs>(new MainPanelArgs(() => UniTask.CompletedTask));
#else
            Assert.Ignore("Settings UI tests require Editor simulated assets.");
            await UniTask.CompletedTask;
#endif
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            GameUI.Shutdown();
            if (GameAudio.IsInited)
            {
                await GameAudio.ShutdownAsync();
            }

            LanguageManager.Shutdown();
            if (resources != null)
            {
                await resources.ShutdownAsync();
            }

            UnityEngine.Object.Destroy(config);
            foreach (var pair in savedVolumes)
            {
                if (pair.Value.HasValue)
                {
                    PlayerPrefs.SetFloat(pair.Key, pair.Value.Value);
                }
                else
                {
                    PlayerPrefs.DeleteKey(pair.Key);
                }
            }

            RestoreInt("game.language", savedLanguage);
            RestoreInt(AudioPreferences.SfxEnabledKey, savedSfxEnabled);
            PlayerPrefs.Save();
            await UniTask.NextFrame();
        });

        private static void RestoreInt(string key, int? value)
        {
            if (value.HasValue)
            {
                PlayerPrefs.SetInt(key, value.Value);
            }
            else
            {
                PlayerPrefs.DeleteKey(key);
            }
        }

        [UnityTest]
        public IEnumerator SettingsEntryAppliesMixerAndLanguageAndReturnsWithoutDuplicateListeners() => UniTask.ToCoroutine(async () =>
        {
            main.transform.Find("Content/Card/SettingsButton").GetComponent<Button>().onClick.Invoke();
            await UniTask.WaitUntil(() => GameUI.IsOpen<SettingsPanel>()).Timeout(TimeSpan.FromSeconds(10));
            var settings = GameUI.Get<SettingsPanel>();
            Assert.IsFalse(main.gameObject.activeInHierarchy);
            var card = settings.transform.Find("Content/Card");
            card.Find("MasterSlider").GetComponent<Slider>().value = 0.4f;
            card.Find("MusicSlider").GetComponent<Slider>().value = 0.3f;
            card.Find("SfxSlider").GetComponent<Slider>().value = 0.65f;
            Assert.AreEqual(0.4f, GameAudio.GetMasterVolume(), 0.001f);
            Assert.AreEqual(0.3f, GameAudio.GetBusVolume(AudioBus.Bgm), 0.001f);
            Assert.AreEqual(0.65f, GameAudio.GetBusVolume(AudioBus.Sfx), 0.001f);
            card.Find("SfxToggle").GetComponent<Toggle>().isOn = false;
            Assert.LessOrEqual(GameAudio.GetBusVolume(AudioBus.Sfx), 0.0001f);
            Assert.LessOrEqual(GameAudio.GetBusVolume(AudioBus.Ui), 0.0001f);
            Assert.IsFalse(card.Find("SfxSlider").GetComponent<Slider>().interactable);
            Assert.AreEqual(0.65f, AudioPreferences.Sfx);
            card.Find("EnglishButton").GetComponent<Button>().onClick.Invoke();
            Assert.AreEqual(GameLanguage.EnUS, LanguageManager.Current);
            Assert.AreEqual("Settings", card.Find("Title").GetComponent<TMP_Text>().text);
            card.Find("BackButton").GetComponent<Button>().onClick.Invoke();
            Assert.IsTrue(main.gameObject.activeInHierarchy);
            Assert.IsFalse(GameUI.IsOpen<SettingsPanel>());

            // Destroy/reload UI and reapply saved preferences as startup does.
            GameUI.Destroy<SettingsPanel>();
            AudioPreferences.ApplySaved();
            settings = await GameUI.Push<SettingsPanel>();
            card = settings.transform.Find("Content/Card");
            Assert.AreEqual(0.4f, card.Find("MasterSlider").GetComponent<Slider>().value);
            Assert.IsFalse(card.Find("SfxToggle").GetComponent<Toggle>().isOn);
            Assert.AreEqual("Back", card.Find("BackButton/BackLabel").GetComponent<TMP_Text>().text);
            card.Find("SfxToggle").GetComponent<Toggle>().isOn = true;
            Assert.AreEqual(0.65f, GameAudio.GetBusVolume(AudioBus.Sfx), 0.001f);
            card.Find("ChineseButton").GetComponent<Button>().onClick.Invoke();
            Assert.AreEqual("设置", card.Find("Title").GetComponent<TMP_Text>().text);
            card.Find("BackButton").GetComponent<Button>().onClick.Invoke();
            Assert.IsTrue(main.transform.Find("Content/Card/SettingsButton").GetComponent<Button>().interactable);
        });
    }
}
