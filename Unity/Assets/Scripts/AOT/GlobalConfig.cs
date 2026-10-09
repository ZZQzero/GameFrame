using System;
using UnityEngine;
using YooAsset;

namespace GameFrame.AOT
{
    /// <summary>随安装包提供的启动配置，在资源包初始化前可用。</summary>
    [CreateAssetMenu(menuName = "GameFrame/Global Config", fileName = "GlobalConfig")]
    public sealed class GlobalConfig : ScriptableObject
    {
        [Header("资源")]
        public EPlayMode PlayMode = EPlayMode.EditorSimulateMode;
        public string PackageName = "DefaultPackage";
        [Tooltip("当前平台、当前资源包的完整 HTTP(S) 目录地址。")]
        public string ServerUrl;
        public string BootTag = "boot";
        public string GameEntryLocation = "GameEntry";

        [Header("HybridCLR")]
        [Tooltip("不含 .dll 的程序集名，按被依赖者优先的顺序加载。")]
        public string[] HotUpdateAssemblies = { "GameFrame.Hotfix" };
        [Tooltip("对应平台裁剪后的 AOT 程序集名，不含 .dll；资源地址为 名称.dll。")]
        public string[] AotMetadataAssemblies = Array.Empty<string>();

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(PackageName) || string.IsNullOrWhiteSpace(BootTag) ||
                string.IsNullOrWhiteSpace(GameEntryLocation))
            {
                throw new InvalidOperationException("[GlobalConfig] 包名、启动标签和入口地址不能为空。");
            }

            switch (PlayMode)
            {
                case EPlayMode.EditorSimulateMode:
#if !UNITY_EDITOR
                    throw new InvalidOperationException("[GlobalConfig] EditorSimulateMode 仅限 Unity Editor。");
#else
                    break;
#endif
                case EPlayMode.OfflinePlayMode:
#if UNITY_WEBGL && !UNITY_EDITOR
                    throw new InvalidOperationException("[GlobalConfig] WebGL 请使用 WebPlayMode。");
#else
                    break;
#endif
                case EPlayMode.HostPlayMode:
#if UNITY_WEBGL && !UNITY_EDITOR
                    throw new InvalidOperationException("[GlobalConfig] WebGL 请使用 WebPlayMode。");
#else
                    ValidateServerUrl();
                    break;
#endif
                case EPlayMode.WebPlayMode:
#if !UNITY_WEBGL && !UNITY_EDITOR
                    throw new InvalidOperationException("[GlobalConfig] WebPlayMode 仅限 WebGL。");
#else
                    ValidateServerUrl();
                    break;
#endif
                default:
                    throw new InvalidOperationException($"[GlobalConfig] 不支持的加载模式：{PlayMode}");
            }

            if (HotUpdateAssemblies == null || HotUpdateAssemblies.Length == 0)
            {
                throw new InvalidOperationException("[GlobalConfig] 热更程序集列表不能为空。");
            }
            ValidateAssemblyNames(HotUpdateAssemblies);
            if (AotMetadataAssemblies == null)
            {
                throw new InvalidOperationException("[GlobalConfig] AOT 元数据列表不能为 null。");
            }
            ValidateAssemblyNames(AotMetadataAssemblies);
        }

        void ValidateServerUrl()
        {
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("[GlobalConfig] 联机模式需要有效的 HTTP(S) 服务器地址。");
            }
        }

        static void ValidateAssemblyNames(string[] names)
        {
            var unique = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    !unique.Add(name))
                {
                    throw new InvalidOperationException($"[GlobalConfig] 程序集名为空、重复或包含 .dll：{name}");
                }
            }
        }
    }
}
