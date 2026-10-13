using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Hotfix;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace GameFrame.Tests
{
    public sealed class GameLoginSessionTests
    {
        private readonly List<GameLoginSession> sessions = new List<GameLoginSession>();
        private LoginPanel panel;
#if UNITY_EDITOR
        private object tlsSettings;
        private PropertyInfo trustAnchors;
        private object previousAnchors;
        private System.Security.Cryptography.X509Certificates.X509Certificate2 testCertificate;
#endif

        [UnitySetUp]
        public IEnumerator Initialize() => UniTask.ToCoroutine(async () =>
        {
#if UNITY_EDITOR
            // Unity TLS does not inherit dotnet dev-certs trust on every Editor platform.
            // An explicitly supplied public certificate is a test-only trust anchor;
            // chain, expiry and hostname validation remain enabled.
            string certificatePath = Environment.GetEnvironmentVariable("GF_LOGIN_TEST_CERTIFICATE");
            if (!string.IsNullOrEmpty(certificatePath))
            {
                Type settingsType = Type.GetType("Mono.Security.Interface.MonoTlsSettings, Mono.Security", true);
                tlsSettings = settingsType.GetProperty("DefaultSettings").GetValue(null);
                trustAnchors = settingsType.GetProperty("TrustAnchors");
                previousAnchors = trustAnchors.GetValue(tlsSettings);
                testCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificatePath);
                var anchors = new System.Security.Cryptography.X509Certificates.X509CertificateCollection { testCertificate };
                trustAnchors.SetValue(tlsSettings, anchors);
            }
#endif
            await GameRuntime.InitAsync(new GameRuntimeConfig
            {
                EnableTimer = false,
                EnableUI = false,
                NetworkOptions = new NetworkRuntimeOptions()
            });
        });

        [UnityTearDown]
        public IEnumerator Shutdown() => UniTask.ToCoroutine(async () =>
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
#if UNITY_EDITOR
            if (tlsSettings != null)
            {
                trustAnchors.SetValue(tlsSettings, previousAnchors);
                testCertificate.Dispose();
                tlsSettings = null;
            }
#endif
        });

        private GameLoginSession CreateSession(bool requireServer = true)
        {
            string endpoint = Environment.GetEnvironmentVariable("GF_LOGIN_TEST_ENDPOINT");
            if (requireServer && string.IsNullOrEmpty(endpoint))
            {
                Assert.Ignore("Real login integration requires GF_LOGIN_TEST_ENDPOINT and a local WSS/PostgreSQL server.");
            }

            var session = new GameLoginSession(GameRuntime.Network, new Uri(endpoint ?? "wss://localhost:7443/network"));
            sessions.Add(session);
            return session;
        }

        private static string Account() => "unity_" + Guid.NewGuid().ToString("N");

        [UnityTest]
        public IEnumerator FirstLoginRetainsUserAndConnectionUntilExplicitLogout() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            string account = Account();
            Assert.AreEqual(ResultCode.Success, await session.LoginAsync(account, "test-password"));
            GameUser user = session.CurrentUser;
            Assert.IsNotNull(user);
            Assert.AreEqual(account, user.AccountName);
            Assert.Greater(user.AccountId, 0UL);
            Assert.Greater(user.AuthEpoch, 0UL);
            Assert.Greater(user.AccountDeadlineMilliseconds, NetworkClock.GetMilliseconds());
            NetworkClient client = session.LobbyClient;
            EchoResponse echo = await client.RequestAsync(GameNetRoutes.Echo, new EchoRequest { Text = "login-integration" },
                TimeSpan.FromSeconds(5));
            Assert.AreEqual("login-integration", echo.Text);
            int lost = 0;
            session.AuthenticationLost += () => lost++;
            Assert.AreEqual(ResultCode.Success, await session.LogoutAsync());
            Assert.AreEqual(1, lost);
            Assert.IsNull(session.CurrentUser);
            Assert.IsFalse(session.IsAuthenticated);
            Assert.AreEqual(NetworkClientState.Idle, client.State);
            Assert.AreEqual(0, client.GetMetrics().OwnedBytes);
            Assert.AreEqual(ResultCode.Success, await session.LoginAsync(account, "test-password"));
            Assert.AreEqual(user.AccountId, session.CurrentUser.AccountId);
            Assert.Greater(session.CurrentUser.AuthEpoch, user.AuthEpoch);
        });

        [UnityTest]
        public IEnumerator WrongPasswordPreservesOldSessionAndCorrectLoginReplacesIt() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession original = CreateSession();
            GameLoginSession replacement = CreateSession();
            string account = Account();
            Assert.AreEqual(ResultCode.Success, await original.LoginAsync(account, "test-password"));
            GameUser user = original.CurrentUser;
            int lost = 0;
            original.AuthenticationLost += () => lost++;
            Assert.AreEqual(ResultCode.InvalidCredentials, await replacement.LoginAsync(account, "wrong-password"));
            Assert.IsNull(replacement.CurrentUser);
            Assert.IsFalse(replacement.IsBusy);
            Assert.IsTrue(original.IsAuthenticated);
            Assert.AreEqual(0, lost);
            Assert.AreEqual(ResultCode.Success, await replacement.LoginAsync(account, "test-password"));
            await UniTask.WaitUntil(() => !original.IsAuthenticated).Timeout(TimeSpan.FromSeconds(5));
            Assert.IsNull(original.CurrentUser);
            Assert.AreEqual(1, lost);
            Assert.AreEqual(user.AccountId, replacement.CurrentUser.AccountId);
            Assert.Greater(replacement.CurrentUser.AuthEpoch, user.AuthEpoch);
        });

        [UnityTest]
        public IEnumerator InvalidOrCancelledInputDoesNotLeaveAnActiveOperation() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession(false);
            Exception failure = null;
            try
            {
                await session.LoginAsync(" bad-account ", "test-password");
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOf<ArgumentException>(failure);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            failure = null;
            try
            {
                await session.LoginAsync(Account(), "test-password", cancellation.Token);
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOf<OperationCanceledException>(failure);
            Assert.IsFalse(session.IsBusy);
            Assert.IsNull(session.CurrentUser);
            Assert.AreEqual(0, GameRuntime.Network.Memory.OwnedBytes);
        });

        [UnityTest]
        public IEnumerator DisposeCancelsAndJoinsPendingLogin() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            var pending = session.LoginAsync(Account(), "test-password").AsTask();
            Assert.IsTrue(session.IsBusy);
            await session.DisposeAsync();
            Exception failure = null;
            try
            {
                await pending;
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOf<OperationCanceledException>(failure);
            Assert.IsFalse(session.IsBusy);
            Assert.IsNull(session.CurrentUser);
            Assert.AreEqual(0, GameRuntime.Network.Memory.OwnedBytes);
            await session.DisposeAsync();
        });

