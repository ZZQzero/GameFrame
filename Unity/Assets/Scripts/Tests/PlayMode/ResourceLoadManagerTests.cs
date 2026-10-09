using System;
using System.Collections;
using System.Threading;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using GameFrame.AOT;
using GameFrame.Hotfix;
using GameFrame.Hotfix.TableConfig;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YooAsset;

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
            if (GameRuntime.IsInited)
            {
                await GameRuntime.ShutdownAsync();
            }
            TableConfigManager.Shutdown();
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

        async UniTask PrepareAsync()
        {
            await resources.InitializeAsync(config);
            // PackageValid 依赖已加载的清单，更新清单只能要求初始化成功。
            await resources.UpdatePackageManifestAsync();
            await resources.DownloadByTagAsync(config.BootTag);
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
            await PrepareAsync();
            instance = await resources.LoadGameObjectAsync(config.GameEntryLocation, null);
            entry = instance.GetComponent<GameEntry>();
            Assert.IsNotNull(entry);
            await UniTask.WaitUntil(() => entry.IsReady).Timeout(TimeSpan.FromSeconds(15));
            Assert.IsTrue(entry.IsReady);
            Assert.IsTrue(GameRuntime.IsInited);

            await resources.UnloadUnusedAssetsAsync();
            Assert.IsTrue(instance != null, "入口实例存活期间必须保留资源句柄。");
            await GameRuntime.ShutdownAsync();
            await resources.ReleaseGameObjectAsync(instance);
            Assert.IsNull(TableConfigManager.Tables);
            Assert.IsFalse(GameRuntime.IsInited);
            Assert.IsTrue(instance == null);
            Assert.IsTrue(YooAssets.IsInitialized);
            Assert.AreSame(resources, ResourceLoadManager.Instance);
            Assert.IsTrue(resources.ContainsLocation("GameEntry"));
        });

        [UnityTest]
        public IEnumerator RejectedEntryDoesNotClearTablesOwnedByAnotherCaller() => UniTask.ToCoroutine(async () =>
        {
            await PrepareAsync();
            TableConfigManager.Init(resources);
            var existingTables = TableConfigManager.Tables;
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException:.*配置已初始化"));
            instance = await resources.LoadGameObjectAsync(config.GameEntryLocation, null);
            entry = instance.GetComponent<GameEntry>();
            await resources.ReleaseGameObjectAsync(instance);
            Assert.AreSame(existingTables, TableConfigManager.Tables);
            Assert.IsFalse(GameRuntime.IsInited);
            Assert.AreSame(resources, ResourceLoadManager.Instance);
        });

        [UnityTest]
        public IEnumerator EditorEntryDestructionDoesNotCloseResourceService() => UniTask.ToCoroutine(async () =>
        {
            await PrepareAsync();
            instance = await resources.LoadGameObjectAsync(config.GameEntryLocation, null);
            entry = instance.GetComponent<GameEntry>();
            UnityEngine.Object.Destroy(instance);
            await UniTask.NextFrame();
            await UniTask.WaitUntil(() => !GameRuntime.IsInited).Timeout(TimeSpan.FromSeconds(15));
            Assert.IsTrue(instance == null);
            Assert.IsFalse(GameRuntime.IsInited);
            Assert.IsNull(TableConfigManager.Tables);
            Assert.IsTrue(YooAssets.IsInitialized);
            Assert.AreSame(resources, ResourceLoadManager.Instance);
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
