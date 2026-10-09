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
using GameFrame.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
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

        [SetUp]
        public void SetUp()
        {
            resources = new ResourceLoadManager();
            config = ScriptableObject.CreateInstance<GlobalConfig>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
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
                    EnableUI = false
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
        public IEnumerator EntryDestructionOnlyClearsHotfixConfiguration() => UniTask.ToCoroutine(async () =>
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
