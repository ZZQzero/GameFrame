using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
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

        [UnityTest]
        public IEnumerator ResumeSaveFailureKeepsAuthenticationAndCannotReuseOldToken() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession original = CreateSession();
            Assert.AreEqual(ResultCode.Success, (await original.LoginAsync(Account(), "test-password")).ResultCode);
            ulong id = original.CurrentUser.AccountId;
            await original.DisposeAsync();
            vault.FailWrite = true;
            GameLoginSession resumed = CreateSession();
            GameLoginResult result = await resumed.ResumeLoginAsync();
            Assert.AreEqual(ResultCode.Success, result.ResultCode);
            Assert.IsInstanceOf<IOException>(result.RememberedLoginError);
            Assert.AreEqual(id, resumed.CurrentUser.AccountId);
            Assert.IsFalse((await store.ReadAsync()).CanResume);
            EchoResponse echo = await resumed.LobbyClient.RequestAsync(GameNetRoutes.Echo,
                new EchoRequest { Text = "authenticated-without-persistence" }, TimeSpan.FromSeconds(5));
            Assert.AreEqual("authenticated-without-persistence", echo.Text);
        });

        [UnityTest]
        public IEnumerator DatabaseSaveFailureIsReportedSeparatelyFromAuthentication() => UniTask.ToCoroutine(async () =>
        {
            Directory.CreateDirectory(Path.Combine(directory, "login.sqlite"));
            GameLoginSession session = CreateSession();
            GameLoginResult result = await session.LoginAsync(Account(), "test-password");
            Assert.AreEqual(ResultCode.Success, result.ResultCode);
            Assert.IsNotNull(result.RememberedLoginError);
            Assert.IsTrue(session.IsAuthenticated);
            Directory.Delete(Path.Combine(directory, "login.sqlite"), true);
            Assert.IsNull(await store.ReadAsync(), "Failed SQLite writes must not publish a remembered account.");
        });

        [UnityTest]
        public IEnumerator LocalDeleteFailureStillRevokesRemoteSession() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            Assert.AreEqual(ResultCode.Success, (await session.LoginAsync(Account(), "test-password")).ResultCode);
            int lost = 0;
            session.AuthenticationLost += () => lost++;
            vault.FailDelete = true;
            LogAssert.Expect(LogType.Warning, "远端已注销，但本地账号清除失败。");
            Exception failure = null;
            try
            {
                await session.LogoutAsync();
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOf<IOException>(failure);
            Assert.AreSame(vault.DeleteError, failure, "Cleanup must preserve the original exception.");
            Assert.IsFalse(session.IsAuthenticated);
            Assert.IsFalse(session.IsBusy);
            Assert.AreEqual(1, lost);
            vault.FailDelete = false;
            Assert.IsTrue((await store.ReadAsync()).CanResume, "The injected vault failure leaves the old local credential.");
            GameLoginSession resumed = CreateSession();
            Assert.AreEqual(ResultCode.InvalidCredentials, (await resumed.ResumeLoginAsync()).ResultCode,
                "The old credential must have been revoked on the server despite local deletion failing.");
        });

        [UnityTest]
        public IEnumerator LogoutWithLocalAndRemoteFailuresPreservesTheFirstError() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            Assert.AreEqual(ResultCode.Success, (await session.LoginAsync(Account(), "test-password")).ResultCode);
            vault.FailDelete = true;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            // Unity's log condition starts with the innermost exception; the context is in its stack trace.
            LogAssert.Expect(LogType.Exception, new Regex("^OperationCanceledException: The operation was canceled\\."));
            Exception failure = null;
            try
            {
                await session.LogoutAsync(cancellation.Token);
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.AreSame(vault.DeleteError, failure);
            Assert.IsFalse(session.IsAuthenticated);
            Assert.IsFalse(session.IsBusy);
            Assert.IsTrue((await store.ReadAsync()).CanResume);
        });

        [UnityTest]
        public IEnumerator CanceledRemoteLogoutStillClearsLocalCredential() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            Assert.AreEqual(ResultCode.Success, (await session.LoginAsync(Account(), "test-password")).ResultCode);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool canceled = await session.LogoutAsync(cancellation.Token).SuppressCancellationThrow()
                .ContinueWith(result => result.IsCanceled);
            Assert.IsTrue(canceled);
            Assert.IsNull(await store.ReadAsync());
            Assert.IsFalse(session.IsAuthenticated);
            Assert.IsFalse(session.IsBusy);
        });

        [UnityTest]
        public IEnumerator NormalConnectionCloseKeepsResumeQualification() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            Assert.AreEqual(ResultCode.Success, (await session.LoginAsync(Account(), "test-password")).ResultCode);
            await session.LobbyClient.CloseAsync();
            Assert.IsFalse(session.IsAuthenticated);
            Assert.IsTrue((await store.ReadAsync()).CanResume);
        });

        [UnityTest]
        public IEnumerator RevocationWhileSavingResponseCannotPublishResumeQualification() => UniTask.ToCoroutine(async () =>
        {
            string account = Account();
            GameLoginSession original = CreateSession();
            var client = (NetworkClient)typeof(GameLoginSession).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(original);
            var gate = (SemaphoreSlim)typeof(RememberedLoginStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(store);
            await gate.WaitAsync();
            var login = original.LoginAsync(account, "test-password").Preserve();
            var replacement = new GameLoginSession(GameRuntime.Network, endpoint);
            sessions.Add(replacement);
            try
            {
                await UniTask.WaitUntil(() => client.GetMetrics().Rpc.SuccessfulResponses == 1)
                    .Timeout(TimeSpan.FromSeconds(10));
                Assert.AreEqual(ResultCode.Success, (await replacement.LoginAsync(account, "test-password")).ResultCode);
                await UniTask.WaitUntil(() => client.State != NetworkClientState.Connected)
                    .Timeout(TimeSpan.FromSeconds(10));
            }
            finally
            {
                gate.Release();
            }

            Exception failure = null;
            try
            {
                await login;
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOf<NetworkException>(failure);
            Assert.IsFalse(original.IsAuthenticated);
            Assert.IsFalse(original.IsBusy);
            RememberedAccount saved = await store.ReadAsync();
            Assert.AreEqual(account, saved.AccountName);
            Assert.IsFalse(saved.CanResume);
        });

        [UnityTest]
        public IEnumerator ReplacedSessionClearsOnlyItsCredentialBeforeNotifying() => UniTask.ToCoroutine(async () =>
        {
            string account = Account();
            GameLoginSession original = CreateSession();
            Assert.AreEqual(ResultCode.Success, (await original.LoginAsync(account, "test-password")).ResultCode);
            // A second device has its own credential store.
            var otherStore = new RememberedLoginStore(Path.Combine(directory, "other.sqlite"), endpoint,
                new RememberedLoginStoreTests.MemoryVault());
            var replacement = new GameLoginSession(GameRuntime.Network, endpoint, rememberedLogin: otherStore);
            sessions.Add(replacement);
            int lost = 0;
            bool busyAtNotification = true;
            original.AuthenticationLost += () =>
            {
                lost++;
                busyAtNotification = original.IsBusy;
            };
            var gate = (SemaphoreSlim)typeof(RememberedLoginStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(store);
            await gate.WaitAsync();
            var reading = original.ReadRememberedAccountAsync().Preserve();
            try
            {
                Assert.AreEqual(ResultCode.Success, (await replacement.LoginAsync(account, "test-password")).ResultCode);
                await UniTask.WaitUntil(() => !original.IsAuthenticated).Timeout(TimeSpan.FromSeconds(10));
                Assert.IsTrue(original.IsBusy);
                Assert.AreEqual(0, lost, "Navigation waits for the pending read and credential cleanup.");
            }
            finally
            {
                gate.Release();
            }

            await reading;
            await UniTask.WaitUntil(() => lost == 1).Timeout(TimeSpan.FromSeconds(10));
            Assert.IsFalse(busyAtNotification);
            RememberedAccount saved = await store.ReadAsync();
            Assert.AreEqual(account, saved.AccountName);
            Assert.IsFalse(saved.CanResume);
            Assert.IsTrue((await otherStore.ReadAsync()).CanResume);
        });

        [UnityTest]
        public IEnumerator LateReplacementClosePreservesNewCredentialInSharedStore() => UniTask.ToCoroutine(async () =>
        {
            string account = Account();
            GameLoginSession original = CreateSession();
            Assert.AreEqual(ResultCode.Success, (await original.LoginAsync(account, "test-password")).ResultCode);
            int lost = 0;
            original.AuthenticationLost += () => lost++;
            // Hold the store gate so both authentication persistence and the old close race for it.
            var gate = (SemaphoreSlim)typeof(RememberedLoginStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(store);
            await gate.WaitAsync();
            GameLoginSession replacement = CreateSession();
            var login = replacement.LoginAsync(account, "test-password").Preserve();
            try
            {
                await UniTask.WaitUntil(() => !original.IsAuthenticated).Timeout(TimeSpan.FromSeconds(10));
                Assert.IsTrue(original.IsBusy, "Credential invalidation remains part of the serialized operation.");
            }
            finally
            {
                gate.Release();
            }

            Assert.AreEqual(ResultCode.Success, (await login).ResultCode);
            await UniTask.WaitUntil(() => lost == 1).Timeout(TimeSpan.FromSeconds(10));
            Assert.IsTrue((await store.ReadAsync()).CanResume);
            await replacement.DisposeAsync();
            GameLoginSession resumed = CreateSession();
            Assert.AreEqual(ResultCode.Success, (await resumed.ResumeLoginAsync()).ResultCode);
        });

#if UNITY_EDITOR
        private async UniTask OpenPanelAsync(GameLoginSession session, Func<UniTask> loggedIn, Action<string> notice = null)
        {
            if (panel == null)
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/HitfixPrefab/LoginPanel.prefab");
                panel = UnityEngine.Object.Instantiate(prefab).GetComponent<LoginPanel>();
                panel.DispatchCreate();
            }

            panel.ApplyArgs(new LoginPanelArgs(session, message =>
            {
                notice?.Invoke(message);
                return loggedIn();
            }));
            panel.DispatchOpen();
            await UniTask.WaitUntil(() => Control<Button>("confirmLoginButton").interactable)
                .Timeout(TimeSpan.FromSeconds(10));
        }

        private T Control<T>(string field) => (T)typeof(LoginPanel)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);

        [UnityTest]
        public IEnumerator PasswordSaveFailureEntersImmediatelyAndPassesNotice() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            int entered = 0;
            string notice = null;
            await OpenPanelAsync(session, () =>
            {
                entered++;
                return UniTask.CompletedTask;
            }, message => notice = message);
            vault.FailWrite = true;
            Control<TMP_InputField>("accountInput").text = Account();
            Control<TMP_InputField>("passwordInput").text = "test-password";
            LogAssert.Expect(LogType.Exception, new Regex("IOException: test-vault-write-failure"));
            Control<Button>("confirmLoginButton").onClick.Invoke();
            await UniTask.WaitUntil(() => Control<Button>("confirmLoginButton").interactable)
                .Timeout(TimeSpan.FromSeconds(10));
            Assert.IsTrue(session.IsAuthenticated);
            Assert.AreEqual(1, entered, "Remembering an account must not require a second login click.");
            StringAssert.Contains("未能记住账号", notice);
            Assert.AreEqual(string.Empty, Control<TMP_InputField>("passwordInput").text);
            Assert.IsFalse(Control<TMP_InputField>("accountInput").interactable);
            Assert.IsNull(await store.ReadAsync());
        });

        [UnityTest]
        public IEnumerator ReopenedClientClicksLoginWithoutPasswordAndRotatesCredentials() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession original = CreateSession();
            string account = Account();
            Assert.AreEqual(ResultCode.Success, (await original.LoginAsync(account, "test-password")).ResultCode);
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
            Assert.AreEqual(ResultCode.Success, (await rotated.ResumeLoginAsync()).ResultCode);
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
            Assert.AreEqual(ResultCode.Success, (await original.LoginAsync(account, "test-password")).ResultCode);
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
