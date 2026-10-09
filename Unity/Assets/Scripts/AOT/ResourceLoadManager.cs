using System;
using System.Collections.Generic;
#if !UNITY_EDITOR
using System.Reflection;
#endif
using System.Threading;
using Cysharp.Threading.Tasks;
using Luban;
using UnityEngine;
using YooAsset;

namespace Game.AOT
{
    /// <summary>Launch 创建的资源服务；运行期间需要关闭时由调用方显式释放。</summary>
    public sealed class ResourceLoadManager
    {
        bool ownsYooAssets;
        readonly Dictionary<GameObject, AssetHandle> instances = new();
        public static ResourceLoadManager Instance { get; private set; }
        public ResourcePackage Package { get; private set; }
        public float DownloadProgress { get; private set; }
        public long TotalDownloadBytes { get; private set; }
        public long CurrentDownloadBytes { get; private set; }
        public int TotalDownloadCount { get; private set; }

        public async UniTask InitializeAsync(GlobalConfig config, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }
            config.Validate();
            if (YooAssets.IsInitialized)
            {
                throw new InvalidOperationException("[Launch] YooAssets 已由其它入口初始化。");
            }

            var options = CreateOptions(config);
            YooAssets.Initialize();
            ownsYooAssets = true;
            Instance = this;
            Package = YooAssets.CreatePackage(config.PackageName);
            await WaitAsync(Package.InitializePackageAsync(options), "初始化资源包", token);
        }

        public async UniTask UpdatePackageManifestAsync(CancellationToken token = default)
        {
            RequirePackage(false);
            token.ThrowIfCancellationRequested();
            var version = Package.RequestPackageVersionAsync(new RequestPackageVersionOptions(true, 60));
            await WaitAsync(version, "获取资源版本", token);
            await WaitAsync(Package.LoadPackageManifestAsync(
                new LoadPackageManifestOptions(version.PackageVersion, 60)), "加载资源清单", token);
        }

        public UniTask DownloadByTagAsync(string tag, CancellationToken token = default) =>
            DownloadByTagsAsync(new[] { tag }, token);

