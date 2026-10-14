using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Hotfix;
using Game.Protocol;
using GameFrame.Network;
using GameFrame.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GameFrame.Tests
{
    public sealed class GomokuLifecycleTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject root;
        GameObject panelObject;
        NetworkRuntime runtime;
        NetworkClient client;
        GameLoginSession login;
        GomokuRoomSession room;
        GameUser user;
        ConnectionContext authenticatedContext;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("GomokuLifecycleTests");
            runtime = new NetworkRuntime(root.transform, new NetworkRuntimeOptions());
            // The fixture sets authentication only for synchronous business dispatch;
            // no socket starts, and the stub is removed before yielding to the player loop.
            login = new GameLoginSession(runtime, new Uri("wss://localhost:1"));
            client = (NetworkClient)typeof(GameLoginSession).GetField("client", Private).GetValue(login);
            user = (GameUser)Activator.CreateInstance(typeof(GameUser), Private, null,
                new object[] { 1UL, "test", 2UL, long.MaxValue }, null);
            typeof(GameLoginSession).GetField("user", Private).SetValue(login, user);
            var context = (ConnectionContext)FormatterServices.GetUninitializedObject(typeof(ConnectionContext));
            typeof(ConnectionContext).GetField("gate", Private).SetValue(context, new object());
            typeof(ConnectionContext).GetField("publishedState", Private).SetValue(context, NetworkClientState.Connected);
            authenticatedContext = context;
            room = new GomokuRoomSession(login);
        }

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            if (panelObject != null)
            {
                panelObject.GetComponent<UIPanel>().DispatchDestroy();
                UnityEngine.Object.Destroy(panelObject);
            }

            if (client != null)
            {
                typeof(NetworkClient).GetField("context", Private).SetValue(client, null);
            }

            if (room != null)
            {
                await room.DisposeAsync();
            }

            if (login != null)
            {
                await login.DisposeAsync();
            }

            await runtime.ShutdownAsync();
            UnityEngine.Object.Destroy(root);
            await UniTask.NextFrame();
        });

        sealed class AuthenticationScope : IDisposable
        {
            readonly NetworkClient client;

            internal AuthenticationScope(NetworkClient client, ConnectionContext context)
            {
                this.client = client;
                typeof(NetworkClient).GetField("context", Private).SetValue(client, context);
            }

            public void Dispose() => typeof(NetworkClient).GetField("context", Private).SetValue(client, null);
        }

        static GomokuSnapshot Snapshot(ulong version = 2, GomokuRoomState state = GomokuRoomState.Waiting,
            ulong admission = 7, ulong member = 3) => new GomokuSnapshot
        {
            RoomId = new string('a', 32), RoomCode = "12345678", Version = version, State = state,
            RecipientAccountId = 1, RecipientAuthEpoch = 2, RecipientMemberId = member, AdmissionCommandId = admission,
            Members = new[] { new GomokuMember { AccountId = 1, MemberId = member, IsHost = true } }
        };

        void Invoke(string method, params object[] args) =>
            typeof(GomokuRoomSession).GetMethod(method, Private).Invoke(room, args);

        [TestCase(0UL)]
        [TestCase(7UL)]
        public void CloseBeforeAdmissionOrQueryBaselineCannotResurrectTheMembership(ulong admission)
        {
            using var authentication = new AuthenticationScope(client, authenticatedContext);
            Invoke("BeginBaseline", admission);
            Invoke("OnSnapshot", Snapshot(3, GomokuRoomState.Closed));
            Invoke("ApplyBaseline", Snapshot(), user);
            Assert.IsNull(room.Snapshot);
            Invoke("Apply", Snapshot(), user, true);
            Assert.IsNull(room.Snapshot, "The selected close must also leave a tombstone for later stale responses.");
        }

        [Test]
        public void QueryChoosesTheLatestMatchingPushAndIgnoresOlderVersions()
        {
            using var authentication = new AuthenticationScope(client, authenticatedContext);
            Invoke("BeginBaseline", 0UL);
            Invoke("OnSnapshot", Snapshot(4));
            Invoke("OnSnapshot", Snapshot(3));
            Invoke("ApplyBaseline", Snapshot(), user);
            Assert.AreEqual(4UL, room.Snapshot.Version);
            Assert.IsTrue(room.LastUpdateWasBaseline);
        }

        [Test]
        public void OtherMembershipOrAccountCannotReplaceTheBaseline()
        {
            using var authentication = new AuthenticationScope(client, authenticatedContext);
            Invoke("BeginBaseline", 7UL);
            Invoke("OnSnapshot", Snapshot(20, admission: 6, member: 8));
            GomokuSnapshot foreign = Snapshot(30);
            foreign.RecipientAuthEpoch++;
            Invoke("OnSnapshot", foreign);
            Invoke("ApplyBaseline", Snapshot(), user);
            Assert.AreEqual(2UL, room.Snapshot.Version);
            Invoke("ClearRoom", string.Empty);
            Invoke("BeginBaseline", 0UL);
            Invoke("OnSnapshot", Snapshot(40, member: 9));
            Invoke("ApplyBaseline", Snapshot(3), user);
            Assert.AreEqual(3UL, room.Snapshot.Version);
            Assert.AreEqual(3UL, room.Snapshot.RecipientMemberId);
        }

        [Test]
        public void DisplayViewDoesNotExposeMutableMemberDtos()
        {
            using var authentication = new AuthenticationScope(client, authenticatedContext);
            GomokuSnapshot dto = Snapshot();
            Invoke("Apply", dto, user, true);
            dto.Members[0].Ready = true;
            dto.RoomCode = "87654321";
            Assert.IsFalse(room.Snapshot.Members[0].Ready);
            Assert.AreEqual("12345678", room.Snapshot.RoomCode);
        }

        [UnityTest]
        public IEnumerator OldOnlineFailureCannotWriteIntoTheReopenedPanel() => CheckReopen<GomokuOnlinePanel>("GomokuOnline", "status");

        [UnityTest]
        public IEnumerator OldLobbyFailureCannotWriteIntoTheReopenedPanel() => CheckReopen<GomokuLobbyPanel>("GomokuLobby", "statusText");

        IEnumerator CheckReopen<T>(string prefab, string statusField) where T : UIPanel<GomokuRoomSession> =>
            UniTask.ToCoroutine(async () =>
            {
#if UNITY_EDITOR
                using var authentication = new AuthenticationScope(client, authenticatedContext);
                Invoke("Apply", Snapshot(), user, true);
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/Gomoku/" + prefab + ".prefab");
                panelObject = UnityEngine.Object.Instantiate(asset);
                T panel = panelObject.GetComponent<T>();
                panel.ApplyArgs(room);
                panel.DispatchCreate();
                panel.DispatchOpen();
                var pending = new UniTaskCompletionSource();
                Func<GomokuRoomSession, CancellationToken, UniTask> action = (_, _) => pending.Task;
                UniTask running = (UniTask)typeof(T).GetMethod("RunAsync", Private).Invoke(panel, new object[] { action });
                panel.DispatchClose();
                panel.DispatchOpen();
                var text = (TMPro.TMP_Text)typeof(T).GetField(statusField, Private).GetValue(panel);
                string reopened = text.text;
                pending.TrySetException(new InvalidOperationException("old cycle failure"));
                await running;
                Assert.AreEqual(reopened, text.text);
#else
                Assert.Ignore("Prefab lifecycle tests require Editor assets.");
                await UniTask.CompletedTask;
#endif
            });
    }
}
