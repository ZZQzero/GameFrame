using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameFrame;
using GameFrame.Audio;
using GameFrame.Diagnostics;
using GameFrame.Input;
using GameFrame.Localization;
using GameFrame.Network;
using GameFrame.UI;
using UnityEngine;
using GameEvents = GameFrame.Event.EventSystem;

namespace Game.AOT
{
    /// <summary>首场景入口：初始化框架、准备资源与 Hotfix DLL，再创建热更业务入口。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10000)]
    public sealed class Launch : MonoBehaviour
    {
        [SerializeField] GlobalConfig globalConfig;
        [SerializeField] InputRuntimeConfig inputConfig;
        [SerializeField] AudioRuntimeConfig audioConfig;
        [SerializeField] RuntimeConsoleMode logMode = RuntimeConsoleMode.Always;
        bool ownsStartupState;
        bool ownsLogs;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            RuntimeLogCapture.Initialize(logMode);
            ownsLogs = true;
        }

        void Start()
        {
            StartAsync().Forget();
        }

        async UniTask StartAsync()
        {
            var token = this.GetCancellationTokenOnDestroy();
            if (GameUI.IsInited || LanguageManager.IsInited || GameRuntime.IsInited)
            {
                throw new InvalidOperationException("[Launch] 框架已由其它入口启动。");
            }
            LanguageManager.Init(new Dictionary<string, LanguageTexts>(StringComparer.Ordinal));
            ownsStartupState = true;
            GameEvents.EnsureDispatcher();
            GameUI.Init();
            GameUI.ConfigureURPCameraStack(Camera.main);

            var resources = new ResourceLoadManager();
            await resources.InitializeAsync(globalConfig, token);
            await resources.UpdatePackageManifestAsync(token);
            GameUI.SetPackage(resources.Package);
            await resources.DownloadByTagAsync(globalConfig.BootTag, token);
            await GameRuntime.InitAsync(new GameRuntimeConfig
            {
                Package = resources.Package,
                InputConfig = inputConfig,
                AudioConfig = audioConfig,
                EnablePool = true,
                EnableScene = true,
                NetworkOptions = new NetworkRuntimeOptions(),
                EnableUI = false
            }, token);
            await resources.LoadHotUpdateDllAsync(globalConfig, token);
            await resources.LoadGameObjectAsync(globalConfig.GameEntryLocation, transform, token);
            Debug.Log($"[Launch] 资源准备完成，资源版本：{resources.Package.GetPackageVersion()}");
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
            if (ownsLogs)
            {
                ownsLogs = false;
                RuntimeLogCapture.Shutdown();
            }
            if (!ownsStartupState)
            {
                return;
            }
            ownsStartupState = false;
            GameUI.Shutdown();
            if (GameRuntime.IsInited)
            {
                GameRuntime.ShutdownAsync().Forget();
            }
            LanguageManager.Shutdown();
            GameEvents.ClearAll();
            GameEvents.ShutdownDispatcher();
        }
    }
}
