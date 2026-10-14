using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Game.Hotfix;
using Game.Protocol;
using GameFrame.Network;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace GameFrame.Tests
{
    public sealed class RememberedLoginIntegrationTests
    {
        private readonly List<GameLoginSession> sessions = new List<GameLoginSession>();
        private string directory;
        private Uri endpoint;
        private RememberedLoginStore store;
        private RememberedLoginStoreTests.MemoryVault vault;
        private LoginPanel panel;
        private object tlsSettings;
        private PropertyInfo trustAnchors;
        private object previousAnchors;
        private System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            string address = Environment.GetEnvironmentVariable("GF_LOGIN_TEST_ENDPOINT");
            if (string.IsNullOrEmpty(address))
            {
                Assert.Ignore("Requires GF_LOGIN_TEST_ENDPOINT and a local WSS/PostgreSQL server.");
            }

            endpoint = new Uri(address);
#if UNITY_EDITOR
            string certificatePath = Environment.GetEnvironmentVariable("GF_LOGIN_TEST_CERTIFICATE");
            if (!string.IsNullOrEmpty(certificatePath))
            {
                Type settingsType = Type.GetType("Mono.Security.Interface.MonoTlsSettings, Mono.Security", true);
                tlsSettings = settingsType.GetProperty("DefaultSettings").GetValue(null);
                trustAnchors = settingsType.GetProperty("TrustAnchors");
                previousAnchors = trustAnchors.GetValue(tlsSettings);
                certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificatePath);
                trustAnchors.SetValue(tlsSettings,
                    new System.Security.Cryptography.X509Certificates.X509CertificateCollection { certificate });
            }
#endif
            directory = Path.Combine(Path.GetTempPath(), "gameframe-resume-" + Guid.NewGuid().ToString("N"));
            vault = new RememberedLoginStoreTests.MemoryVault();
            store = ReopenStore();
            await GameRuntime.InitAsync(new GameRuntimeConfig
            {
                EnableTimer = false,
                EnableUI = false,
                NetworkOptions = new NetworkRuntimeOptions()
            });
            await UniTask.Delay(1100, ignoreTimeScale: true);
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            if (panel != null)
            {
                panel.DispatchDestroy();
                UnityEngine.Object.Destroy(panel.gameObject);
            }

            foreach (GameLoginSession session in sessions)
            {
                await session.DisposeAsync();
            }

            sessions.Clear();
            await GameRuntime.ShutdownAsync();
            if (tlsSettings != null)
            {
                trustAnchors.SetValue(tlsSettings, previousAnchors);
                certificate.Dispose();
            }

            if (directory != null && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        });

        private RememberedLoginStore ReopenStore() => new RememberedLoginStore(
            Path.Combine(directory, "login.sqlite"), endpoint, vault);

        private GameLoginSession CreateSession()
        {
            var session = new GameLoginSession(GameRuntime.Network, endpoint, rememberedLogin: store);
            sessions.Add(session);
            return session;
        }

        private static string Account() => "resume_" + Guid.NewGuid().ToString("N");