#if UNITY_EDITOR
        private void OpenPanel(GameLoginSession session, Func<UniTask> loggedIn, string message = "")
        {
            if (panel == null)
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/HitfixPrefab/LoginPanel.prefab");
                panel = UnityEngine.Object.Instantiate(prefab).GetComponent<LoginPanel>();
                panel.DispatchCreate();
            }

            panel.ApplyArgs(new LoginPanelArgs(session, loggedIn, message));
            panel.DispatchOpen();
        }

        private T Control<T>(string name) => (T)typeof(LoginPanel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);

        [UnityTest]
        public IEnumerator LoginPanelSubmitsOnceAndClosingItDoesNotOwnTheSession() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            int successes = 0;
            OpenPanel(session, () =>
            {
                successes++;
                panel.DispatchClose();
                return UniTask.CompletedTask;
            });
            Control<TMP_InputField>("accountInput").text = Account();
            Control<TMP_InputField>("passwordInput").text = "test-password";
            Button confirm = Control<Button>("confirmLoginButton");
            confirm.onClick.Invoke();
            confirm.onClick.Invoke();
            Assert.IsFalse(confirm.interactable);
            await UniTask.WaitUntil(() => successes == 1).Timeout(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, successes);
            Assert.AreEqual(string.Empty, Control<TMP_InputField>("passwordInput").text);
            Assert.IsTrue(session.IsAuthenticated);
            Assert.AreEqual(NetworkClientState.Connected, session.LobbyClient.State);
        });

        [UnityTest]
        public IEnumerator FailedMainEntryCanBeRetriedWithoutLoggingInAgain() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            int entries = 0;
            OpenPanel(session, () =>
            {
                entries++;
                return entries == 1
                    ? UniTask.FromException(new InvalidOperationException("test main entry failure"))
                    : UniTask.CompletedTask;
            });
            Control<TMP_InputField>("accountInput").text = Account();
            Control<TMP_InputField>("passwordInput").text = "test-password";
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: test main entry failure");
            Button confirm = Control<Button>("confirmLoginButton");
            confirm.onClick.Invoke();
            await UniTask.WaitUntil(() => entries == 1 && confirm.interactable).Timeout(TimeSpan.FromSeconds(10));
            GameUser user = session.CurrentUser;
            Assert.IsNotNull(user);
            Assert.AreEqual("进入主场景失败，请再次点击确定", Control<TextMeshProUGUI>("statusText").text);
            Assert.AreEqual(string.Empty, Control<TMP_InputField>("passwordInput").text);
            Assert.IsFalse(Control<TMP_InputField>("accountInput").interactable);
            Assert.IsFalse(Control<TMP_InputField>("passwordInput").interactable);
            confirm.onClick.Invoke();
            await UniTask.WaitUntil(() => entries == 2 && confirm.interactable).Timeout(TimeSpan.FromSeconds(5));
            Assert.AreSame(user, session.CurrentUser, "再次点击只进入主场景，不重复认证或增加 epoch。");
        });

        [UnityTest]
        public IEnumerator PanelCloseAndReopenCancelsOldRequestWithoutChangingNewStatus() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession session = CreateSession();
            int successes = 0;
            Func<UniTask> loggedIn = () =>
            {
                successes++;
                return UniTask.CompletedTask;
            };
            OpenPanel(session, loggedIn);
            Control<TMP_InputField>("accountInput").text = Account();
            Control<TMP_InputField>("passwordInput").text = "test-password";
            Control<Button>("confirmLoginButton").onClick.Invoke();
            panel.DispatchClose();
            OpenPanel(session, loggedIn, "reopened");
            await UniTask.WaitUntil(() => !session.IsBusy && Control<Button>("confirmLoginButton").interactable)
                .Timeout(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, successes);
            Assert.AreEqual("reopened", Control<TextMeshProUGUI>("statusText").text);
            Assert.AreEqual(string.Empty, Control<TMP_InputField>("passwordInput").text);
            Assert.IsNull(session.CurrentUser);
            Assert.AreEqual(0, GameRuntime.Network.Memory.OwnedBytes);
        });
#endif
    }
}
