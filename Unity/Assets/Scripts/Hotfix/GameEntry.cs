using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameFrame.AOT;
using GameFrame.Audio;
using GameFrame.Hotfix.TableConfig;
using GameFrame.Input;
using GameFrame.Localization;
using UnityEngine;
using GameEvents = GameFrame.Event.EventSystem;

namespace GameFrame.Hotfix
{
    /// <summary>资源就绪后实例化的 Hotfix 入口，在 Awake 中启动框架。</summary>
    [DisallowMultipleComponent]
    public sealed class GameEntry : MonoBehaviour
    {
        [SerializeField] InputRuntimeConfig inputConfig;
        [SerializeField] AudioRuntimeConfig audioConfig;
        bool ownsState;

        public bool IsReady { get; private set; }

        void Awake()
        {
            InitializeAsync().Forget();
        }

        async UniTask InitializeAsync()
        {
            var token = this.GetCancellationTokenOnDestroy();
            token.ThrowIfCancellationRequested();
            if (GameRuntime.IsInited)
            {
                throw new InvalidOperationException("[GameEntry] 框架已由其它入口启动。");
            }
            var resources = ResourceLoadManager.Instance ??
                throw new InvalidOperationException("[GameEntry] 请先通过 Launch 准备资源。");
            TableConfigManager.Init(resources, token);
            ownsState = true;

            var languageRows = TableConfigManager.Tables.TbLanguage.DataList;
            var languages = new Dictionary<string, LanguageTexts>(languageRows.Count, StringComparer.Ordinal);
            foreach (var row in languageRows)
            {
                languages.Add(row.Key, new LanguageTexts(row.ZhCN, row.EnUS, row.ArSA));
            }

            GameEvents.EnsureDispatcher();
            await GameRuntime.InitAsync(new GameRuntimeConfig
            {
                Package = resources.Package,
                InputConfig = inputConfig,
                AudioConfig = audioConfig,
                EnablePool = true,
                EnableScene = true,
                LanguageTable = languages
            }, token);
            IsReady = true;
            Debug.Log("[GameEntry] 框架初始化完成。");
        }

        void OnApplicationQuit()
        {
            Cleanup();
        }

        void OnDestroy()
        {
            Cleanup();
        }

        void Cleanup()
        {
            if (!ownsState)
            {
                return;
            }
            ownsState = false;
            IsReady = false;
            // 只做尽力清理，不等待异步关闭或阻塞进程退出。
            if (GameRuntime.IsInited)
            {
                GameRuntime.ShutdownAsync().Forget();
            }
            TableConfigManager.Shutdown();
            GameEvents.ClearAll();
            GameEvents.ShutdownDispatcher();
        }
    }
}
