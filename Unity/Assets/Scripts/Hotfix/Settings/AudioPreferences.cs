using System;
using GameFrame.Audio;
using UnityEngine;

namespace Game.Hotfix
{
    public static class AudioPreferences
    {
        public const string MasterKey = "game.audio.master";
        public const string MusicKey = "game.audio.music";
        public const string SfxKey = "game.audio.sfx";
        public const string SfxEnabledKey = "game.audio.sfxEnabled";

        public static float Master => ReadVolume(MasterKey);
        public static float Music => ReadVolume(MusicKey);
        public static float Sfx => ReadVolume(SfxKey);
        public static bool SfxEnabled => PlayerPrefs.GetInt(SfxEnabledKey, 1) != 0;

        public static void ApplySaved()
        {
            if (GameAudio.IsInited)
            {
                GameAudio.SetMasterVolume(Master);
                GameAudio.SetBusVolume(AudioBus.Bgm, Music);
                ApplySfx(Sfx, SfxEnabled);
            }
        }

        public static void SetMaster(float value)
        {
            ValidateVolume(value);
            GameAudio.SetMasterVolume(value);
            PlayerPrefs.SetFloat(MasterKey, value);
        }

        public static void SetMusic(float value)
        {
            ValidateVolume(value);
            GameAudio.SetBusVolume(AudioBus.Bgm, value);
            PlayerPrefs.SetFloat(MusicKey, value);
        }

        public static void SetSfx(float value)
        {
            ValidateVolume(value);
            ApplySfx(value, SfxEnabled);
            PlayerPrefs.SetFloat(SfxKey, value);
        }

        public static void SetSfxEnabled(bool value)
        {
            ApplySfx(Sfx, value);
            PlayerPrefs.SetInt(SfxEnabledKey, value ? 1 : 0);
        }

        private static void ApplySfx(float volume, bool enabled)
        {
            GameAudio.SetBusVolume(AudioBus.Sfx, enabled ? volume : 0f);
            GameAudio.SetBusVolume(AudioBus.Ui, enabled ? volume : 0f);
        }

        private static float ReadVolume(string key)
        {
            float value = PlayerPrefs.GetFloat(key, 1f);
            ValidateVolume(value);
            return value;
        }

        private static void ValidateVolume(float value)
        {
            if (float.IsNaN(value) || value < 0f || value > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
        }
    }
}
