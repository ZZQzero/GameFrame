using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using GameFrame.Audio;
using GameFrame.Input;
using GameFrame.Localization;
using GameFrame.Pooling;
using GameFrame.Scene;
using GameFrame.Timing;
using GameFrame.UI;
using UnityEngine;

namespace GameFrame
{
    /// <summary>主线程上的统一生命周期入口。启动失败直接抛出，不重试、不自动回滚。</summary>
    public static class GameRuntime
    {
        enum RuntimeState { None, Starting, Running, Failed, Stopping }

        static RuntimeState state;
        static Transform root;
        static bool ownsRoot;
        static bool timer, input, pool, scene, language, audio, ui;

        public static bool IsInited => state == RuntimeState.Running;
        public static Transform PersistRoot => root;

        /// <summary>
        /// 顺序启动选中的系统。失败后终止启动；需要清理时显式调用 ShutdownAsync。
        /// 不接管已由其它入口初始化的系统，也不允许未关闭就再次启动。
        /// </summary>
        public static async UniTask InitAsync(
            GameRuntimeConfig config,
            CancellationToken cancellationToken = default)
        {
            RequireMainThread();
            if (state != RuntimeState.None)
                throw new InvalidOperationException("GameRuntime 已启动或上次启动失败，请先 ShutdownAsync。");
            if (config == null) throw new ArgumentNullException(nameof(config));
            cancellationToken.ThrowIfCancellationRequested();
            if ((config.EnableTimer && GameTimer.IsInited) ||
                (config.InputConfig != null && GameInput.IsInited) ||
                (config.EnablePool && GamePool.IsInited) ||
                (config.EnableScene && GameScene.IsInited) ||
                (config.LanguageTable != null && LanguageManager.IsInited) ||
                (config.AudioConfig != null && GameAudio.IsInited) ||
                (config.EnableUI && GameUI.IsInited))
                throw new InvalidOperationException("选中的系统已经初始化，请统一使用一个启动入口。");

            var package = config.Package;
            bool enableUI = config.EnableUI;
            state = RuntimeState.Starting;
            try
            {
                root = config.PersistRoot;
                if (config.EnableTimer || config.InputConfig != null || config.EnablePool)
                {
                    if (root == null)
                    {
                        root = new GameObject("[GameRuntime]").transform;
                        ownsRoot = true;
                        if (Application.isPlaying)
                            UnityEngine.Object.DontDestroyOnLoad(root.gameObject);
                    }
                    if (!root.gameObject.activeInHierarchy)
                        throw new InvalidOperationException("GameRuntime 的持久根节点必须处于激活状态。");
                }

                // 记录由本入口启动的系统，显式关闭时也能处理初始化中途留下的状态。
                if (config.EnableTimer)
                {
                    timer = true;
                    GameTimer.Init(root, config.TimerOptions);
                }
                if (config.InputConfig != null)
                {
                    input = true;
                    GameInput.Init(root, config.InputConfig);
                }
                if (config.EnablePool)
                {
                    pool = true;
                    GamePool.Init(package, root);
                }
                if (config.EnableScene)
                {
                    scene = true;
                    GameScene.Init(package);
                }
                if (config.LanguageTable != null)
                {
                    language = true;
                    LanguageManager.Init(config.LanguageTable);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (config.AudioConfig != null)
                {
                    audio = true;
                    await GameAudio.InitAsync(package, config.AudioConfig, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (enableUI)
                {
                    ui = true;
                    if (package == null) GameUI.Init();
                    else GameUI.Init(package);
                }
                cancellationToken.ThrowIfCancellationRequested();
                state = RuntimeState.Running;
            }
            catch
            {
                state = RuntimeState.Failed;
                throw;
            }
        }

        /// <summary>
        /// 关闭本入口管理的系统；未启动时为空操作。先停止业务任务、释放 Media/数据库实例，
        /// 再等待本方法完成，最后释放资源包或调用 Application.Quit。
        /// </summary>
        public static async UniTask ShutdownAsync()
        {
            RequireMainThread();
            if (state == RuntimeState.None) return;
            if (state == RuntimeState.Starting || state == RuntimeState.Stopping)
                throw new InvalidOperationException("GameRuntime 正在启动或关闭，不允许重入。");
            state = RuntimeState.Stopping;
            var failure = new CleanupFailure();
            if (ui && GameUI.IsInited) failure.Run(GameUI.Shutdown);
            if (scene && GameScene.IsInited)
            {
                try { await GameScene.ShutdownAsync(); }
                catch (Exception exception) { failure.Capture(exception); }
            }
            if (audio && GameAudio.IsInited)
            {
                try { await GameAudio.ShutdownAsync(); }
                catch (Exception exception) { failure.Capture(exception); }
            }
            if (input && GameInput.IsInited) failure.Run(GameInput.Shutdown);
            if (pool && GamePool.IsInited) failure.Run(GamePool.Shutdown);
            if (timer && GameTimer.IsInited) failure.Run(GameTimer.Shutdown);
            if (language && LanguageManager.IsInited) failure.Run(LanguageManager.Shutdown);
            if (ownsRoot && root != null)
                failure.Run(() =>
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(root.gameObject);
                    else UnityEngine.Object.DestroyImmediate(root.gameObject);
                });
            ResetState();
            failure.Throw();
        }

        static void RequireMainThread()
        {
            if (!PlayerLoopHelper.IsMainThread)
                throw new InvalidOperationException("GameRuntime 的初始化和关闭必须在 Unity 主线程调用。");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState()
        {
            state = RuntimeState.None;
            root = null;
            ownsRoot = timer = input = pool = scene = language = audio = ui = false;
        }
    }
}