#if UNITY_EDITOR
        private async UniTask OpenPanelAsync(GameLoginSession session, Func<UniTask> loggedIn)
        {
            if (panel == null)
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/HitfixPrefab/LoginPanel.prefab");
                panel = UnityEngine.Object.Instantiate(prefab).GetComponent<LoginPanel>();
                panel.DispatchCreate();
            }

            panel.ApplyArgs(new LoginPanelArgs(session, loggedIn));
            panel.DispatchOpen();
            await UniTask.WaitUntil(() => Control<Button>("confirmLoginButton").interactable)
                .Timeout(TimeSpan.FromSeconds(10));
        }

        private T Control<T>(string field) => (T)typeof(LoginPanel)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);

        [UnityTest]
        public IEnumerator ReopenedClientClicksLoginWithoutPasswordAndRotatesCredentials() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession original = CreateSession();
            string account = Account();
            Assert.AreEqual(ResultCode.Success, await original.LoginAsync(account, "test-password"));
            ulong id = original.CurrentUser.AccountId;
            ulong epoch = original.CurrentUser.AuthEpoch;
            Assert.IsTrue((await store.ReadAsync()).CanResume);
            await original.DisposeAsync();
            store = ReopenStore();
            GameLoginSession resumed = CreateSession();
            int entered = 0;
            await OpenPanelAsync(resumed, () =>
            {
                entered++;
                panel.DispatchClose();
                return UniTask.CompletedTask;
            });
            Assert.AreEqual(account, Control<TMP_InputField>("accountInput").text);
            Assert.IsFalse(Control<TMP_InputField>("accountInput").interactable);
            Assert.IsFalse(Control<TMP_InputField>("passwordInput").gameObject.activeSelf);
            Assert.IsFalse(resumed.IsAuthenticated, "Reading saved metadata must not connect automatically.");
            Control<Button>("confirmLoginButton").onClick.Invoke();
            Control<Button>("confirmLoginButton").onClick.Invoke();
            await UniTask.WaitUntil(() => entered == 1).Timeout(TimeSpan.FromSeconds(10));
            Assert.AreEqual(id, resumed.CurrentUser.AccountId);
            Assert.AreEqual(epoch, resumed.CurrentUser.AuthEpoch, "Resume rotates the token while preserving the account session epoch.");
            Assert.IsTrue((await store.ReadAsync()).CanResume);
            await resumed.DisposeAsync();
            store = ReopenStore();
            GameLoginSession rotated = CreateSession();
            Assert.AreEqual(ResultCode.Success, await rotated.ResumeLoginAsync());
            Assert.AreEqual(id, rotated.CurrentUser.AccountId);
            Assert.AreEqual(ResultCode.Success, await rotated.LogoutAsync());
            Assert.IsNull(await store.ReadAsync(), "Explicit logout clears the remembered account and secret.");
            Assert.IsFalse(rotated.IsBusy);
        });

        [UnityTest]
        public IEnumerator RejectedResumeKeepsAccountAndRequiresExplicitPasswordLogin() => UniTask.ToCoroutine(async () =>
        {
            string account = Account();
            GameLoginSession original = CreateSession();
            Assert.AreEqual(ResultCode.Success, await original.LoginAsync(account, "test-password"));
            ulong id = original.CurrentUser.AccountId;
            await original.DisposeAsync();
            await store.SaveAsync(account, id, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                new byte[16], new byte[32]);
            GameLoginSession session = CreateSession();
            int entered = 0;
            await OpenPanelAsync(session, () =>
            {
                entered++;
                return UniTask.CompletedTask;
            });
            Control<Button>("confirmLoginButton").onClick.Invoke();
            await UniTask.WaitUntil(() => Control<Button>("confirmLoginButton").interactable)
                .Timeout(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, entered);
            Assert.IsNull(session.CurrentUser);
            Assert.AreEqual(account, Control<TMP_InputField>("accountInput").text);
            Assert.IsTrue(Control<TMP_InputField>("passwordInput").gameObject.activeSelf);
            Assert.IsTrue(Control<TMP_InputField>("passwordInput").interactable);
            Assert.IsFalse((await store.ReadAsync()).CanResume);
            Control<TMP_InputField>("passwordInput").text = "test-password";
            Control<Button>("confirmLoginButton").onClick.Invoke();
            await UniTask.WaitUntil(() => entered == 1).Timeout(TimeSpan.FromSeconds(10));
            Assert.AreEqual(id, session.CurrentUser.AccountId);
            Assert.IsTrue((await store.ReadAsync()).CanResume);
        });

        [UnityTest]
        public IEnumerator SwitchAccountRemovesSavedCredentialsAndRestoresEmptyInputs() => UniTask.ToCoroutine(async () =>
        {
            await store.SaveAsync("saved-player", 42, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                new byte[16], new byte[32]);
            GameLoginSession session = CreateSession();
            await OpenPanelAsync(session, () => UniTask.CompletedTask);
            Control<Button>("switchAccountButton").onClick.Invoke();
            await UniTask.WaitUntil(() => Control<Button>("confirmLoginButton").interactable)
                .Timeout(TimeSpan.FromSeconds(10));
            Assert.AreEqual(string.Empty, Control<TMP_InputField>("accountInput").text);
            Assert.AreEqual(string.Empty, Control<TMP_InputField>("passwordInput").text);
            Assert.IsTrue(Control<TMP_InputField>("passwordInput").gameObject.activeSelf);
            Assert.IsTrue(Control<TMP_InputField>("accountInput").interactable);
            Assert.IsNull(await store.ReadAsync());
            panel.DispatchClose();
            await OpenPanelAsync(session, () => UniTask.CompletedTask);
            Assert.IsTrue(Control<TMP_InputField>("passwordInput").gameObject.activeSelf);
            Assert.IsNull(session.CurrentUser);
        });
#endif
    }
}
