using Cysharp.Threading.Tasks;
using UnityEngine;

namespace GameFrame.AOT
{
    /// <summary>首场景入口：准备资源与 Hotfix DLL，然后创建游戏入口。</summary>
    [DisallowMultipleComponent]
    public sealed class Launch : MonoBehaviour
    {
        [SerializeField] GlobalConfig globalConfig;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            StartAsync().Forget();
        }

        async UniTask StartAsync()
        {
            var token = this.GetCancellationTokenOnDestroy();
            var resources = new ResourceLoadManager();
            await resources.InitializeAsync(globalConfig, token);
            await resources.UpdatePackageManifestAsync(token);
            await resources.DownloadByTagAsync(globalConfig.BootTag, token);
            await resources.LoadHotUpdateDllAsync(globalConfig, token);
            await resources.LoadGameObjectAsync(globalConfig.GameEntryLocation, transform, token);
            Debug.Log($"[Launch] 资源准备完成，资源版本：{resources.Package.GetPackageVersion()}");
        }
    }
}
