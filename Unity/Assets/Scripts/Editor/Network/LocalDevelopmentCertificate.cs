using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UnityEditor;
using UnityEngine;

namespace GameFrame.Editor.Network
{
    /// <summary>仅为 Editor 播放配置开发证书信任，正式客户端保持平台默认 TLS 验证。</summary>
    [InitializeOnLoad]
    public static class LocalDevelopmentCertificate
    {
        private const string SetMenu = "Tools/GameFrame/Network/Set Local Development Certificate";
        private const string ClearMenu = "Tools/GameFrame/Network/Clear Local Development Certificate";
        private const string ShowMenu = "Tools/GameFrame/Network/Show Local Development Certificate";
        private static readonly string PreferencePrefix = "GameFrame.Network.DevelopmentCertificate." + Application.dataPath;
        private static object tlsSettings;
        private static PropertyInfo trustAnchors;
        private static object previousAnchors;
        private static X509Certificate2 activeCertificate;

        static LocalDevelopmentCertificate()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Restore;
            EditorApplication.quitting += Restore;
            if (EditorApplication.isPlaying)
            {
                // 播放中重新编译也会重建 Editor 域；旧域已恢复设置。
                EditorApplication.delayCall += InitializeForPlay;
            }
        }

        [MenuItem(SetMenu)]
        private static void SelectCertificate()
        {
            string path = EditorUtility.OpenFilePanel("选择 localhost 开发证书（公开 .cer / .pem，不含私钥）", "", "");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                Configure(path);
                ShowCertificate();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        /// <summary>显式选择并保存当前证书指纹；更换证书后需再次配置。</summary>
        public static void Configure(string path)
        {
            if (!CanConfigure())
            {
                throw new InvalidOperationException("请先停止播放并等待编译完成，再配置开发证书。");
            }

            using X509Certificate2 certificate = ReadCertificate(path);
            ResolveTlsSettings();
            EditorPrefs.SetString(PreferencePrefix + ".Path", path);
            EditorPrefs.SetString(PreferencePrefix + ".Fingerprint", Fingerprint(certificate));
        }

        [MenuItem(ClearMenu)]
        public static void ClearCertificate()
        {
            if (!CanConfigure())
            {
                throw new InvalidOperationException("请先停止播放，再清除开发证书配置。");
            }

            EditorPrefs.DeleteKey(PreferencePrefix + ".Path");
            EditorPrefs.DeleteKey(PreferencePrefix + ".Fingerprint");
            Debug.Log("[GameFrame] 已清除本项目的本机开发证书配置。");
        }

        [MenuItem(ShowMenu)]
        public static void ShowCertificate()
        {
            string path = EditorPrefs.GetString(PreferencePrefix + ".Path", "");
            Debug.Log(string.IsNullOrEmpty(path) ? "[GameFrame] 未配置本机开发证书，使用默认 TLS 信任。" :
                $"[GameFrame] Editor 开发证书：{path}\nSHA256：{EditorPrefs.GetString(PreferencePrefix + ".Fingerprint", "")}");
        }

        [MenuItem(SetMenu, true)]
        [MenuItem(ClearMenu, true)]
        private static bool CanConfigure() => !EditorApplication.isCompiling && !EditorApplication.isPlayingOrWillChangePlaymode;

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                try
                {
                    using X509Certificate2 certificate = ReadConfiguredCertificate();
                    if (certificate != null)
                    {
                        ResolveTlsSettings();
                    }
                }
                catch (Exception error)
                {
                    // 显式配置失效时阻止本次播放，避免初始化后才发现 TLS 配置错误。
                    EditorApplication.isPlaying = false;
                    Debug.LogException(error);
                }
            }
            else if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                Restore();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeForPlay()
        {
            if (!EditorApplication.isPlaying || activeCertificate != null)
            {
                return;
            }

            X509Certificate2 certificate = ReadConfiguredCertificate();
            if (certificate == null)
            {
                return;
            }

            try
            {
                PropertyInfo property = ResolveTlsSettings();
                object settings = property.DeclaringType.GetProperty("DefaultSettings").GetValue(null);
                object previous = property.GetValue(settings);
                var anchors = new X509CertificateCollection();
                if (previous is X509CertificateCollection existing)
                {
                    anchors.AddRange(existing);
                }

                anchors.Add(certificate);
                // 仅添加明确选择的信任锚，不修改验证回调、域名或系统验证器。
                property.SetValue(settings, anchors);
                tlsSettings = settings;
                trustAnchors = property;
                previousAnchors = previous;
                activeCertificate = certificate;
            }
            catch
            {
                certificate.Dispose();
                throw;
            }
        }

        private static X509Certificate2 ReadConfiguredCertificate()
        {
            string path = EditorPrefs.GetString(PreferencePrefix + ".Path", "");
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            X509Certificate2 certificate = ReadCertificate(path);
            if (Fingerprint(certificate) != EditorPrefs.GetString(PreferencePrefix + ".Fingerprint", ""))
            {
                certificate.Dispose();
                throw new InvalidOperationException($"开发证书文件已更换，请通过 {SetMenu} 重新选择并确认。");
            }

            return certificate;
        }

        private static X509Certificate2 ReadCertificate(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !File.Exists(path))
            {
                throw new FileNotFoundException($"开发证书路径无效，请通过 {SetMenu} 选择公开证书。", path);
            }

            var certificate = new X509Certificate2(File.ReadAllBytes(path));
            DateTime now = DateTime.UtcNow;
            if (certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime() ||
                !string.Equals(certificate.GetNameInfo(X509NameType.DnsName, false), "localhost", StringComparison.OrdinalIgnoreCase))
            {
                certificate.Dispose();
                throw new ArgumentException("请选择有效期内、DNS 名为 localhost 且不含私钥的公开开发证书。");
            }

            return certificate;
        }

        private static PropertyInfo ResolveTlsSettings()
        {
            Type type = Type.GetType("Mono.Security.Interface.MonoTlsSettings, Mono.Security", false);
            PropertyInfo property = type?.GetProperty("TrustAnchors");
            if (property == null || !property.CanWrite || type.GetProperty("DefaultSettings") == null)
            {
                throw new PlatformNotSupportedException("当前 Unity Editor 不提供 Mono TLS TrustAnchors，不能配置此开发证书。");
            }

            return property;
        }

        private static string Fingerprint(X509Certificate2 certificate)
        {
            using SHA256 hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(certificate.RawData)).Replace("-", "");
        }

        private static void Restore()
        {
            if (activeCertificate == null)
            {
                return;
            }

            trustAnchors.SetValue(tlsSettings, previousAnchors);
            activeCertificate.Dispose();
            activeCertificate = null;
            previousAnchors = null;
            trustAnchors = null;
            tlsSettings = null;
        }
    }
}
