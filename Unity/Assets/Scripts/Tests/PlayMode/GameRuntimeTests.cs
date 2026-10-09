using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using GameFrame.Audio;
using GameFrame.Input;
using GameFrame.Localization;
using GameFrame.Timing;
using GameFrame.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace GameFrame.Tests
{
    public sealed class RuntimeCleanupPanel : UIPanel<UINone>
    {
        public Action Cleanup;
        protected override void OnOpen(UINone args) { }
        protected override void OnDestroyPanel() => Cleanup?.Invoke();
    }

    public sealed class GameRuntimeTests
    {
        readonly List<UnityEngine.Object> objects = new();
        Action rootReady;

        static Dictionary<string, LanguageTexts> LanguageTable() => new()
        {
            ["title"] = new LanguageTexts("标题", "Title", "عنوان")
        };

        T Track<T>(T value) where T : UnityEngine.Object
        {
            objects.Add(value);
            return value;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            if (rootReady != null) GameUI.RootReady -= rootReady;
            await GameRuntime.ShutdownAsync();
            GameUI.Shutdown();
            if (GameInput.IsInited) GameInput.Shutdown();
            if (GameTimer.IsInited) GameTimer.Shutdown();
            LanguageManager.Shutdown();
            foreach (var obj in objects)
                if (obj != null) UnityEngine.Object.Destroy(obj);
            objects.Clear();
            await UniTask.Yield();
        });

        [TestCase(false)]
        [TestCase(true)]
        public void InputEntriesRejectInvalidAssetAndKeepSourceAssetUnchanged(bool useConfig)
        {
            var root = Track(new GameObject("input-validation-root"));
            var actions = Track(ScriptableObject.CreateInstance<InputActionAsset>());
            var config = Track(InputRuntimeConfig.Create(actions));
            void Init()
            {
                if (useConfig) GameInput.Init(root.transform, config);
                else GameInput.Init(root.transform, actions);
            }
            Assert.Throws<InvalidOperationException>(Init);
            Assert.IsFalse(GameInput.IsInited);
            var player = actions.AddActionMap("Player");
            player.AddAction("Move", InputActionType.Value);
            actions.AddActionMap("UI");
            Init();
            Assert.IsTrue(GameInput.IsInited);
            Assert.IsFalse(player.enabled, "运行时必须使用克隆，不能开启原始资产");
            Assert.Throws<InputStateException>(Init);
            Assert.IsTrue(GameInput.IsInited, "重复初始化不能破坏已有输入状态");
            GameInput.Shutdown();
            Assert.IsFalse(player.enabled);
        }

        [Test]
        public void InvalidInputSensitivityIsRejectedBeforeCreatingRuntimeAsset()
        {
            var root = Track(new GameObject("input-validation-root"));
            var actions = Track(ScriptableObject.CreateInstance<InputActionAsset>());
            actions.AddActionMap("Player").AddAction("Move", InputActionType.Value);
            actions.AddActionMap("UI");
            var config = Track(InputRuntimeConfig.Create(actions, lookSensitivity: float.NaN));
            Assert.Throws<InvalidOperationException>(() => GameInput.Init(root.transform, config));
            Assert.IsFalse(GameInput.IsInited);
            GameInput.Init(root.transform, actions);
            Assert.IsTrue(GameInput.IsInited);
        }

        [UnityTest]
        public IEnumerator StartsSelectedSystemsAndPreservesBorrowedRoot() => UniTask.ToCoroutine(async () =>
        {
            var root = Track(new GameObject("runtime-borrowed-root"));
            var actions = Track(ScriptableObject.CreateInstance<InputActionAsset>());
            actions.AddActionMap("Player").AddAction("Move", InputActionType.Value);
            actions.AddActionMap("UI");
            var inputConfig = Track(InputRuntimeConfig.Create(actions));
            int readyCount = 0;
            rootReady = () =>
            {
                readyCount++;
                Assert.IsTrue(GameTimer.IsInited);
                Assert.IsTrue(GameInput.IsInited);
                Assert.IsTrue(LanguageManager.IsInited, "UI 创建前必须初始化多语言");
            };
            GameUI.RootReady += rootReady;

            await GameRuntime.InitAsync(new GameRuntimeConfig
            {
                PersistRoot = root.transform,
                InputConfig = inputConfig,
                LanguageTable = LanguageTable()
            });
            Assert.IsTrue(GameRuntime.IsInited);
            Assert.AreSame(root.transform, GameRuntime.PersistRoot);
            Assert.AreEqual(1, readyCount);
            await GameRuntime.ShutdownAsync();
            await GameRuntime.ShutdownAsync();
            await UniTask.Yield();
            Assert.IsFalse(GameRuntime.IsInited);
            Assert.IsFalse(GameUI.IsInited);
            Assert.IsFalse(GameInput.IsInited);
            Assert.IsFalse(GameTimer.IsInited);
            Assert.IsFalse(LanguageManager.IsInited);
            Assert.IsTrue(root != null, "借用根节点不能由框架销毁");
        });

        [UnityTest]
        public IEnumerator FailureStopsStartupWithoutRollbackAndExplicitShutdownCleansPartialState() => UniTask.ToCoroutine(async () =>
        {
            var original = new InvalidOperationException("runtime-language-startup-failure");
            int attempts = 0;
            LanguageManager.LanguageChanged += _ => { attempts++; throw original; };
            var config = new GameRuntimeConfig { LanguageTable = LanguageTable() };
            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() =>
                GameRuntime.InitAsync(config).GetAwaiter().GetResult()));
            var ownedRoot = GameRuntime.PersistRoot;
            Assert.IsFalse(GameRuntime.IsInited);
            Assert.IsFalse(GameUI.IsInited, "多语言失败后不能继续启动 UI");
            Assert.IsTrue(GameTimer.IsInited, "启动失败不自动回滚已启动的系统");
            Assert.IsTrue(LanguageManager.IsInited, "回调失败留下的状态由显式关闭清理");
            Assert.Throws<InvalidOperationException>(() => GameRuntime.InitAsync(config).GetAwaiter().GetResult());
            Assert.AreEqual(1, attempts, "失败不自动重试");

            await GameRuntime.ShutdownAsync();
            await UniTask.Yield();
            Assert.IsFalse(GameTimer.IsInited);
            Assert.IsFalse(LanguageManager.IsInited);
            Assert.IsTrue(ownedRoot == null);
            await GameRuntime.InitAsync(new GameRuntimeConfig { EnableUI = false });
            Assert.IsTrue(GameRuntime.IsInited, "显式关闭后可以重新启动");
        });

        [Test]
        public void AudioFailureStopsBeforeUiAndKeepsEarlierSystems()
        {
            var audioConfig = Track(ScriptableObject.CreateInstance<AudioRuntimeConfig>());
            Assert.Throws<ArgumentNullException>(() => GameRuntime.InitAsync(new GameRuntimeConfig
            {
                AudioConfig = audioConfig,
                LanguageTable = LanguageTable()
            }).GetAwaiter().GetResult());
            Assert.IsFalse(GameRuntime.IsInited);
            Assert.IsFalse(GameAudio.IsInited);
            Assert.IsFalse(GameUI.IsInited);
            Assert.IsTrue(GameTimer.IsInited);
            Assert.IsTrue(LanguageManager.IsInited);
        }

        [UnityTest]
        public IEnumerator UiCleanupFailureStillClosesRemainingSystemsAndDestroysOwnedRoot() => UniTask.ToCoroutine(async () =>
        {
            await GameRuntime.InitAsync(new GameRuntimeConfig { LanguageTable = LanguageTable() });
            var ownedRoot = GameRuntime.PersistRoot;
            var panel = Track(new GameObject("runtime-cleanup-panel", typeof(RectTransform)))
                .AddComponent<RuntimeCleanupPanel>();
            var original = new InvalidOperationException("runtime-ui-shutdown-failure");
            bool timerAliveDuringCleanup = false;
            panel.Cleanup = () =>
            {
                timerAliveDuringCleanup = GameTimer.IsInited;
                throw original;
            };
            var manager = (UIManager)typeof(GameUI).GetField("_manager", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);
            var cached = (Dictionary<Type, UIPanel>)typeof(UIManager)
                .GetField("_cached", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            cached[typeof(RuntimeCleanupPanel)] = panel;

            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() =>
                GameRuntime.ShutdownAsync().GetAwaiter().GetResult()));
            Assert.IsTrue(timerAliveDuringCleanup, "UI 必须先于 Timer 关闭");
            Assert.IsTrue(panel.DestroyDispatched);
            Assert.IsFalse(GameTimer.IsInited);
            Assert.IsFalse(LanguageManager.IsInited);
            Assert.IsFalse(GameRuntime.IsInited);
            await UniTask.Yield();
            Assert.IsTrue(ownedRoot == null);
        });

        [Test]
        public void DisabledSystemsRemainOwnedByTheirOriginalCaller()
        {
            var root = Track(new GameObject("external-timer-root"));
            GameTimer.Init(root.transform);
            LanguageManager.Init(LanguageTable());
            GameRuntime.InitAsync(new GameRuntimeConfig { EnableTimer = false, EnableUI = false })
                .GetAwaiter().GetResult();
            Assert.IsNull(GameRuntime.PersistRoot, "不需要根节点的配置不应额外创建对象");
            GameRuntime.ShutdownAsync().GetAwaiter().GetResult();
            Assert.IsTrue(GameTimer.IsInited);
            Assert.IsTrue(LanguageManager.IsInited);
        }

        [Test]
        public void AlreadyInitializedSystemIsRejectedWithoutTakingOwnership()
        {
            var root = Track(new GameObject("external-timer-root"));
            GameTimer.Init(root.transform);
            Assert.Throws<InvalidOperationException>(() =>
                GameRuntime.InitAsync(new GameRuntimeConfig()).GetAwaiter().GetResult());
            GameRuntime.ShutdownAsync().GetAwaiter().GetResult();
            Assert.IsTrue(GameTimer.IsInited);
            Assert.IsFalse(GameUI.IsInited);
            Assert.IsNull(GameRuntime.PersistRoot);
        }

        [Test]
        public void CancellationDuringStartupStopsBeforeUiWithoutRollback()
        {
            using var cancellation = new CancellationTokenSource();
            LanguageManager.LanguageChanged += _ => cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => GameRuntime.InitAsync(
                new GameRuntimeConfig { LanguageTable = LanguageTable() }, cancellation.Token)
                .GetAwaiter().GetResult());
            Assert.IsTrue(GameTimer.IsInited);
            Assert.IsTrue(LanguageManager.IsInited);
            Assert.IsFalse(GameUI.IsInited);
            Assert.IsFalse(GameRuntime.IsInited);
        }

        [Test]
        public void ShutdownDuringStartupIsRejectedAndHaltsStartup()
        {
            LanguageManager.LanguageChanged += _ => GameRuntime.ShutdownAsync().GetAwaiter().GetResult();
            Assert.Throws<InvalidOperationException>(() => GameRuntime.InitAsync(
                new GameRuntimeConfig { LanguageTable = LanguageTable() }).GetAwaiter().GetResult());
            Assert.IsFalse(GameUI.IsInited);
            Assert.IsTrue(GameTimer.IsInited);
            Assert.IsFalse(GameRuntime.IsInited);
        }
    }
}
