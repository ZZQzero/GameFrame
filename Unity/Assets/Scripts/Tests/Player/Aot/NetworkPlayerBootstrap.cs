#if UNITY_EDITOR || DEVELOPMENT_BUILD || GAMEFRAME_NETWORK_PERFORMANCE
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using HybridCLR;
using UnityEngine;

namespace Game.AOT.Validation
{
    // Only the generated verification scene owns this component. Launch is unchanged.
    public sealed class NetworkPlayerBootstrap : MonoBehaviour
    {
        private async void Start()
        {
            string directory = null;
            try
            {
#if !ENABLE_IL2CPP || UNITY_EDITOR
                throw new PlatformNotSupportedException("Run the standalone IL2CPP verification Player.");
#else
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "--gf-network-host");
                if (index < 0 || index + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing verification host directory.");
                }

                directory = Path.GetFullPath(args[index + 1]);
                Application.runInBackground = true;
                string payload = Path.Combine(Application.streamingAssetsPath, "NetworkValidation");
                foreach (string name in File.ReadAllLines(Path.Combine(payload, "metadata.txt")))
                {
                    LoadImageErrorCode result = RuntimeApi.LoadMetadataForAOTAssembly(
                        File.ReadAllBytes(Path.Combine(payload, name + ".bytes")), HomologousImageMode.SuperSet);
                    if (result != LoadImageErrorCode.OK)
                    {
                        throw new InvalidOperationException("AOT metadata load failed: " + name + ": " + result);
                    }
                }

                Assembly hotfix = Assembly.Load(File.ReadAllBytes(Path.Combine(payload, "Game.Hotfix.dll.bytes")));
#if GAMEFRAME_NETWORK_PERFORMANCE
                const string probe = "Game.Hotfix.Validation.NetworkPerformanceProbe";
#else
                const string probe = "Game.Hotfix.Validation.NetworkPlayerProbe";
#endif
                MethodInfo entry = hotfix.GetType(probe, true)
                    .GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static);
                await (Task)entry.Invoke(null, new object[] { directory });
                Debug.Log("[NetworkPlayerValidation] PASS: native IL2CPP + HybridCLR public-client verification.");
                Application.Quit(0);
#endif
            }
            catch (Exception error)
            {
                if (directory != null)
                {
                    string abi = error.Message.StartsWith("Native ABI ", StringComparison.Ordinal) ? error.Message + "\n" : "";
                    File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString() + "\n" + abi);
                }

                Debug.LogError("[NetworkPlayerValidation] FAIL: " + error.GetType().FullName);
                Application.Quit(1);
            }

            await UniTask.CompletedTask;
        }
    }
}
#endif
