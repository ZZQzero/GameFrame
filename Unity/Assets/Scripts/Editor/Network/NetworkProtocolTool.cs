using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameFrame.Editor.Network
{
    public static class NetworkProtocolTool
    {
        private const string GenerateMenu = "Tools/GameFrame/Network/Generate Messages";
        private const string CheckMenu = "Tools/GameFrame/Network/Check Messages";
        private const string DotnetMenu = "Tools/GameFrame/Network/Set .NET Executable";
        private const string DotnetPreference = "GameFrame.NetworkProtocol.DotnetExecutable";
        private static bool running;

        [MenuItem(GenerateMenu)]
        public static async void GenerateMessages()
        {
            await ExecuteAsync(false);
        }

        [MenuItem(CheckMenu)]
        public static async void CheckMessages()
        {
            await ExecuteAsync(true);
        }

        [MenuItem(GenerateMenu, true)]
        [MenuItem(CheckMenu, true)]
        [MenuItem(DotnetMenu, true)]
        private static bool CanRun()
        {
            return !running && !EditorApplication.isCompiling && !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        [MenuItem(DotnetMenu)]
        public static void SetDotnetExecutable()
        {
            string path = EditorUtility.OpenFilePanel("选择 dotnet 可执行文件（需要 .NET SDK 10.0.401）", "", "");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            EditorPrefs.SetString(DotnetPreference, path);
            Debug.Log($"[GameFrame] 网络协议生成器 .NET 路径：{path}");
        }

        private static async Task ExecuteAsync(bool checkOnly)
        {
            if (!CanRun())
            {
                Debug.LogError("[GameFrame] 网络协议工具需要处于编辑模式，且没有正在进行的生成、检查或脚本编译。");
                return;
            }

            running = true;
            // 生成文件会触发脚本导入；进程退出并收集结果后才允许刷新与域重载。
            EditorApplication.LockReloadAssemblies();
            AssetDatabase.DisallowAutoRefresh();
            bool succeeded = false;
            try
            {
                string repositoryRoot = Directory.GetParent(Application.dataPath).Parent.FullName;
                string executable = ResolveDotnetExecutable();
                Debug.Log($"[GameFrame] 网络协议{(checkOnly ? "检查" : "生成")}开始，.NET：{executable}");
                string output = await RunGeneratorAsync(repositoryRoot, checkOnly, executable);
                succeeded = true;
                Debug.Log($"[GameFrame] 网络协议{(checkOnly ? "检查" : "生成")}成功。\n{output}");
            }
            catch (Exception exception)
            {
                // 菜单没有等待方，将进程退出码及两条输出管道的错误交给 Console。
                Debug.LogException(exception);
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
                EditorApplication.UnlockReloadAssemblies();
                running = false;
            }

            if (succeeded && !checkOnly)
            {
                AssetDatabase.Refresh();
            }
        }

        internal static async Task<string> RunGeneratorAsync(string repositoryRoot, bool checkOnly, string executable)
        {
            string dotnetRoot = Path.Combine(repositoryRoot, "DotNet");
            string project = Path.Combine(dotnetRoot, "NetworkProtocol", "NetworkProtocol.csproj");
            if (!File.Exists(project))
            {
                throw new FileNotFoundException("找不到网络协议生成器工程。", project);
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    WorkingDirectory = dotnetRoot,
                    FileName = executable,
                    Arguments = "run --project NetworkProtocol/NetworkProtocol.csproj --no-launch-profile -- " +
                        (checkOnly ? "check" : "generate") + " --artifacts",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动网络协议生成器：{executable}");
            }

            // 同时读取两个管道，避免大量编译诊断填满管道而使进程互锁。
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit());
            string stdout = await output;
            string stderr = await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"网络协议{(checkOnly ? "检查" : "生成")}失败：.NET={executable}, ExitCode={process.ExitCode}\n" +
                    $"需要 DotNet/global.json 指定的 .NET SDK。\nstdout:\n{stdout}\nstderr:\n{stderr}");
            }

            return stdout + stderr;
        }

        internal static string ResolveDotnetExecutable()
        {
            string configured = EditorPrefs.GetString(DotnetPreference, "");
            if (!string.IsNullOrEmpty(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(configured))
                {
                    throw new FileNotFoundException($"配置的 .NET 路径无效，请通过 {DotnetMenu} 重新选择。", configured);
                }

                return configured;
            }

#if UNITY_EDITOR_WIN
            const string executableName = "dotnet.exe";
#else
            const string executableName = "dotnet";
#endif
            var directories = new List<string>();
            string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                directories.Add(dotnetRoot);
            }

            string searchPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            directories.AddRange(searchPath.Split(Path.PathSeparator));
#if UNITY_EDITOR_WIN
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
#elif UNITY_EDITOR_OSX
            directories.Add("/usr/local/share/dotnet");
            directories.Add("/opt/homebrew/bin");
            directories.Add("/usr/local/bin");
#elif UNITY_EDITOR_LINUX
            directories.Add("/usr/share/dotnet");
            directories.Add("/usr/lib/dotnet");
            directories.Add("/usr/local/bin");
#endif
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
            foreach (string directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                {
                    continue;
                }

                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"找不到 .NET，请安装 SDK 10.0.401，或通过 {DotnetMenu} 指定 dotnet 路径。");
        }
    }
}
