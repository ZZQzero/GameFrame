using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Hotfix;
using Game.AOT;
using GameFrame.Audio;
using GameFrame.Scene;
using UnityEngine.SceneManagement;
using YooAsset;
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
        private readonly List<GomokuRoomSession> gomokuRooms = new List<GomokuRoomSession>();
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
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GF_LOGIN_TEST_ENDPOINT")))
            {
                // One shared local server enforces 5 authentications/s per IP.
                // Fixtures isolate their admission windows instead of retrying rejected login.
                await UniTask.Delay(1100, ignoreTimeScale: true);
            }
        });

        [UnityTearDown]
        public IEnumerator Shutdown() => UniTask.ToCoroutine(async () =>
        {
            if (panel != null)
            {
                panel.DispatchDestroy();
                UnityEngine.Object.Destroy(panel.gameObject);
            }

            foreach (GomokuRoomSession room in gomokuRooms)
            {
                await room.DisposeAsync();
            }

            gomokuRooms.Clear();
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
        public IEnumerator CanceledGomokuAdmissionRequiresExplicitQueryBeforeMoreCommands() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession login = CreateSession();
            Assert.AreEqual(ResultCode.Success, await login.LoginAsync(Account(), "test-password"));
            var room = new GomokuRoomSession(login);
            gomokuRooms.Add(room);
            using var cancellation = new CancellationTokenSource();
            UniTask<GomokuResult> create = room.CreateAsync(cancellation.Token);
            cancellation.Cancel();
            bool canceled = false;
            try
            {
                await create;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert.IsTrue(canceled);
            Assert.IsTrue(room.NeedsQuery);
            Assert.IsFalse(room.IsBusy);
            await room.QueryAsync();
            Assert.IsFalse(room.NeedsQuery);
            if (room.Snapshot != null)
            {
                Assert.AreEqual(GomokuResult.Ok, await room.LeaveAsync());
            }
        });

        [UnityTest]
        public IEnumerator GomokuLobbyButtonsOpenAuthoritativeBoardAndLeaveBeforeReturning() => UniTask.ToCoroutine(async () =>
        {
#if UNITY_EDITOR
            GameLoginSession a = CreateSession();
            GameLoginSession b = CreateSession();
            Assert.AreEqual(ResultCode.Success, await a.LoginAsync(Account(), "test-password"));
            Assert.AreEqual(ResultCode.Success, await b.LoginAsync(Account(), "test-password"));
            var roomA = new GomokuRoomSession(a);
            var roomB = new GomokuRoomSession(b);
            gomokuRooms.Add(roomA);
            gomokuRooms.Add(roomB);
            var config = ScriptableObject.CreateInstance<GlobalConfig>();
            config.PlayMode = EPlayMode.EditorSimulateMode;
            var resources = new ResourceLoadManager();
            var previousScene = SceneManager.GetActiveScene();
            GameFlow flow = null;
            try
            {
                await resources.InitializeAsync(config);
                await resources.UpdatePackageManifestAsync();
                GameUI.Init(resources.Package);
                GameUIRegistration.RegisterAll();
                var audio = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioRuntimeConfig>("Assets/Config/Audio/GameAudioConfig.asset");
                await GameAudio.InitAsync(resources.Package, audio);
                GameScene.Init(resources.Package);
                flow = new GameFlow(a, roomA);
                await flow.StartAsync();
                GameUI.Get<LoginPanel>().transform.Find("Card/ConfirmLoginButton").GetComponent<Button>().onClick.Invoke();
                await UniTask.WaitUntil(() => GameUI.IsOpen<MainPanel>()).Timeout(TimeSpan.FromSeconds(10));
                GameUI.Get<MainPanel>().transform.Find("Content/Card/GomokuButton").GetComponent<Button>().onClick.Invoke();
                await UniTask.WaitUntil(() => GameUI.IsOpen<GomokuLobbyPanel>()).Timeout(TimeSpan.FromSeconds(5));
                var lobby = GameUI.Get<GomokuLobbyPanel>();
                lobby.transform.Find("Card/CreateButton").GetComponent<Button>().onClick.Invoke();
                await UniTask.WaitUntil(() => roomA.Snapshot != null && !roomA.IsBusy).Timeout(TimeSpan.FromSeconds(5));
                Assert.AreEqual(GomokuRoomState.Waiting, roomA.Snapshot.State);
                Assert.IsTrue(lobby.transform.Find("Card/StatusText").GetComponent<TextMeshProUGUI>().text.Contains(roomA.Snapshot.RoomCode));
                Assert.AreEqual(GomokuResult.Ok, await roomB.JoinAsync(roomA.Snapshot.RoomCode));
                await UniTask.WaitUntil(() => roomA.Snapshot.Members.Count == 2).Timeout(TimeSpan.FromSeconds(5));
                lobby.transform.Find("Card/ReadyButton").GetComponent<Button>().onClick.Invoke();
                await UniTask.WaitUntil(() => !roomA.IsBusy).Timeout(TimeSpan.FromSeconds(5));
                await roomB.QueryAsync();
                Assert.AreEqual(GomokuResult.Ok, await roomB.ReadyAsync(true));
                Button start = lobby.transform.Find("Card/StartButton").GetComponent<Button>();
                await UniTask.WaitUntil(() => start.interactable).Timeout(TimeSpan.FromSeconds(5));
                start.onClick.Invoke();
                await UniTask.WaitUntil(() => GameUI.IsOpen<GomokuOnlinePanel>()).Timeout(TimeSpan.FromSeconds(5));
                Assert.IsFalse(lobby.gameObject.activeInHierarchy);
                Assert.AreEqual(GomokuRoomState.Playing, roomA.Snapshot.State);
                var online = GameUI.Get<GomokuOnlinePanel>();
                Assert.IsTrue(online.GetComponentInChildren<GomokuBoardView>().Interactable);
                Assert.AreEqual(GomokuResult.Ok, await roomA.MoveAsync(7, 7));
                Assert.IsFalse(online.GetComponentInChildren<GomokuBoardView>().Interactable);
                await roomB.QueryAsync();
                Assert.AreEqual(GomokuResult.Ok, await roomB.MoveAsync(7, 8));
                await UniTask.WaitUntil(() => roomA.Game.Moves.Count == 2).Timeout(TimeSpan.FromSeconds(5));
                Assert.IsTrue(online.GetComponentInChildren<GomokuBoardView>().Interactable);
                online.transform.Find("GomokuBg/Content/ExitButton").GetComponent<Button>().onClick.Invoke();
                await UniTask.WaitUntil(() => !GameUI.IsOpen<GomokuOnlinePanel>()).Timeout(TimeSpan.FromSeconds(5));
                Assert.IsNull(roomA.Snapshot);
                Assert.IsTrue(lobby.gameObject.activeInHierarchy);
                await roomB.QueryAsync();
                Assert.AreEqual(GomokuOutcome.WhiteVictory, roomB.Snapshot.Outcome);
                Assert.AreEqual(GomokuResult.Ok, await roomB.LeaveAsync());
                lobby.transform.Find("Card/LocalButton").GetComponent<Button>().onClick.Invoke();
                await UniTask.WaitUntil(() => GameUI.IsOpen<GomokuMainPanel>()).Timeout(TimeSpan.FromSeconds(5));
                GameUI.Get<GomokuMainPanel>().ApplyMove(0, 0, GomokuStone.Black);
                Assert.AreEqual(1, GameUI.Get<GomokuMainPanel>().Game.Moves.Count);
            }
            finally
            {
                if (flow != null)
                {
                    await flow.StopAsync();
                }

                GameUI.Shutdown();
                if (GameScene.IsInited)
                {
                    if (GameScene.IsLoaded("Main"))
                    {
                        SceneManager.SetActiveScene(previousScene.IsValid() && previousScene.isLoaded
                            ? previousScene : SceneManager.CreateScene("GomokuFlowTests"));
                        await GameScene.UnloadAsync("Main");
                    }

                    await GameScene.ShutdownAsync();
                }

                if (GameAudio.IsInited)
                {
                    await GameAudio.ShutdownAsync();
                }

                await resources.ShutdownAsync();
                UnityEngine.Object.Destroy(config);
            }
#else
            Assert.Ignore("Gomoku UI test requires Editor simulated assets.");
            await UniTask.CompletedTask;
#endif
        });

        [UnityTest]
        public IEnumerator TwoGomokuSessionsPlayAuthoritativeGameAndReleaseRoom() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession a = CreateSession();
            GameLoginSession b = CreateSession();
            Assert.AreEqual(ResultCode.Success, await a.LoginAsync(Account(), "test-password"));
            Assert.AreEqual(ResultCode.Success, await b.LoginAsync(Account(), "test-password"));
            var roomA = new GomokuRoomSession(a);
            var roomB = new GomokuRoomSession(b);
            gomokuRooms.Add(roomA);
            gomokuRooms.Add(roomB);
            Assert.AreEqual(GomokuResult.Ok, await roomA.CreateAsync());
            Assert.AreEqual(GomokuResult.Ok, await roomB.JoinAsync(roomA.Snapshot.RoomCode));
            await roomA.QueryAsync();
            Assert.AreEqual(GomokuResult.Ok, await roomA.ReadyAsync(true));
            await roomB.QueryAsync();
            Assert.AreEqual(GomokuResult.Ok, await roomB.ReadyAsync(true));
            await roomA.QueryAsync();
            Assert.AreEqual(GomokuResult.Ok, await roomA.StartAsync());
            for (int column = 0; column < 5; column++)
            {
                await roomA.QueryAsync();
                Assert.AreEqual(GomokuResult.Ok, await roomA.MoveAsync(column, 0));
                if (column < 4)
                {
                    await roomB.QueryAsync();
                    Assert.AreEqual(GomokuResult.Ok, await roomB.MoveAsync(column, 2));
                }
            }

            await UniTask.WaitUntil(() => roomB.Snapshot.Outcome == GomokuOutcome.BlackVictory).Timeout(TimeSpan.FromSeconds(5));
            Assert.AreEqual(roomA.Snapshot.Version, roomB.Snapshot.Version);
            Assert.AreEqual(9, roomA.Game.Moves.Count);
            Assert.AreEqual(9, roomB.Game.Moves.Count);
            Assert.AreEqual(GomokuGameState.BlackWon, roomB.Game.State);
            Assert.AreEqual(GomokuResult.InvalidState, await roomB.MoveAsync(10, 10));
            Assert.AreEqual(GomokuResult.Ok, await roomA.LeaveAsync());
            Assert.IsNull(roomA.Snapshot);
            await roomB.QueryAsync();
            Assert.AreEqual(GomokuOutcome.BlackVictory, roomB.Snapshot.Outcome);
            Assert.AreEqual(GomokuResult.Ok, await roomB.LeaveAsync());
            Assert.IsNull(roomB.Snapshot);
        });

        [UnityTest]
        public IEnumerator GomokuClosedPushCannotBeUndoneByOldRpcAndTopLoginEndsMembership() => UniTask.ToCoroutine(async () =>
        {
            GameLoginSession a = CreateSession();
            string account = Account();
            Assert.AreEqual(ResultCode.Success, await a.LoginAsync(account, "test-password"));
            var room = new GomokuRoomSession(a);
            gomokuRooms.Add(room);
            Assert.AreEqual(GomokuResult.Ok, await room.CreateAsync());
            GomokuRoomView view = room.Snapshot;
            var baseline = new GomokuSnapshot
            {
                RoomId = view.RoomId, RoomCode = view.RoomCode, Version = view.Version,
                RecipientAccountId = view.RecipientAccountId, RecipientAuthEpoch = view.RecipientAuthEpoch,
                RecipientMemberId = view.RecipientMemberId, AdmissionCommandId = view.AdmissionCommandId
            };
            var close = new GomokuSnapshot
            {
                RoomId = baseline.RoomId, RoomCode = baseline.RoomCode, Version = baseline.Version + 1,
                State = GomokuRoomState.Closed, RecipientAccountId = baseline.RecipientAccountId,
                RecipientAuthEpoch = baseline.RecipientAuthEpoch, RecipientMemberId = baseline.RecipientMemberId,
                AdmissionCommandId = baseline.AdmissionCommandId
            };
            MethodInfo apply = typeof(GomokuRoomSession).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic);
            apply.Invoke(room, new object[] { close, a.CurrentUser, false });
            Assert.IsNull(room.Snapshot);
            apply.Invoke(room, new object[] { baseline, a.CurrentUser, false });
            Assert.IsNull(room.Snapshot, "A closed membership must not be resurrected by an older response.");
            await room.QueryAsync();
            Assert.IsNull(room.Snapshot, "The previous membership remains closed locally until explicit server cleanup.");
            GameLoginSession replacement = CreateSession();
            Assert.AreEqual(ResultCode.Success, await replacement.LoginAsync(account, "test-password"));
            await UniTask.WaitUntil(() => !a.IsAuthenticated).Timeout(TimeSpan.FromSeconds(5));
            Assert.IsNull(room.Snapshot);
        });

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
