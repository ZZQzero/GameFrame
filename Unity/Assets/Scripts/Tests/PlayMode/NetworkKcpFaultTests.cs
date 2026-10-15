#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Cysharp.Threading.Tasks;
using Game.Protocol;
using GameFrame.Network;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GameFrame.Tests
{
    // Opt-in real sockets and a separate .NET companion, never a mocked NetworkClient.
    public sealed class NetworkKcpFaultTests
    {
        [Serializable]
        private sealed class HostReply
        {
            public int[] Ports;
            public uint Conv;
            public string Identity;
            public string Psk;
            public string Ticket;
            public bool Ok;
            public bool FactoryEntered;
            public bool SessionCompleted;
            public int ServerReason;
            public string ServerErrorType;
            public int HandlerCalls;
            public long ClientToServerFaultDrops;
            public long ServerToClientFaultDrops;
            public long HeldPackets;
            public long ReleasedHeldPackets;
            public int QueuePackets;
            public bool AccountingValid;
            public long QueueOverflowDrops;
            public bool ResourcesReleased;
            public bool NormalSessionsClosed;
        }

        [Serializable]
        private sealed class HostCommand
        {
            public string Operation;
            public int Index;
            public bool Enabled;
        }

        private readonly List<NetworkClient> clients = new List<NetworkClient>();
        private readonly List<Task> operations = new List<Task>();
        private readonly List<NetworkStateChange> changes = new List<NetworkStateChange>();
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private Process process;
        private string directory;
        private Uri[] endpoints;
        private int sequence;
        private int controlEchoes;
        private bool callbacksOnMainThread;
        private NetworkClient control;
        private NetworkRuntime runtime;
        private CancellationTokenSource controlStop;
        private Task controlTask;
        private bool optedIn;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Task setup = SetUpAsync().AsTask();
            while (!setup.IsCompleted)
            {
                yield return null;
            }

            setup.GetAwaiter().GetResult();
        }

        private async UniTask SetUpAsync()
        {
            if (Environment.GetEnvironmentVariable("GF_UNITY_KCP_FAULT_TESTS") != "1")
            {
                Assert.Ignore("Opt in with GF_UNITY_KCP_FAULT_TESTS=1 and the documented .NET/native prerequisites.");
            }

            string dotnet = Environment.GetEnvironmentVariable("GF_UNITY_KCP_FAULT_DOTNET");
            string native = Environment.GetEnvironmentVariable("GF_KCP_NATIVE_LIBRARY");
            Assert.IsTrue(File.Exists(dotnet), "Configure GF_UNITY_KCP_FAULT_DOTNET with an absolute dotnet executable path.");
            Assert.IsTrue(File.Exists(native), "Configure a verified GF_KCP_NATIVE_LIBRARY for the host platform.");
            optedIn = true;
            clients.Clear();
            operations.Clear();
            changes.Clear();
            subscriptions.Clear();
            sequence = controlEchoes = 0;
            callbacksOnMainThread = true;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            directory = Path.Combine(Path.GetTempPath(), "gameframe-unity-kcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string settings = Path.Combine(directory, "host.runsettings");
            using (XmlWriter writer = XmlWriter.Create(settings))
            {
                writer.WriteStartElement("RunSettings");
                writer.WriteStartElement("TestRunParameters");
                writer.WriteStartElement("Parameter");
                writer.WriteAttributeString("name", "HostDirectory");
                writer.WriteAttributeString("value", directory);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            var start = new ProcessStartInfo
            {
                FileName = dotnet, WorkingDirectory = Path.Combine(root, "Tools"), UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                Arguments = "test NetworkServer.Tests/NetworkServer.Tests.csproj -c Release --no-build --no-restore " +
                    "--filter FullyQualifiedName=GameFrame.Tests.KcpUnityFaultHostTests.ServeUnityFaults " +
                    "--settings " + Quote(settings) + " --results-directory " + Quote(directory)
            };
            start.EnvironmentVariables["GF_KCP_NATIVE_LIBRARY"] = Path.GetFullPath(native);
            process = new Process { StartInfo = start };
            // Drain both pipes without retaining arbitrary process/exception text or credentials.
            process.OutputDataReceived += (_, __) => { };
            process.ErrorDataReceived += (_, __) => { };
            Assert.IsTrue(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            string ready = Path.Combine(directory, "ready.json");
            await WaitAsync(() => File.Exists(ready), 20000);
            HostReply reply = JsonUtility.FromJson<HostReply>(File.ReadAllText(ready));
            File.Delete(ready);
            Assert.AreEqual(3, reply.Ports.Length);
            endpoints = reply.Ports.Select(port => new Uri("udp://127.0.0.1:" + port)).ToArray();
            await GameRuntime.InitAsync(new GameRuntimeConfig
            {
                EnableTimer = false, EnableUI = false, NetworkOptions = new NetworkRuntimeOptions()
            });
            runtime = GameRuntime.Network;
            control = CreateClient();
            await ConnectAsync(control, 1);
            controlStop = new CancellationTokenSource();
            controlTask = ProbeControlAsync(controlStop.Token).AsTask();
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private NetworkClient CreateClient(int pendingCount = 512, bool observed = false)
        {
            NetworkClient client = runtime.CreateClient(new NetworkClientOptions
            {
                TrustedUdpEndpoints = endpoints, PendingCount = pendingCount
            }, GameNetProtocol.Catalog);
            clients.Add(client);
            if (observed)
            {
                subscriptions.Add(client.ObserveState(change =>
                {
                    callbacksOnMainThread &= PlayerLoopHelper.IsMainThread;
                    changes.Add(change);
                }));
            }

            return client;
        }

        private async UniTask ProbeControlAsync(CancellationToken cancellation)
        {
            try
            {
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    EchoResponse echo = await control.RequestAsync(GameNetRoutes.Echo, new EchoRequest { Text = "control" },
                        TimeSpan.FromSeconds(3), cancellation);
                    Assert.AreEqual("control", echo.Text);
                    Assert.IsTrue(PlayerLoopHelper.IsMainThread);
                    controlEchoes++;
                    await UniTask.Delay(500, ignoreTimeScale: true, cancellationToken: cancellation);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Only the owned probe's explicit stop is expected.
            }
        }

        private async UniTask WaitAsync(Func<bool> predicate, int milliseconds = 6000, bool checkProcess = true)
        {
            long deadline = NetworkClock.GetMilliseconds() + milliseconds;
            while (!predicate())
            {
                if (checkProcess && process != null && process.HasExited)
                {
                    Assert.Fail("Unity KCP companion exited before completing the command; exit code " + process.ExitCode);
                }

                if (controlTask != null && controlTask.IsFaulted)
                {
                    await controlTask;
                }

                Assert.Less(NetworkClock.GetMilliseconds(), deadline, "Unity KCP test observation deadline exceeded.");
                await UniTask.Delay(10, ignoreTimeScale: true);
            }
        }

        private async UniTask<HostReply> CommandAsync(string operation, int index = 0, bool enabled = false)
        {
            string name = Path.Combine(directory, (++sequence).ToString("D6"));
            File.WriteAllText(name + ".tmp", JsonUtility.ToJson(new HostCommand
            {
                Operation = operation, Index = index, Enabled = enabled
            }));
            File.Move(name + ".tmp", name + ".command.json");
            string result = name + ".result.json";
            await WaitAsync(() => File.Exists(result), operation == "revoke" ? 10000 : 6000);
            HostReply reply = JsonUtility.FromJson<HostReply>(File.ReadAllText(result));
            File.Delete(result);
            if (operation != "grant" && operation != "status")
            {
                Assert.IsTrue(reply.Ok, "Companion command failed: " + operation);
            }

            return reply;
        }

        private async UniTask WaitStatusAsync(Func<HostReply, bool> predicate, int milliseconds = 6000)
        {
            long deadline = NetworkClock.GetMilliseconds() + milliseconds;
            while (!predicate(await CommandAsync("status")))
            {
                Assert.Less(NetworkClock.GetMilliseconds(), deadline, "Expected server/relay state was not observed.");
                await UniTask.Delay(20, ignoreTimeScale: true);
            }
        }

        private static byte[] Decode(string text)
        {
            var bytes = new byte[text.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = Convert.ToByte(text.Substring(index * 2, 2), 16);
            }

            return bytes;
        }

        private async UniTask ConnectAsync(NetworkClient client, int index, CancellationToken cancellation = default)
        {
            HostReply grant = await CommandAsync("grant", index);
            byte[] identity = Decode(grant.Identity);
            byte[] psk = Decode(grant.Psk);
            byte[] ticket = Decode(grant.Ticket);
            grant.Identity = grant.Psk = grant.Ticket = null;
            try
            {
                Task connect = client.ConnectAsync(NetworkConnectOptions.Kcp(endpoints[index], ticket, grant.Conv,
                    identity, psk, NetworkClock.GetMilliseconds() + 30000, unreliableLimit: 0), cancellation).AsTask();
                operations.Add(connect);
                await connect;
                Assert.IsTrue(PlayerLoopHelper.IsMainThread);
            }
            finally
            {
                Array.Clear(identity, 0, identity.Length);
                Array.Clear(psk, 0, psk.Length);
                Array.Clear(ticket, 0, ticket.Length);
            }
        }

        private Task<EchoResponse> Echo(NetworkClient client, string text, int seconds = 3, CancellationToken cancellation = default)
        {
            Task<EchoResponse> request = client.RequestAsync(GameNetRoutes.Echo, new EchoRequest { Text = text },
                TimeSpan.FromSeconds(seconds), cancellation).AsTask();
            operations.Add(request);
            return request;
        }

        private static async UniTask<Exception> OutcomeAsync(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        private static void AssertReleased(NetworkClient client)
        {
            NetworkMetricsSnapshot snapshot = client.GetMetrics();
            Assert.AreEqual(NetworkClientState.Idle, client.State);
            Assert.AreEqual(0, snapshot.OwnedBytes);
            Assert.AreEqual(0, snapshot.PendingCount);
            Assert.AreEqual(0, snapshot.DeadlineCount);
            Assert.AreEqual(0, snapshot.ReliableSendCount);
            Assert.AreEqual(0, snapshot.ReliableReceiveCount);
            Assert.AreEqual(0, snapshot.Kcp.NativeOwnedBytes);
            Assert.AreEqual(0, snapshot.Kcp.NativeReservedBytes);
            Assert.AreEqual(0, snapshot.Kcp.Waitsnd);
            Assert.AreEqual(0, snapshot.Kcp.InputQueueCount);
            Assert.AreEqual(0, snapshot.Kcp.OutputQueueCount);
            Assert.AreEqual(0, snapshot.Kcp.PlaintextQueueCount);
        }

        private async UniTask CheckControlAsync()
        {
            int before = controlEchoes;
            await WaitAsync(() => controlEchoes > before);
            Assert.AreEqual(NetworkClientState.Connected, control.State);
            Assert.IsTrue(callbacksOnMainThread);
        }

        [UnityTest]
        public IEnumerator DtlsCancellationPreservesCallerToken() => UniTask.ToCoroutine(async () => await VerifyHandshakeAsync(false, true));

        [UnityTest]
        public IEnumerator DtlsDeadlineReleasesThePublicClient() => UniTask.ToCoroutine(async () => await VerifyHandshakeAsync(false, false));

        [UnityTest]
        public IEnumerator HelloCancellationPreservesCallerToken() => UniTask.ToCoroutine(async () => await VerifyHandshakeAsync(true, true));

        [UnityTest]
        public IEnumerator HelloDeadlineReleasesThePublicClient() => UniTask.ToCoroutine(async () => await VerifyHandshakeAsync(true, false));

        private async UniTask VerifyHandshakeAsync(bool hello, bool cancel)
        {
            NetworkClient client = CreateClient(observed: true);
            using var cancellation = new CancellationTokenSource();
            if (hello)
            {
                await CommandAsync("arm-hello");
            }
            else
            {
                await CommandAsync("cut", enabled: true);
            }

            long started = NetworkClock.GetMilliseconds();
            Task connect = ConnectAsync(client, 0, cancellation.Token).AsTask();
            if (hello)
            {
                await WaitStatusAsync(status => status.FactoryEntered);
                await CommandAsync("release-hello");
                await WaitStatusAsync(status => status.ServerToClientFaultDrops > 0);
            }
            else
            {
                await WaitStatusAsync(status => status.ClientToServerFaultDrops > 0);
            }

            if (cancel)
            {
                cancellation.Cancel();
            }

            await WaitAsync(() => connect.IsCompleted, 12000);
            Exception error = await OutcomeAsync(connect);
            if (cancel)
            {
                Assert.IsInstanceOf<OperationCanceledException>(error);
                Assert.AreEqual(cancellation.Token, ((OperationCanceledException)error).CancellationToken);
            }
            else
            {
                Assert.IsFalse(cancellation.IsCancellationRequested);
                Assert.IsTrue(error is NetworkException || error is OperationCanceledException);
                if (error is OperationCanceledException timedOut)
                {
                    Assert.AreNotEqual(cancellation.Token, timedOut.CancellationToken);
                }
                Assert.GreaterOrEqual(NetworkClock.GetMilliseconds() - started, hello ? 4500 : 9500);
            }

            Assert.AreEqual(1, changes.Count(change => change.State == NetworkClientState.Faulted));
            await client.CloseAsync();
            AssertReleased(client);
            await CommandAsync("revoke");
            await CheckControlAsync();
        }

        [UnityTest]
        public IEnumerator ThreeSecondCutRecoversTheOriginalRpcOnce() => UniTask.ToCoroutine(async () =>
        {
            NetworkClient client = CreateClient(observed: true);
            await ConnectAsync(client, 0);
            await CommandAsync("cut", enabled: true);
            Task<EchoResponse> request = Echo(client, "recover", 8);
            await WaitStatusAsync(status => status.ClientToServerFaultDrops > 0);
            await UniTask.Delay(3000, ignoreTimeScale: true);
            Assert.IsFalse(request.IsCompleted);
            Assert.AreEqual(NetworkClientState.Connected, client.State);
            await CommandAsync("cut", enabled: false);
            Assert.AreEqual("recover", (await request).Text);
            Assert.AreEqual(1, (await CommandAsync("status")).HandlerCalls);
            Assert.IsFalse(changes.Any(change => change.State == NetworkClientState.Faulted));
            await CheckControlAsync();
        });

        [UnityTest]
        public IEnumerator PermanentCutFaultsThroughHeartbeatBeforeRpcDeadline() => UniTask.ToCoroutine(async () =>
        {
            NetworkClient client = CreateClient(observed: true);
            await ConnectAsync(client, 0);
            await CommandAsync("cut", enabled: true);
            long started = NetworkClock.GetMilliseconds();
            Task<EchoResponse> request = Echo(client, "blackhole", 30);
            await WaitAsync(() => request.IsCompleted, 26000);
            Exception error = await OutcomeAsync(request);
            Assert.IsInstanceOf<NetworkException>(error);
            Assert.IsNotInstanceOf<NetworkRequestTimeoutException>(error);
            Assert.IsNotInstanceOf<NetworkConnectionClosedException>(error);
            Assert.AreEqual("Application heartbeat timed out.", error.Message);
            Assert.Less(NetworkClock.GetMilliseconds() - started, 26000);
            await WaitStatusAsync(status => status.SessionCompleted, 26000);
            Assert.AreEqual(1, changes.Count(change => change.State == NetworkClientState.Faulted));
            Assert.AreSame(error, changes.Single(change => change.State == NetworkClientState.Faulted).Error);
            await client.CloseAsync();
            AssertReleased(client);
            await CheckControlAsync();
        });

        [UnityTest]
        public IEnumerator SameClientReconnectIgnoresDelayedOldUdpAndState() => UniTask.ToCoroutine(async () =>
        {
            NetworkClient client = CreateClient(observed: true);
            await ConnectAsync(client, 0);
            await CommandAsync("hold", enabled: true);
            Task<EchoResponse> old = Echo(client, "old-generation", 1);
            await WaitStatusAsync(status => status.HeldPackets > 0);
            await WaitAsync(() => old.IsCompleted);
            Assert.IsInstanceOf<NetworkRequestTimeoutException>(await OutcomeAsync(old));
            await client.CloseAsync();
            AssertReleased(client);
            await CommandAsync("revoke");
            int boundary = changes.Count;
            await ConnectAsync(client, 2);
            Assert.AreEqual(2, client.Generation);
            Assert.Greater((await CommandAsync("status")).HeldPackets, 0);
            await CommandAsync("hold", enabled: false);
            await WaitStatusAsync(status => status.QueuePackets == 0 && status.ReleasedHeldPackets == status.HeldPackets);
            Assert.AreEqual("new-generation", (await Echo(client, "new-generation")).Text);
            await CheckControlAsync();
            Assert.IsTrue(changes.Skip(boundary).All(change => change.Generation == 2));
            Assert.IsFalse(changes.Skip(boundary).Any(change => change.State == NetworkClientState.Faulted));
            Assert.AreEqual(NetworkClientState.Connected, client.State);
            Assert.AreEqual(1, client.GetMetrics().Rpc.SuccessfulResponses);
        });

        [UnityTest]
        public IEnumerator MainThreadPauseKeepsIoAliveAndDeliversReservedResponsesInOrder() => UniTask.ToCoroutine(async () =>
        {
            // A small admission limit makes the ownership boundary observable without flooding the listener.
            NetworkClient client = CreateClient(pendingCount: 3, observed: true);
            await ConnectAsync(client, 0);
            NetworkMetricsSnapshot before = client.GetMetrics();
            var delivered = new List<string>();
            async Task RecordAsync(Task<EchoResponse> response)
            {
                EchoResponse value = await response;
                callbacksOnMainThread &= PlayerLoopHelper.IsMainThread;
                delivered.Add(value.Text);
            }

            Task first = RecordAsync(Echo(client, "one", 2));
            Task second = RecordAsync(Echo(client, "two", 2));
            Task third = RecordAsync(Echo(client, "three", 2));
            Thread.Sleep(6000);
            NetworkMetricsSnapshot paused = client.GetMetrics();
            Assert.AreEqual(0, delivered.Count);
            Assert.AreEqual(3, paused.PendingCount);
            Assert.AreEqual(0, paused.DeadlineCount, "Responses arrived before their deadlines while the main thread was paused.");
            Assert.AreEqual(3, paused.ReliableReceiveCount);
            Assert.Greater(paused.Kcp.ReceivedUdpPackets, 0);
            Assert.Greater(paused.SentFrames - before.SentFrames, 3, "The paused client must also send control frames on its IO worker.");
            Assert.Greater((paused.ReceivedApplicationBytes - before.ReceivedApplicationBytes) -
                (paused.ReceivedBusinessBodyBytes - before.ReceivedBusinessBodyBytes), 3 * 32,
                "Inbound control traffic must continue in addition to the three response headers.");
            Assert.AreEqual(NetworkClientState.Connected, client.State);
            Exception overflow = await OutcomeAsync(Echo(client, "overflow"));
            Assert.IsInstanceOf<NetworkBackpressureException>(overflow);
            await Task.WhenAll(first, second, third);
            CollectionAssert.AreEqual(new[] { "one", "two", "three" }, delivered);
            Assert.AreEqual(0, client.GetMetrics().PendingCount);
            Assert.AreEqual(0, client.GetMetrics().Memory.PendingReservedBytes);
            Assert.AreEqual(3, client.GetMetrics().Rpc.SuccessfulResponses);
            Assert.Greater(client.GetMetrics().Rpc.MainThreadWait.MaximumMilliseconds, 2000);
            Assert.IsFalse(changes.Any(change => change.State == NetworkClientState.Faulted));
            await CheckControlAsync();
        });

        [UnityTest]
        public IEnumerator CancelReservedResponseWaitsForMainThreadAndKeepsConnectionUsable() => UniTask.ToCoroutine(async () =>
        {
            NetworkClient client = CreateClient(observed: true);
            await ConnectAsync(client, 0);
            using var cancellation = new CancellationTokenSource();
            Task<EchoResponse> request = Echo(client, "reserved-cancel", 2, cancellation.Token);
            Thread.Sleep(1000);
            Assert.AreEqual(1, client.GetMetrics().ReliableReceiveCount);
            Assert.AreEqual(0, client.GetMetrics().DeadlineCount);
            // Cancel on a worker, while the main thread is still deliberately not yielding to Pump.
            Task cancel = Task.Run(() => cancellation.Cancel());
            long cancelDeadline = NetworkClock.GetMilliseconds() + 2000;
            while (!cancel.IsCompleted)
            {
                Assert.Less(NetworkClock.GetMilliseconds(), cancelDeadline, "Worker cancellation failed to return while Pump was paused.");
                Thread.Sleep(1);
            }

            await cancel;
            Assert.IsFalse(request.IsCompleted);
            Assert.AreEqual(1, client.GetMetrics().PendingCount);
            Exception error = await OutcomeAsync(request);
            Assert.IsInstanceOf<OperationCanceledException>(error);
            Assert.AreEqual(cancellation.Token, ((OperationCanceledException)error).CancellationToken);
            Assert.IsTrue(PlayerLoopHelper.IsMainThread);
            await WaitAsync(() => client.GetMetrics().ReliableReceiveCount == 0);
            Assert.AreEqual(0, client.GetMetrics().PendingCount);
            Assert.AreEqual(0, client.GetMetrics().Rpc.SuccessfulResponses);
            Assert.AreEqual("after-cancel", (await Echo(client, "after-cancel")).Text);
            await CheckControlAsync();
        });

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Task cleanup = CleanupAsync().AsTask();
            while (!cleanup.IsCompleted)
            {
                yield return null;
            }

            cleanup.GetAwaiter().GetResult();
        }

        private async UniTask CleanupAsync()
        {
            if (!optedIn)
            {
                return;
            }

            var errors = new List<string>();
            async UniTask AttemptAsync(Func<UniTask> action)
            {
                try
                {
                    await action();
                }
                catch (Exception error)
                {
                    errors.Add(error.GetType().FullName);
                }
            }

            controlStop?.Cancel();
            if (controlTask != null)
            {
                await AttemptAsync(async () => await controlTask.AsUniTask().Timeout(TimeSpan.FromSeconds(5)));
                controlTask = null;
            }

            foreach (NetworkClient client in clients)
            {
                await AttemptAsync(async () =>
                {
                    await client.CloseAsync().Timeout(TimeSpan.FromSeconds(8));
                    AssertReleased(client);
                });
                await AttemptAsync(async () => await client.DisposeAsync());
            }

            foreach (Task operation in operations)
            {
                await AttemptAsync(async () => await OutcomeAsync(operation).Timeout(TimeSpan.FromSeconds(2)));
            }

            foreach (IDisposable subscription in subscriptions)
            {
                subscription.Dispose();
            }

            await AttemptAsync(async () => await GameRuntime.ShutdownAsync().Timeout(TimeSpan.FromSeconds(10)));
            if (runtime != null)
            {
                await AttemptAsync(() =>
                {
                    Assert.AreEqual(0, runtime.Memory.OwnedBytes);
                    return UniTask.CompletedTask;
                });
            }

            if (process != null)
            {
                await AttemptAsync(async () =>
                {
                    HostReply final = await CommandAsync("stop");
                    Assert.IsTrue(final.ResourcesReleased && final.NormalSessionsClosed && final.AccountingValid);
                    await WaitAsync(() => process.HasExited, 10000, checkProcess: false);
                    Assert.AreEqual(0, process.ExitCode);
                });
                if (!process.HasExited)
                {
                    process.Kill();
                    await AttemptAsync(async () => await WaitAsync(() => process.HasExited, 5000, checkProcess: false));
                }

                process.Dispose();
                process = null;
            }

            controlStop?.Dispose();
            controlStop = null;
            control = null;
            runtime = null;
            if (directory != null)
            {
                await AttemptAsync(() =>
                {
                    Directory.Delete(directory, true);
                    return UniTask.CompletedTask;
                });
                directory = null;
            }

            Assert.IsTrue(callbacksOnMainThread);
            Assert.IsEmpty(errors, "Cleanup errors (safe types only): " + string.Join(", ", errors));
            optedIn = false;
        }
    }
}
#endif
