using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.AOT.Validation;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameFrame.Editor
{
    public static class NetworkPlayerValidationBuild
    {
        private static bool scheduled;

        [MenuItem("Tools/GameFrame/Network/Build macOS IL2CPP Validation Player")]
        private static void BuildFromMenu()
        {
            string output = EditorUtility.SaveFolderPanel("Network IL2CPP validation output", Path.GetTempPath(), "");
            if (!string.IsNullOrEmpty(output))
            {
                ScheduleBuild(output);
            }
        }

        // Scheduling lets MCP return before Unity's synchronous native build starts.
        public static void ScheduleBuild(string output)
        {
            if (scheduled || BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                throw new InvalidOperationException("Editor is busy.");
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX ||
                PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone) != ScriptingImplementation.IL2CPP)
            {
                throw new InvalidOperationException("Select StandaloneOSX and IL2CPP before verification.");
            }

            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            if (Directory.EnumerateFileSystemEntries(output).Any())
            {
                throw new IOException("Choose an empty output directory.");
            }

            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                if (SceneManager.GetSceneAt(index).isDirty)
                {
                    throw new InvalidOperationException("Save scene edits before building the verification Player.");
                }
            }

            scheduled = true;
            EditorApplication.CallbackFunction run = null;
            run = () =>
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    return;
                }

                EditorApplication.update -= run;
                Build(output);
            };
            EditorApplication.update += run;
        }

        private static void Build(string output)
        {
            SceneSetup[] scenes = EditorSceneManager.GetSceneManagerSetup();
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;
            bool development = EditorUserBuildSettings.development;
            string buildLocation = EditorUserBuildSettings.GetBuildLocation(BuildTarget.StandaloneOSX);
            string il2cppPath = Environment.GetEnvironmentVariable("UNITY_IL2CPP_PATH");
            UnityEngine.Object[] preloaded = PlayerSettings.GetPreloadedAssets();
            byte[] playerSettingsFile = File.ReadAllBytes("ProjectSettings/ProjectSettings.asset");
            byte[] buildSettingsFile = File.ReadAllBytes("ProjectSettings/EditorBuildSettings.asset");
            string scenePath = "Assets/NetworkValidation-" + Guid.NewGuid().ToString("N") + ".unity";
            try
            {
                File.WriteAllText(Path.Combine(output, "building.txt"), "Generating HybridCLR and building IL2CPP.");
                UnityEngine.SceneManagement.Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(scene);
                new GameObject("Network Player Validation").AddComponent<NetworkPlayerBootstrap>();
                new GameObject("Main Camera").AddComponent<Camera>();
                new GameObject("Directional Light").AddComponent<Light>().type = LightType.Directional;
                EditorSceneManager.SaveScene(scene, scenePath);
                EditorSceneManager.RestoreSceneManagerSetup(scenes);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
                EditorUserBuildSettings.development = true;
                PrebuildCommand.GenerateAll();
                string app = Path.Combine(output, "NetworkValidation.app");
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath }, locationPathName = app, target = BuildTarget.StandaloneOSX,
                    options = BuildOptions.Development | BuildOptions.StrictMode
                });
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new BuildFailedException("IL2CPP verification build failed: " + report.summary.result);
                }

                // Use the final build's stripped assemblies, never DLLs from another platform/build.
                string payload = Path.Combine(app, "Contents/Resources/Data/StreamingAssets/NetworkValidation");
                Directory.CreateDirectory(payload);
                File.Copy(Path.Combine(SettingsUtil.GetHotUpdateDllsOutputDirByTarget(BuildTarget.StandaloneOSX), "Game.Hotfix.dll"),
                    Path.Combine(payload, "Game.Hotfix.dll.bytes"));
                string generated = File.ReadAllText(Path.Combine(Application.dataPath,
                    SettingsUtil.HybridCLRSettings.outputAOTGenericReferenceFile));
                string assemblyList = generated.Substring(0, generated.IndexOf("// }}", StringComparison.Ordinal));
                string[] metadata = Regex.Matches(assemblyList, "\"([^\"]+\\.dll)\"")
                    .Cast<Match>().Select(match => match.Groups[1].Value).ToArray();
                if (metadata.Length == 0)
                {
                    throw new BuildFailedException("HybridCLR did not produce an AOT metadata inventory.");
                }

                foreach (string name in metadata)
                {
                    File.Copy(Path.Combine(SettingsUtil.GetAssembliesPostIl2CppStripDir(BuildTarget.StandaloneOSX), name),
                        Path.Combine(payload, name + ".bytes"));
                }

                File.WriteAllLines(Path.Combine(payload, "metadata.txt"), metadata);
                SignPlayer(app);
                File.WriteAllText(Path.Combine(output, "build-succeeded.txt"),
                    "StandaloneOSX IL2CPP Development / HybridCLR enabled\n" +
                    "Stripping=" + PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Standalone) + "\n" +
                    "Architecture=" + UnityEditor.OSXStandalone.UserBuildSettings.architecture + "\n" +
                    "AOT metadata=" + metadata.Length + "\n" +
                    "Build warnings=" + report.summary.totalWarnings + "\nBuild errors=" + report.summary.totalErrors);
                Debug.Log("[NetworkPlayerValidation] Build complete: " + app);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "build-failed.txt"), error.ToString());
                Debug.LogException(error);
            }
            finally
            {
                EditorBuildSettings.scenes = buildScenes;
                EditorUserBuildSettings.development = development;
                EditorUserBuildSettings.SetBuildLocation(BuildTarget.StandaloneOSX, buildLocation);
                Environment.SetEnvironmentVariable("UNITY_IL2CPP_PATH", il2cppPath);
                PlayerSettings.SetPreloadedAssets(preloaded);
                EditorSceneManager.RestoreSceneManagerSetup(scenes);
                AssetDatabase.DeleteAsset(scenePath);
                File.WriteAllBytes("ProjectSettings/ProjectSettings.asset", playerSettingsFile);
                File.WriteAllBytes("ProjectSettings/EditorBuildSettings.asset", buildSettingsFile);
                scheduled = false;
            }
        }

        private static void SignPlayer(string app)
        {
            // Adding the Hotfix/metadata payload changes sealed resources. Re-sign this local test app.
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/codesign", UseShellExecute = false, RedirectStandardError = true,
                Arguments = "--force --deep --sign - \"" + app.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            };
            using (var process = System.Diagnostics.Process.Start(start))
            {
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000))
                {
                    process.Kill();
                    process.WaitForExit();
                    throw new BuildFailedException("Local verification Player signing timed out.");
                }

                string diagnostic = error.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    throw new BuildFailedException("Local verification Player signing failed: " + diagnostic);
                }
            }
        }
    }
}
