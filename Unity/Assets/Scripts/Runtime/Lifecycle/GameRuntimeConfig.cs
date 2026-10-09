using System.Collections.Generic;
using GameFrame.Audio;
using GameFrame.Input;
using GameFrame.Localization;
using GameFrame.Timing;
using UnityEngine;
using YooAsset;

namespace GameFrame
{
    /// <summary>统一启动配置。资源包由调用方提前初始化，并在框架关闭后自行释放。</summary>
    public sealed class GameRuntimeConfig
    {
        public ResourcePackage Package { get; set; }

        /// <summary>可选的持久根节点，由调用方保证其生命周期；不传时按需自动创建。</summary>
        public Transform PersistRoot { get; set; }

        public bool EnableTimer { get; set; } = true;
        public bool EnableUI { get; set; } = true;
        public bool EnablePool { get; set; }
        public bool EnableScene { get; set; }
        public TimerSchedulerOptions TimerOptions { get; set; }

        // 可选模块：不传配置就不启动。
        public InputRuntimeConfig InputConfig { get; set; }
        public AudioRuntimeConfig AudioConfig { get; set; }
        public Dictionary<string, LanguageTexts> LanguageTable { get; set; }
    }
}
