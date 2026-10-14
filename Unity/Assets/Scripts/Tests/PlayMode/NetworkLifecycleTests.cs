using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Protocol;
using GameFrame.Network;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GameFrame.Tests
{
    public sealed class NetworkLifecycleTests
    {
        [UnityTearDown]
        public IEnumerator Shutdown() => UniTask.ToCoroutine(async () => await GameRuntime.ShutdownAsync());

        private static UniTask Initialize(NetworkRuntimeOptions options = null)
        {
            return GameRuntime.InitAsync(new GameRuntimeConfig
            {
                EnableTimer = false,
                EnableUI = false,
                NetworkOptions = options
            });
        }

        [UnityTest]
        public IEnumerator OfflineConfigurationDoesNotCreateNetworkOrRoot() => UniTask.ToCoroutine(async () =>
        {
            await Initialize();
            Assert.Throws<InvalidOperationException>(() => _ = GameRuntime.Network);
            Assert.IsNull(GameRuntime.PersistRoot);
        });

        [UnityTest]
        public IEnumerator NetworkOnlyConfigurationOwnsRootAndShutdownDisposesClients() => UniTask.ToCoroutine(async () =>
        {
            await Initialize(new NetworkRuntimeOptions());
            Assert.IsNotNull(GameRuntime.PersistRoot);
            NetworkClient client = GameRuntime.Network.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            Assert.AreEqual(NetworkClientState.Idle, client.State);
            Assert.AreEqual(0, client.GetMetrics().OwnedBytes);
            await GameRuntime.ShutdownAsync();
            Assert.AreEqual(NetworkClientState.Disposed, client.State);
            Assert.IsNull(GameRuntime.PersistRoot);
            Assert.Throws<InvalidOperationException>(() => _ = GameRuntime.Network);
        });

        [UnityTest]
        public IEnumerator DisposedObserverCanReenterRuntimeShutdown() => UniTask.ToCoroutine(async () =>
        {
            await Initialize(new NetworkRuntimeOptions());
            NetworkRuntime runtime = GameRuntime.Network;
            // Exercise worker shutdown even when there is no active UDP connection.
            runtime.GetKcpWorker();
            NetworkClient client = runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            Task reentered = null;
            int notifications = 0;
            using IDisposable observer = client.ObserveState(change =>
            {
                if (change.State == NetworkClientState.Disposed)
                {
                    notifications++;
                    reentered = runtime.ShutdownAsync().AsTask();
                }
            });

            await runtime.ShutdownAsync().Timeout(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(reentered);
            await reentered.AsUniTask().Timeout(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, notifications);
            Assert.AreEqual(NetworkClientState.Disposed, client.State);
            Assert.AreEqual(0, runtime.Memory.OwnedBytes);
            Assert.Throws<InvalidOperationException>(() => runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog));
        });

        [UnityTest]
        public IEnumerator ClientQuotaIsReleasedOnlyByDispose() => UniTask.ToCoroutine(async () =>
        {
            await Initialize(new NetworkRuntimeOptions { MaxClients = 1 });
            NetworkRuntime runtime = GameRuntime.Network;
            NetworkClient first = runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            await first.CloseAsync();
            Assert.Throws<NetworkBackpressureException>(() => runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog));
            await first.DisposeAsync();
            NetworkClient replacement = runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            Assert.AreEqual(NetworkClientState.Idle, replacement.State);
            await replacement.DisposeAsync();
            await replacement.DisposeAsync();
        });

        [UnityTest]
        public IEnumerator PushRegistrationHasSingleOwnerAndOldHandleCannotRemoveNewRegistration() => UniTask.ToCoroutine(async () =>
        {
            await Initialize(new NetworkRuntimeOptions());
            NetworkClient client = GameRuntime.Network.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            IDisposable first = client.Subscribe(GameNetRoutes.GomokuRoomSnapshot, _ => { });
            Assert.Throws<InvalidOperationException>(() => client.Subscribe(GameNetRoutes.GomokuRoomSnapshot, _ => { }));
            first.Dispose();
            using IDisposable second = client.Subscribe(GameNetRoutes.GomokuRoomSnapshot, _ => { });
            first.Dispose();
            Assert.Throws<InvalidOperationException>(() => client.Subscribe(GameNetRoutes.GomokuRoomSnapshot, _ => { }));
            await client.DisposeAsync();
        });

        [UnityTest]
        public IEnumerator BusinessApisRejectWorkerThreads() => UniTask.ToCoroutine(async () =>
        {
            await Initialize(new NetworkRuntimeOptions());
            NetworkClient client = GameRuntime.Network.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            Exception failure = await Task.Run(() =>
            {
                try
                {
                    _ = client.State;
                    return null;
                }
                catch (Exception error)
                {
                    return error;
                }
            });
            Assert.IsInstanceOf<InvalidOperationException>(failure);
            Assert.IsTrue(PlayerLoopHelper.IsMainThread);
            await client.DisposeAsync();
        });

        [UnityTest]
        public IEnumerator TrustedEndpointIsCheckedBeforeStartingIo() => UniTask.ToCoroutine(async () =>
        {
            await Initialize(new NetworkRuntimeOptions());
            NetworkClient client = GameRuntime.Network.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            Exception failure = null;
            try
            {
                await client.ConnectAsync(NetworkConnectOptions.WebSocket(new Uri("wss://localhost:7443/network")));
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsInstanceOf<ArgumentException>(failure);
            Assert.AreEqual(NetworkClientState.Idle, client.State);
            Assert.AreEqual(0, client.Generation);
            await client.DisposeAsync();
        });

        [UnityTest]
        public IEnumerator DestroyedDriverFailsAllClientsAndCloseDoesNotNeedTheDriver() => UniTask.ToCoroutine(async () =>
        {
            await Initialize(new NetworkRuntimeOptions());
            NetworkRuntime runtime = GameRuntime.Network;
            var endpoint = new Uri("wss://localhost:7443/network");
            NetworkClient connecting = runtime.CreateClient(new NetworkClientOptions
            {
                TrustedWebSocketEndpoints = new[] { endpoint }
            }, GameNetProtocol.Catalog);
            NetworkClient idle = runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            var states = new List<NetworkClientState>();
            Exception hostFailure = null;
            using IDisposable observer = connecting.ObserveState(change =>
            {
                states.Add(change.State);
                if (change.State == NetworkClientState.Connecting)
                {
                    LogAssert.Expect(LogType.Exception, new Regex("Network driver was disabled or destroyed"));
                    UnityEngine.Object.DestroyImmediate(GameRuntime.PersistRoot.GetComponentInChildren<NetworkRuntimeDriver>().gameObject);
                }
                else if (change.State == NetworkClientState.Faulted)
                {
                    hostFailure = change.Error;
                }
            });

            Exception connectFailure = null;
            try
            {
                await connecting.ConnectAsync(NetworkConnectOptions.WebSocket(endpoint)).Timeout(TimeSpan.FromSeconds(5));
            }
            catch (Exception error)
            {
                connectFailure = error;
            }

            Assert.IsNotNull(hostFailure);
            Assert.AreSame(hostFailure, connectFailure);
            Assert.AreEqual(NetworkClientState.Faulted, connecting.State);
            Assert.AreEqual(NetworkClientState.Faulted, idle.State);
            NetworkException admissionFailure = Assert.Throws<NetworkException>(() =>
                runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog));
            Assert.AreSame(hostFailure, admissionFailure.InnerException);
            await connecting.CloseAsync().Timeout(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, connecting.GetMetrics().OwnedBytes);
            Assert.AreEqual(0, runtime.Memory.OwnedBytes);
            CollectionAssert.AreEqual(new[] { NetworkClientState.Connecting, NetworkClientState.Faulted,
                NetworkClientState.Closing, NetworkClientState.Idle }, states);
            Assert.AreEqual(NetworkClientState.Faulted, connecting.State, "A failed host cannot be reused after Close.");
            await runtime.ShutdownAsync().Timeout(TimeSpan.FromSeconds(5));
            await runtime.ShutdownAsync();
            Assert.AreEqual(NetworkClientState.Disposed, connecting.State);
            Assert.AreEqual(NetworkClientState.Disposed, idle.State);
        });

        [UnityTest]
        public IEnumerator ClosingObserverReusesTheActiveCloseTask() => UniTask.ToCoroutine(async () =>
        {
            await VerifyClosingReentry(false);
        });

        [UnityTest]
        public IEnumerator ClosingObserverReusesTheActiveDisposeTask() => UniTask.ToCoroutine(async () =>
        {
            await VerifyClosingReentry(true);
        });

        private static async UniTask VerifyClosingReentry(bool dispose)
        {
            await Initialize(new NetworkRuntimeOptions());
            var endpoint = new Uri("wss://localhost:7443/network");
            NetworkClient client = GameRuntime.Network.CreateClient(new NetworkClientOptions
            {
                TrustedWebSocketEndpoints = new[] { endpoint }
            }, GameNetProtocol.Catalog);
            Task first = null;
            Task second = null;
            int closingNotifications = 0;
            int disposedNotifications = 0;
            using IDisposable observer = client.ObserveState(change =>
            {
                if (change.State == NetworkClientState.Connecting)
                {
                    first = dispose ? client.DisposeAsync().AsTask() : client.CloseAsync().AsTask();
                }
                else if (change.State == NetworkClientState.Closing)
                {
                    closingNotifications++;
                    second = dispose ? client.DisposeAsync().AsTask() : client.CloseAsync().AsTask();
                }
                else if (change.State == NetworkClientState.Disposed)
                {
                    disposedNotifications++;
                }
            });

            Exception failure = null;
            try
            {
                await client.ConnectAsync(NetworkConnectOptions.WebSocket(endpoint)).Timeout(TimeSpan.FromSeconds(5));
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.IsNotNull(failure);
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            await first.AsUniTask().Timeout(TimeSpan.FromSeconds(5));
            await second.AsUniTask().Timeout(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, closingNotifications);
            Assert.AreEqual(dispose ? 1 : 0, disposedNotifications);
            Assert.AreEqual(dispose ? NetworkClientState.Disposed : NetworkClientState.Idle, client.State);
            Assert.AreEqual(0, GameRuntime.Network.Memory.OwnedBytes);
            await client.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator DisabledRootStopsAdmissionAndStillAllowsShutdown() => UniTask.ToCoroutine(async () =>
        {
            await Initialize(new NetworkRuntimeOptions());
            NetworkRuntime runtime = GameRuntime.Network;
            NetworkClient client = runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog);
            LogAssert.Expect(LogType.Exception, new Regex("Network driver was disabled or destroyed"));
            GameRuntime.PersistRoot.gameObject.SetActive(false);
            Assert.AreEqual(NetworkClientState.Faulted, client.State);
            Assert.Throws<NetworkException>(() => runtime.CreateClient(new NetworkClientOptions(), GameNetProtocol.Catalog));
            await GameRuntime.ShutdownAsync().Timeout(TimeSpan.FromSeconds(5));
            Assert.AreEqual(NetworkClientState.Disposed, client.State);
            Assert.IsNull(GameRuntime.PersistRoot);
        });
    }
}
