using System.IO;
using UnityEditor;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif
#if UNITY_IOS
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
#endif

namespace GameFrame.Editor
{
    public sealed class LoginVaultBuildProcessor
#if UNITY_ANDROID
        : IPostGenerateGradleAndroidProject
#endif
    {
#if UNITY_ANDROID
        public int callbackOrder => 200;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            File.AppendAllText(Path.Combine(path, "proguard-unity.txt"),
                "\n-keep class com.zzq.gameframe.login.LoginVault { public static *; }\n");
        }
#endif
#if UNITY_IOS
        [PostProcessBuild(200)]
        private static void ConfigureIos(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
            {
                return;
            }

            string projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            project.AddFrameworkToProject(project.GetUnityFrameworkTargetGuid(), "Security.framework", false);
            project.WriteToFile(projectPath);
        }
#endif
    }
}