        public async UniTask DownloadByTagsAsync(string[] tags, CancellationToken token = default)
        {
            RequirePackage();
            token.ThrowIfCancellationRequested();
            if (tags == null || tags.Length == 0)
            {
                throw new ArgumentException("[Resource] 下载标签不能为空。", nameof(tags));
            }
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    throw new ArgumentException("[Resource] 下载标签不能为空。", nameof(tags));
                }
            }

            var downloader = Package.CreateResourceDownloader(new ResourceDownloaderOptions(tags, 4, 0));
            TotalDownloadBytes = downloader.TotalDownloadBytes;
            TotalDownloadCount = downloader.TotalDownloadCount;
            CurrentDownloadBytes = 0;
            DownloadProgress = 0f;
            void OnProgress(DownloadProgressChangedEventArgs progress)
            {
                DownloadProgress = progress.Progress;
                CurrentDownloadBytes = progress.CurrentDownloadBytes;
            }

            downloader.DownloadProgressChanged += OnProgress;
            try
            {
                downloader.StartDownload();
                using (token.Register(downloader.CancelDownload))
                {
                    await WaitAsync(downloader, "下载资源", token);
                }
            }
            finally
            {
                downloader.DownloadProgressChanged -= OnProgress;
            }
            CurrentDownloadBytes = TotalDownloadBytes;
            DownloadProgress = 1f;
        }

        static InitializePackageOptions CreateOptions(GlobalConfig config)
        {
            switch (config.PlayMode)
            {
#if UNITY_EDITOR
                case EPlayMode.EditorSimulateMode:
                {
                    var build = EditorSimulateBuildInvoker.Build(config.PackageName, (int)EBundleType.VirtualAssetBundle);
                    return new EditorSimulateModeOptions
                    {
                        EditorFileSystemParameters = FileSystemParameters.CreateDefaultEditorFileSystemParameters(
                            build.PackageRootDirectory)
                    };
                }
#endif
                case EPlayMode.OfflinePlayMode:
                    return new OfflinePlayModeOptions
                    {
                        BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters()
                    };
                case EPlayMode.HostPlayMode:
                    return new HostPlayModeOptions
                    {
                        BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                        CacheFileSystemParameters = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(
                            new RemoteService(config.ServerUrl))
                    };
                case EPlayMode.WebPlayMode:
                    return new WebPlayModeOptions
                    {
                        WebServerFileSystemParameters = FileSystemParameters.CreateDefaultWebServerFileSystemParameters(),
                        WebNetworkFileSystemParameters = FileSystemParameters.CreateDefaultWebNetworkFileSystemParameters(
                            new RemoteService(config.ServerUrl))
                    };
                default:
                    throw new InvalidOperationException($"[Resource] 不支持的加载模式：{config.PlayMode}");
            }
        }

        static async UniTask WaitAsync(AsyncOperationBase operation, string stage, CancellationToken token)
        {
            // 等操作结束再检查取消，保证显式关闭时不与资源包初始化交错。
            await operation;
            token.ThrowIfCancellationRequested();
            if (operation.Status != EOperationStatus.Succeeded)
            {
                throw new InvalidOperationException($"[Launch] {stage}失败：{operation.Error}");
            }
        }

        /// <summary>成功后句柄归调用方持有，必须在资源不再使用时 Release。</summary>
        public async UniTask<AssetHandle> LoadAssetAsync<T>(string location, CancellationToken token = default)
            where T : UnityEngine.Object
        {
            RequirePackage();
            token.ThrowIfCancellationRequested();
            var handle = Package.LoadAssetAsync<T>(location);
            try
            {
                await handle;
                token.ThrowIfCancellationRequested();
                if (handle.Status != EOperationStatus.Succeeded)
                {
                    throw new InvalidOperationException($"[Launch] 加载失败：{location}, {handle.Error}");
                }
                return handle;
            }
            catch
            {
                handle.Release();
                throw;
            }
        }

        public async UniTask<byte[]> LoadBytesAsync(string location, CancellationToken token = default)
        {
            var handle = await LoadAssetAsync<TextAsset>(location, token);
            try
            {
                return ReadBytes(handle, location);
            }
            finally
            {
                handle.Release();
            }
        }

        /// <summary>直接供生成的 Tables 加载回调使用。</summary>
        public ByteBuf LoadConfigByte(string location) => new(LoadBytes(location));

        /// <summary>用于已下载配置的同步加载回调，返回的托管字节不依赖资源句柄。</summary>
        public byte[] LoadBytes(string location)
        {
            RequirePackage();
            var handle = Package.LoadAssetSync<TextAsset>(location);
            try
            {
                if (handle.Status != EOperationStatus.Succeeded)
                {
                    throw new InvalidOperationException($"[Resource] 加载失败：{location}, {handle.Error}");
                }
                return ReadBytes(handle, location);
            }
            finally
            {
                handle.Release();
            }
        }

        static byte[] ReadBytes(AssetHandle handle, string location)
        {
            var bytes = handle.GetAssetObject<TextAsset>()?.bytes;
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException($"[Resource] 字节资源为空：{location}");
            }
            return bytes;
        }

        /// <summary>实例与句柄一起保留；通过 ReleaseGameObjectAsync 销毁并释放。</summary>
        public async UniTask<GameObject> LoadGameObjectAsync(string location, Transform parent,
            CancellationToken token = default)
        {
            var handle = await LoadAssetAsync<GameObject>(location, token);
            GameObject instance = null;
            try
            {
                var operation = handle.InstantiateAsync(new InstantiateOptions(true, parent, false));
                await WaitAsync(operation, "实例化入口", CancellationToken.None);
                instance = operation.Result;
                if (instance == null)
                {
                    throw new InvalidOperationException($"[Resource] 实例化失败：{location}");
                }
                instances.Add(instance, handle);
                // 实例已归本服务管理，启动取消后由显式关闭销毁。
                token.ThrowIfCancellationRequested();
                return instance;
            }
            catch
            {
                if (instance == null)
                {
                    handle.Release();
                }
                throw;
            }
        }

        public async UniTask ReleaseGameObjectAsync(GameObject instance)
        {
            if (ReferenceEquals(instance, null) || !instances.TryGetValue(instance, out var handle))
            {
                throw new InvalidOperationException("[Resource] 实例不属于此资源服务或已释放。");
            }
            instances.Remove(instance);
            if (instance != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(instance);
                    await UniTask.NextFrame();
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
            handle.Release();
        }

        public async UniTask LoadHotUpdateDllAsync(GlobalConfig config, CancellationToken token = default)
        {
            RequirePackage();
            token.ThrowIfCancellationRequested();
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }
            config.Validate();
#if !UNITY_EDITOR
            foreach (var name in config.AotMetadataAssemblies)
            {
                var bytes = await LoadBytesAsync(name + ".dll", token);
                var result = HybridCLR.RuntimeApi.LoadMetadataForAOTAssembly(
                    bytes, HybridCLR.HomologousImageMode.SuperSet);
                if (result != HybridCLR.LoadImageErrorCode.OK)
                {
                    throw new InvalidOperationException($"[Resource] AOT 元数据加载失败：{name}, {result}");
                }
            }
            foreach (var name in config.HotUpdateAssemblies)
            {
                var assembly = Assembly.Load(await LoadBytesAsync(name + ".dll", token));
                if (!string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"[Resource] 程序集名称不匹配：{name}, {assembly.FullName}");
                }
            }
#else
            // Editor 使用已经编译的 Hotfix，加载 prefab 时由 Unity 调用其组件。
            await UniTask.CompletedTask;
#endif
        }

        public bool ContainsLocation(string location)
        {
            RequirePackage();
            return !string.IsNullOrWhiteSpace(location) && Package.IsLocationValid(location);
        }

        public async UniTask UnloadUnusedAssetsAsync(CancellationToken token = default)
        {
            RequirePackage();
            token.ThrowIfCancellationRequested();
            await WaitAsync(Package.UnloadUnusedAssetsAsync(new UnloadUnusedAssetsOptions(10)),
                "回收未使用资源", token);
        }

        void RequirePackage(bool requireManifest = true)
        {
            if (Package == null || Package.InitializeStatus != EOperationStatus.Succeeded ||
                (requireManifest && !Package.PackageValid))
            {
                throw new InvalidOperationException("[Resource] 请先成功初始化资源包并加载所需清单。");
            }
        }

        public async UniTask ShutdownAsync()
        {
            if (!ownsYooAssets)
            {
                return;
            }
            if (Package != null)
            {
                foreach (var instance in new List<GameObject>(instances.Keys))
                {
                    await ReleaseGameObjectAsync(instance);
                }
                await WaitAsync(Package.DestroyPackageAsync(), "销毁资源包", CancellationToken.None);
                YooAssets.RemovePackage(Package.PackageName);
                Package = null;
            }
            YooAssets.Destroy();
            ownsYooAssets = false;
            Instance = null;
        }

        sealed class RemoteService : IRemoteService
        {
            readonly string root;
            public RemoteService(string url)
            {
                root = url.TrimEnd('/') + "/";
            }

            public IReadOnlyList<string> GetRemoteUrls(string fileName) => new[] { root + fileName };
        }
    }
}
