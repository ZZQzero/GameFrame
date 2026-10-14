using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.AOT;
using Game.Hotfix.TableConfig;
using GameFrame;
using GameFrame.Localization;
using GameFrame.UI;
using UnityEngine;

namespace Game.Hotfix
{
    /// <summary>框架与资源就绪后实例化的 Hotfix 入口，仅初始化热更配置与业务。</summary>
    [DisallowMultipleComponent]
    public sealed class GameEntry : MonoBehaviour
    {
        bool ownsTables;
        [SerializeField] string loginEndpoint = "wss://localhost:7443/network";
        GameLoginSession loginSession;
        GameFlow flow;
        Task stopping;

        public bool IsReady { get; private set; }

        void Awake()
        {
            Initialize();
        }

        void Start()
        {
            if (!IsReady)
            {
                // Awake 已报告初始化异常，后续生命周期不再启动业务。
                return;
            }

            flow.StartAsync().Forget();
        }

        void Initialize()
        {
            var token = this.GetCancellationTokenOnDestroy();
            token.ThrowIfCancellationRequested();
            if (!GameRuntime.IsInited || !GameUI.IsInited || !LanguageManager.IsInited)
            {
                throw new InvalidOperationException("[GameEntry] 请先通过 Launch 启动框架。");
            }
            var resources = ResourceLoadManager.Instance ??
                throw new InvalidOperationException("[GameEntry] 请先通过 Launch 准备资源。");
            TableConfigManager.Init(resources, token);
            ownsTables = true;

            var languageRows = TableConfigManager.Tables.TbLanguage.DataList;
            var languages = new Dictionary<string, LanguageTexts>(languageRows.Count, StringComparer.Ordinal);
            foreach (var row in languageRows)
            {
                languages.Add(row.Key, new LanguageTexts(row.ZhCN, row.EnUS, row.ArSA));
            }

            LanguageManager.AddTable(languages);
            GameUIRegistration.RegisterAll();
            loginSession = new GameLoginSession(GameRuntime.Network, new Uri(loginEndpoint));
            flow = new GameFlow(loginSession);
            IsReady = true;
            Debug.Log("[GameEntry] Hotfix 初始化完成。");
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
            StopAsync().Forget();
        }

        /// <summary>运行中主动停止业务时等待本方法，再关闭 GameRuntime 和资源包。</summary>
        public UniTask StopAsync()
        {
            if (stopping == null)
            {
                var completed = new TaskCompletionSource<bool>();
                stopping = completed.Task;
                _ = StopCoreAsync(completed);
            }

            return stopping.AsUniTask();
        }

        async Task StopCoreAsync(TaskCompletionSource<bool> completed)
        {
            IsReady = false;
            Exception failure = null;
            try
            {
                if (flow != null)
                {
                    await flow.StopAsync();
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                flow = null;
            }

            try
            {
                if (loginSession != null)
                {
                    await loginSession.DisposeAsync();
                }
            }
            catch (Exception error)
            {
                if (failure == null)
                {
                    failure = error;
                }
                else
                {
                    Debug.LogException(error);
                }
            }
            finally
            {
                loginSession = null;
                if (ownsTables)
                {
                    ownsTables = false;
                    TableConfigManager.Shutdown();
                }
            }

            if (failure == null)
            {
                completed.TrySetResult(true);
            }
            else
            {
                completed.TrySetException(failure);
            }
        }
    }
}
