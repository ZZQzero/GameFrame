using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Game.AOT;
using Game.Hotfix;
using Game.Hotfix.TableConfig;
using GameFrame.Localization;
using GameFrame.Network;
using GameFrame.Scene;
using GameFrame.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YooAsset;
using GameEvents = GameFrame.Event.EventSystem;

namespace GameFrame.Tests
{
    public sealed class ResourceLoadManagerTests
    {
        ResourceLoadManager resources;
        GlobalConfig config;
        GameEntry entry;
        GameObject instance;
        UnityEngine.SceneManagement.Scene testScene;

        [SetUp]
        public void SetUp()
        {
            testScene = SceneManager.GetActiveScene();
            resources = new ResourceLoadManager();
            config = ScriptableObject.CreateInstance<GlobalConfig>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            if (entry != null)
            {
                await entry.StopAsync();
            }

            var mainScene = SceneManager.GetSceneByPath("Assets/Scenes/MainScene/Main.unity");
            if (mainScene.IsValid() && mainScene.isLoaded)
            {
                // Single 切场已卸掉测试场景；先准备空场，才能卸载唯一的内容场景。
                SceneManager.SetActiveScene(testScene.IsValid() && testScene.isLoaded
                    ? testScene : SceneManager.CreateScene("ResourceLoadManagerTests"));
                if (GameScene.IsInited && GameScene.IsLoaded("Main"))
                {
                    await GameScene.UnloadAsync("Main");
                }
                else
                {
                    await SceneManager.UnloadSceneAsync(mainScene);
                }
            }

            GameUI.Shutdown();
            if (GameRuntime.IsInited)
            {
                await GameRuntime.ShutdownAsync();
            }
            TableConfigManager.Shutdown();
            LanguageManager.Shutdown();
            GameEvents.ClearAll();
            GameEvents.ShutdownDispatcher();
            await resources.ShutdownAsync();
            UnityEngine.Object.Destroy(config);
            await UniTask.NextFrame();
            Assert.IsFalse(YooAssets.IsInitialized);
            Assert.IsFalse(GameRuntime.IsInited);
            Assert.IsNull(ResourceLoadManager.Instance);
        });

        [TestCase(EPlayMode.None)]
        [TestCase(EPlayMode.HostPlayMode)]
        public void InvalidSettingsFailBeforeTakingResourceOwnership(EPlayMode mode)
        {
            config.PlayMode = mode;
            Assert.Throws<InvalidOperationException>(() => resources.InitializeAsync(config).GetAwaiter().GetResult());
            Assert.IsFalse(YooAssets.IsInitialized);
            Assert.IsNull(resources.Package);
        }

