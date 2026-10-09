using System;
using System.Collections.Generic;
using System.IO;
using Game.AOT;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using UnityEditor;
using UnityEngine;

namespace GameFrame.Editor
{
    /// <summary>将当前构建平台的 HybridCLR DLL 拷贝为 YooAsset 可采集的 TextAsset。</summary>
    public static class AssemblyTool
    {
        const string GlobalConfigPath = "Assets/Resources/GlobalConfig.asset";
        const string OutputDirectory = "Assets/Config/Code";

        [MenuItem("Tools/GameFrame/Loader/CopyAOTDlls", priority = 10)]
        public static void CopyAOTDlls()
        {
            CopyConfiguredDlls(false, true);
        }

        [MenuItem("Tools/GameFrame/Loader/CompileAndCopyHotUpdateDlls", priority = 11)]
        public static void CompileAndCopyHotUpdateDlls()
        {
            CopyConfiguredDlls(true, false);
        }

        [MenuItem("Tools/GameFrame/Loader/CompileAndCopyHotUpdateAndAOTDlls", priority = 12)]
        public static void CompileAndCopyHotUpdateAndAOTDlls()
        {
            CopyConfiguredDlls(true, true);
        }

        static void CopyConfiguredDlls(bool copyHotUpdate, bool copyAot)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("[AssemblyTool] 请在编辑器完成编译、资源刷新且停止播放后拷贝 DLL。");
            }

            var config = AssetDatabase.LoadAssetAtPath<GlobalConfig>(GlobalConfigPath);
            if (config == null)
            {
                throw new FileNotFoundException("[AssemblyTool] 找不到启动配置。", GlobalConfigPath);
            }

            var target = EditorUserBuildSettings.activeBuildTarget;
            var sources = new List<string>();
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (copyHotUpdate)
            {
                if (config.HotUpdateAssemblies == null || config.HotUpdateAssemblies.Length == 0)
                {
                    throw new InvalidOperationException("[AssemblyTool] GlobalConfig.HotUpdateAssemblies 不能为空。");
                }

                var registeredAssemblies = SettingsUtil.HotUpdateAssemblyNamesExcludePreserved;
                foreach (var name in config.HotUpdateAssemblies)
                {
                    if (!registeredAssemblies.Contains(name))
                    {
                        throw new InvalidOperationException($"[AssemblyTool] 热更程序集未登记到 HybridCLR Settings：{name}");
                    }
                }

                var sourceDirectory = SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target);
                AddSources(sources, assemblyNames, sourceDirectory, config.HotUpdateAssemblies);
                CompileDllCommand.CompileDllActiveBuildTargetRelease();
            }

            if (copyAot)
            {
                AddSources(sources, assemblyNames, SettingsUtil.GetAssembliesPostIl2CppStripDir(target),
                    config.AotMetadataAssemblies);
            }

            var destination = Path.Combine(SettingsUtil.ProjectDir, OutputDirectory);
            CopyDlls(sources, destination);
            AssetDatabase.Refresh();
            Debug.Log($"[AssemblyTool] DLL 拷贝完成：平台={target}，文件数={sources.Count}，目录={OutputDirectory}。" +
                (copyAot && config.AotMetadataAssemblies.Length == 0 ? " AOT 元数据列表为空，未拷贝 AOT DLL。" : string.Empty));
        }

        static void AddSources(List<string> sources, HashSet<string> assemblyNames, string sourceDirectory,
            string[] names)
        {
            if (names == null)
            {
                throw new InvalidOperationException("[AssemblyTool] GlobalConfig 的程序集列表不能为 null。");
            }

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                    name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !assemblyNames.Add(name))
                {
                    throw new InvalidOperationException($"[AssemblyTool] 程序集名为空、重复、包含路径或 .dll 后缀：{name}");
                }

                sources.Add(Path.Combine(SettingsUtil.ProjectDir, sourceDirectory, name + ".dll"));
            }
        }

        internal static void CopyDlls(IReadOnlyList<string> sources, string destination)
        {
            // 全部源文件检查完成后才写入，避免漏拷贝却报告成功。
            foreach (var source in sources)
            {
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException(
                        "[AssemblyTool] 找不到源 DLL。请先为当前平台编译热更 DLL，或生成裁剪后的 AOT DLL。", source);
                }
            }

            if (sources.Count == 0)
            {
                return;
            }

            Directory.CreateDirectory(destination);
            foreach (var source in sources)
            {
                // 覆盖字节文件，保留已有 .meta 和 GUID。
                var output = Path.Combine(destination, Path.GetFileName(source) + ".bytes");
                File.Copy(source, output, true);
                Debug.Log($"[AssemblyTool] {source} -> {output}");
            }
        }
    }
}