        [Test]
        public void CancelledStartupDoesNotInitializeResources()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                resources.InitializeAsync(config, cancellation.Token).GetAwaiter().GetResult());
            Assert.IsFalse(YooAssets.IsInitialized);
        }

        async UniTask PrepareAsync(bool initializeFramework = false)
        {
            if (initializeFramework)
            {
                LanguageManager.Init(new Dictionary<string, LanguageTexts>(StringComparer.Ordinal));
                GameEvents.EnsureDispatcher();
                GameUI.Init();
            }
            await resources.InitializeAsync(config);
            // PackageValid 依赖已加载的清单，更新清单只能要求初始化成功。
            await resources.UpdatePackageManifestAsync();
            if (initializeFramework)
            {
                GameUI.SetPackage(resources.Package);
            }
            await resources.DownloadByTagAsync(config.BootTag);
            if (initializeFramework)
            {
                await GameRuntime.InitAsync(new GameRuntimeConfig
                {
                    Package = resources.Package,
                    EnablePool = true,
                    EnableScene = true,
                    EnableUI = false,
                    NetworkOptions = new NetworkRuntimeOptions()
                });
            }
            await resources.LoadHotUpdateDllAsync(config);
        }

        [UnityTest]
        public IEnumerator GeneratedTablesLoadWithoutMaintainingAnotherNameList() => UniTask.ToCoroutine(async () =>
        {
            await PrepareAsync();
            Assert.AreEqual(1f, resources.DownloadProgress);
            Assert.IsTrue(resources.ContainsLocation("tblanguage"));
            Assert.IsTrue(resources.ContainsLocation("GameEntry"));
            Assert.IsFalse(resources.ContainsLocation("not-a-resource"));
            var syncBytes = resources.LoadBytes("tblanguage");
            var asyncBytes = await resources.LoadBytesAsync("tblanguage");
            CollectionAssert.AreEqual(syncBytes, asyncBytes);

            TableConfigManager.Init(resources);
            Assert.Greater(TableConfigManager.Tables.TbLanguage.DataList.Count, 0);
            Assert.Throws<InvalidOperationException>(() => TableConfigManager.Init(resources));
            await resources.UnloadUnusedAssetsAsync();
            Assert.Greater(TableConfigManager.Tables.TbLanguage.DataList.Count, 0);
            Assert.IsFalse(GameRuntime.IsInited);
        });

        [UnityTest]
        public IEnumerator PrefabAwakeInitializesAndFrameworkCanBeClosedWithoutClosingResources() => UniTask.ToCoroutine(async () =>
        {
            await PrepareAsync(true);
            var startupCanvas = GameUI.CanvasRoot;
            instance = await resources.LoadGameObjectAsync(config.GameEntryLocation, null);
            entry = instance.GetComponent<GameEntry>();
            Assert.IsNotNull(entry);
            await UniTask.WaitUntil(() => entry.IsReady).Timeout(TimeSpan.FromSeconds(15));
            Assert.IsTrue(entry.IsReady);
            Assert.IsTrue(GameRuntime.IsInited);
            Assert.AreSame(startupCanvas, GameUI.CanvasRoot, "Hotfix 应复用 Launch 已创建的 UI。");

            await resources.UnloadUnusedAssetsAsync();
            Assert.IsTrue(instance != null, "入口实例存活期间必须保留资源句柄。");
            await entry.StopAsync();
            await GameRuntime.ShutdownAsync();
            await resources.ReleaseGameObjectAsync(instance);
            Assert.IsNull(TableConfigManager.Tables);
            Assert.IsFalse(GameRuntime.IsInited);
            Assert.IsTrue(GameUI.IsInited);
            Assert.IsTrue(LanguageManager.IsInited);
            Assert.IsTrue(instance == null);
            Assert.IsTrue(YooAssets.IsInitialized);
            Assert.AreSame(resources, ResourceLoadManager.Instance);
            Assert.IsTrue(resources.ContainsLocation("GameEntry"));
        });

        [UnityTest]
        public IEnumerator RejectedEntryDoesNotClearTablesOwnedByAnotherCaller() => UniTask.ToCoroutine(async () =>
        {
            await PrepareAsync(true);
            TableConfigManager.Init(resources);
            var existingTables = TableConfigManager.Tables;
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException:.*配置已初始化"));
            instance = await resources.LoadGameObjectAsync(config.GameEntryLocation, null);
            entry = instance.GetComponent<GameEntry>();
            await resources.ReleaseGameObjectAsync(instance);
            Assert.AreSame(existingTables, TableConfigManager.Tables);
            Assert.IsTrue(GameRuntime.IsInited);
            Assert.AreSame(resources, ResourceLoadManager.Instance);
        });

        [UnityTest]
        public IEnumerator EntryDestructionOnlyStopsHotfixBusiness() => UniTask.ToCoroutine(async () =>
        {
            await PrepareAsync(true);
            instance = await resources.LoadGameObjectAsync(config.GameEntryLocation, null);
            entry = instance.GetComponent<GameEntry>();
            UnityEngine.Object.Destroy(instance);
            await UniTask.NextFrame();
            Assert.IsTrue(instance == null);
            Assert.IsTrue(GameRuntime.IsInited);
            Assert.IsTrue(GameUI.IsInited);
            Assert.IsTrue(LanguageManager.IsInited);
            Assert.IsNull(TableConfigManager.Tables);
            Assert.IsTrue(YooAssets.IsInitialized);
            Assert.AreSame(resources, ResourceLoadManager.Instance);
        });

        async UniTask<GameLoginSession> CreateLoginEntryAsync()
        {
            string endpoint = Environment.GetEnvironmentVariable("GF_LOGIN_TEST_ENDPOINT");
            if (string.IsNullOrEmpty(endpoint))
            {
                Assert.Ignore("Real entry login requires GF_LOGIN_TEST_ENDPOINT and a local WSS/PostgreSQL server.");
            }

            await PrepareAsync(true);
            // 在 Awake 前注入测试端点，避免修改正式 prefab 或使用玩家数据库。
            var parent = new GameObject("inactive-entry-test-parent");
            parent.SetActive(false);
            try
            {
                instance = await resources.LoadGameObjectAsync(config.GameEntryLocation, parent.transform);
                entry = instance.GetComponent<GameEntry>();
                typeof(GameEntry).GetField("loginEndpoint", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(entry, endpoint);
                instance.transform.SetParent(null);
                // 实际入口是持久 Launch 的子节点；测试独立实例同样需要跨 Single 切场存活。
                UnityEngine.Object.DontDestroyOnLoad(instance);
            }
            finally
            {
                UnityEngine.Object.Destroy(parent);
            }

            await UniTask.WaitUntil(() => GameUI.IsOpen<LoginPanel>()).Timeout(TimeSpan.FromSeconds(15));
            return (GameLoginSession)typeof(GameEntry).GetField("loginSession", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(entry);
        }

        static void SubmitLogin(string account)
        {
            LoginPanel login = GameUI.Get<LoginPanel>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            ((TMP_InputField)typeof(LoginPanel).GetField("accountInput", flags).GetValue(login)).text =
                account;
            ((TMP_InputField)typeof(LoginPanel).GetField("passwordInput", flags).GetValue(login)).text = "test-password";
            ((Button)typeof(LoginPanel).GetField("confirmLoginButton", flags).GetValue(login)).onClick.Invoke();
        }

        async UniTask<GameLoginSession> LoginToMainAsync()
        {
            GameLoginSession session = await CreateLoginEntryAsync();
            SubmitLogin("entry_" + Guid.NewGuid().ToString("N"));
            await UniTask.WaitUntil(() => GameUI.IsOpen<MainPanel>()).Timeout(TimeSpan.FromSeconds(15));
            Assert.AreEqual("Main", SceneManager.GetActiveScene().name);
            Assert.AreEqual("Main", GameScene.ActiveId);
            Assert.IsTrue(GameScene.IsLoaded("Main"));
            Assert.IsTrue(entry.IsReady, "切场不能销毁持久业务入口或账号会话。");
            CollectionAssert.AreEqual(new[] { GameUI.UICamera }, Camera.main.GetUniversalAdditionalCameraData().cameraStack);
            Assert.IsTrue(session.IsAuthenticated);
            Assert.IsFalse(GameUI.IsOpen<LoginPanel>());
            Assert.IsFalse(GameUI.IsOpen<GomokuMainPanel>());
            Assert.IsFalse(GameUI.IsOpen<WoodenFishMainPanel>());
            return session;
        }

        [UnityTest]
        public IEnumerator ConnectionLossWhileRunningReturnsToLogin() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = await LoginToMainAsync();
            GameUser user = session.CurrentUser;
            ISceneHandle mainHandle = GameScene.Loaded["Main"];
            await session.LobbyClient.CloseAsync();
            await UniTask.WaitUntil(() => GameUI.IsOpen<LoginPanel>()).Timeout(TimeSpan.FromSeconds(5));
            Assert.IsTrue(GameRuntime.IsInited);
            Assert.IsTrue(entry.IsReady);
            Assert.IsNull(session.CurrentUser);
            Assert.IsFalse(GameUI.IsOpen<MainPanel>());
            SubmitLogin(user.AccountName);
            await UniTask.WaitUntil(() => GameUI.IsOpen<MainPanel>()).Timeout(TimeSpan.FromSeconds(15));
            Assert.AreSame(mainHandle, GameScene.Loaded["Main"], "重新登录复用 Main，不重复加载场景。");
            Assert.AreEqual(user.AccountId, session.CurrentUser.AccountId);
            Assert.Greater(session.CurrentUser.AuthEpoch, user.AuthEpoch);
            CollectionAssert.AreEqual(new[] { GameUI.UICamera }, Camera.main.GetUniversalAdditionalCameraData().cameraStack);
        });

        sealed class GatedSceneLoader : ISceneLoader
        {
            readonly ISceneLoader inner;
            public readonly UniTaskCompletionSource Started = new UniTaskCompletionSource();
            public readonly UniTaskCompletionSource Continue = new UniTaskCompletionSource();

            public GatedSceneLoader(ResourcePackage package)
            {
                inner = new YooAssetSceneLoader(package);
            }

            public async UniTask<ISceneHandle> LoadAsync(string location, LoadSceneMode mode,
                bool allowSceneActivation, Action<float> report)
            {
                Started.TrySetResult();
                await Continue.Task;
                return await inner.LoadAsync(location, mode, allowSceneActivation, report);
            }
        }

        [UnityTest]
        public IEnumerator FlowStopClosesUIButLeavesBorrowedAccountSessionAlive() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = await LoginToMainAsync();
            GameUser user = session.CurrentUser;
            var flow = (GameFlow)typeof(GameEntry).GetField("flow", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(entry);
            await flow.StopAsync();
            await flow.StopAsync();
            Assert.IsTrue(entry.IsReady, "流程停止不负责停止组合入口或配置。");
            Assert.AreSame(user, session.CurrentUser, "流程只借用会话，不注销或释放账号连接。");
            Assert.IsTrue(GameRuntime.IsInited);
            Assert.IsFalse(GameUI.IsOpen<MainPanel>());
            Assert.IsFalse(GameUI.IsOpen<LoginPanel>());
            Assert.Throws<ObjectDisposedException>(() => flow.StartAsync());

            await session.LogoutAsync();
            await UniTask.NextFrame();
            Assert.IsFalse(GameUI.IsOpen<LoginPanel>(), "停止后认证失效不能再次启动导航。");
            await entry.StopAsync();
            await entry.StopAsync();
            Assert.IsFalse(entry.IsReady);
            Assert.IsNull(TableConfigManager.Tables);
            Assert.AreEqual(0, GameRuntime.Network.Memory.OwnedBytes);
        });

        [UnityTest]
        public IEnumerator ReloginDuringPreviousMainLoadEntersWithNewAccountSession() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = await CreateLoginEntryAsync();
            await GameScene.ShutdownAsync();
            var loader = new GatedSceneLoader(resources.Package);
            GameScene.InitLoader(loader);
            try
            {
                SubmitLogin("entry_" + Guid.NewGuid().ToString("N"));
                await loader.Started.Task.Timeout(TimeSpan.FromSeconds(15));
                GameUser previousUser = session.CurrentUser;
                await session.LobbyClient.CloseAsync();
                await UniTask.WaitUntil(() => GameUI.IsOpen<LoginPanel>()).Timeout(TimeSpan.FromSeconds(5));
                SubmitLogin("entry_" + Guid.NewGuid().ToString("N"));
                await UniTask.WaitUntil(() => session.IsAuthenticated).Timeout(TimeSpan.FromSeconds(15));
                GameUser currentUser = session.CurrentUser;
                Assert.AreNotEqual(previousUser.AccountId, currentUser.AccountId);
                Assert.IsFalse(GameUI.IsOpen<MainPanel>());

                loader.Continue.TrySetResult();
                await UniTask.WaitUntil(() => GameUI.IsOpen<MainPanel>()).Timeout(TimeSpan.FromSeconds(15));
                Assert.AreSame(currentUser, session.CurrentUser);
                Assert.AreEqual("Main", GameScene.ActiveId);
                Assert.IsFalse(GameUI.IsOpen<LoginPanel>());
                CollectionAssert.AreEqual(new[] { GameUI.UICamera }, Camera.main.GetUniversalAdditionalCameraData().cameraStack);
            }
            finally
            {
                loader.Continue.TrySetResult();
            }
        });

        [UnityTest]
        public IEnumerator AuthenticationLossDuringMainLoadDoesNotOpenMain() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = await CreateLoginEntryAsync();
            await GameScene.ShutdownAsync();
            var loader = new GatedSceneLoader(resources.Package);
            GameScene.InitLoader(loader);
            string account = "entry_" + Guid.NewGuid().ToString("N");
            try
            {
                SubmitLogin(account);
                await loader.Started.Task.Timeout(TimeSpan.FromSeconds(15));
                await session.LobbyClient.CloseAsync();
                Assert.IsNull(session.CurrentUser);
                Assert.IsTrue(GameUI.IsOpen<LoginPanel>());
                loader.Continue.TrySetResult();
                await GameScene.WaitForIdleAsync();
                await UniTask.NextFrame();
                Assert.AreEqual("Main", SceneManager.GetActiveScene().name);
                Assert.IsFalse(GameUI.IsOpen<MainPanel>(), "迟到的切场结果不能进入已失效账号的业务。");
                Assert.IsTrue(GameUI.IsOpen<LoginPanel>());
                CollectionAssert.AreEqual(new[] { GameUI.UICamera }, Camera.main.GetUniversalAdditionalCameraData().cameraStack);
                SubmitLogin(account);
                await UniTask.WaitUntil(() => GameUI.IsOpen<MainPanel>()).Timeout(TimeSpan.FromSeconds(15));
                Assert.IsTrue(session.IsAuthenticated);
            }
            finally
            {
                loader.Continue.TrySetResult();
            }
        });

        [UnityTest]
        public IEnumerator StopWaitsForMainLoadWithoutOpeningBusinessUI() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = await CreateLoginEntryAsync();
            await GameScene.ShutdownAsync();
            var loader = new GatedSceneLoader(resources.Package);
            GameScene.InitLoader(loader);
            try
            {
                SubmitLogin("entry_" + Guid.NewGuid().ToString("N"));
                await loader.Started.Task.Timeout(TimeSpan.FromSeconds(15));
                var stopped = entry.StopAsync().AsTask();
                Assert.IsFalse(entry.IsReady);
                Assert.IsFalse(stopped.IsCompleted, "业务主动停止必须等待自己发起的切场。");
                loader.Continue.TrySetResult();
                await stopped;
                Assert.IsFalse(GameScene.IsBusy);
                Assert.IsTrue(GameRuntime.IsInited);
                Assert.IsNull(session.CurrentUser);
                Assert.IsFalse(GameUI.IsOpen<LoginPanel>());
                Assert.IsFalse(GameUI.IsOpen<MainPanel>());
                Assert.AreEqual(0, GameRuntime.Network.Memory.OwnedBytes);
            }
            finally
            {
                loader.Continue.TrySetResult();
            }
        });

        [UnityTest]
        public IEnumerator UIShutdownBeforeFrameworkDoesNotNavigateOnAuthenticationLoss() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = await LoginToMainAsync();
            int lost = 0;
            session.AuthenticationLost += () => lost++;
            GameUI.Shutdown();
            await GameRuntime.ShutdownAsync();
            Assert.IsTrue(entry.IsReady, "退出时允许框架先于业务入口收到关闭回调。");
            Assert.AreEqual(1, lost, "网络关闭仍应清除认证并通知观察者。");
            Assert.IsNull(session.CurrentUser);
            Assert.IsFalse(GameUI.IsInited);
            await entry.StopAsync();
            Assert.IsFalse(entry.IsReady);
        });

        [UnityTest]
        public IEnumerator FrameworkShutdownWithUIAliveDoesNotReopenLogin() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = await LoginToMainAsync();
            int lost = 0;
            session.AuthenticationLost += () => lost++;
            await GameRuntime.ShutdownAsync();
            await UniTask.NextFrame();
            Assert.IsTrue(GameUI.IsInited);
            Assert.IsTrue(entry.IsReady);
            Assert.AreEqual(1, lost);
            Assert.IsNull(session.CurrentUser);
            Assert.IsFalse(GameUI.IsOpen<LoginPanel>(), "框架停止后不能把断线当作新的登录导航。");
            await entry.StopAsync();
            Assert.IsFalse(GameUI.IsOpen<MainPanel>());
        });

        [UnityTest]
        public IEnumerator LaunchCreatesUIBeforeResourcesAndHotfixReusesIt() => UniTask.ToCoroutine(async () =>
        {
            var cameraObject = new GameObject("launch-test-camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            var cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.renderType = CameraRenderType.Base;
            var launchObject = new GameObject("launch-test");
            launchObject.SetActive(false);
            var launch = launchObject.AddComponent<Launch>();
            typeof(Launch).GetField("globalConfig", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(launch, config);
            RectTransform startupCanvas = null;
            var resourcesWereReady = true;
            var languageWasReady = false;
            var startupLanguage = default(GameLanguage);
            Action rootReady = () =>
            {
                startupCanvas = GameUI.CanvasRoot;
                resourcesWereReady = YooAssets.IsInitialized;
                languageWasReady = LanguageManager.IsInited;
                startupLanguage = LanguageManager.Current;
            };
            GameUI.RootReady += rootReady;
            try
            {
                launchObject.SetActive(true);
                await UniTask.WaitUntil(() =>
                {
                    var gameEntry = UnityEngine.Object.FindFirstObjectByType<GameEntry>();
                    return gameEntry != null && gameEntry.IsReady;
                }).Timeout(TimeSpan.FromSeconds(15));

                Assert.IsNotNull(startupCanvas);
                Assert.IsFalse(resourcesWereReady, "启动 UI 必须先于 YooAsset 初始化。");
                Assert.IsTrue(languageWasReady, "语言服务必须在创建启动 UI 前就绪。");
                Assert.AreEqual(startupLanguage, LanguageManager.Current, "补入热更翻译不能重置当前语言。");
                Assert.IsTrue(GameRuntime.IsInited);
                Assert.Greater(TableConfigManager.Tables.TbLanguage.DataList.Count, 0);
                Assert.AreSame(startupCanvas, GameUI.CanvasRoot);
                Assert.IsTrue(cameraData.cameraStack.Contains(GameUI.UICamera));
                var handle = await GameUI.LoadAsset<GameObject>(config.GameEntryLocation, default);
                try
                {
                    Assert.IsNotNull(handle.GetAssetObject<GameObject>(), "资源就绪后 UI 应已绑定资源包。");
                }
                finally
                {
                    handle.Release();
                }
            }
            finally
            {
                GameUI.RootReady -= rootReady;
                resources = ResourceLoadManager.Instance ?? resources;
                UnityEngine.Object.Destroy(launchObject);
                UnityEngine.Object.Destroy(cameraObject);
                await UniTask.NextFrame();
            }
        });

        [UnityTest]
        public IEnumerator GenericAssetLoadTransfersHandleOwnershipToCaller() => UniTask.ToCoroutine(async () =>
        {
            await PrepareAsync();
            var handle = await resources.LoadAssetAsync<GameObject>("GameEntry");
            try
            {
                await resources.UnloadUnusedAssetsAsync();
                Assert.IsTrue(handle.IsValid);
                Assert.IsNotNull(handle.GetAssetObject<GameObject>());
            }
            finally
            {
                handle.Release();
            }
            Assert.IsFalse(handle.IsValid);
        });
    }
}
