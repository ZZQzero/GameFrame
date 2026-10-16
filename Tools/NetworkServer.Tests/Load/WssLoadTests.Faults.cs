using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Game.Protocol;
using Game.Protocol.Diagnostics;
using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed partial class WssLoadTests
    {
        private static readonly string[] FaultCases =
        {
            "dtls-loss", "dtls-cancel", "dtls-timeout", "hello-cancel", "hello-timeout",
            "outage-recover", "outage-timeout", "close-client-loss", "close-server-loss",
            "close-client-blackhole", "close-server-blackhole", "reconnect"
        };

        private sealed class FaultResult
        {
            public string Case { get; init; } = "";
            public bool ExpectationsMet { get; internal set; }
            public bool FaultPhaseCompleted { get; internal set; }
            public double FaultMilliseconds { get; internal set; }
            public double? ClientIoMilliseconds { get; internal set; }
            public double? ServerCompletionMilliseconds { get; internal set; }
            public int ControlEchoes { get; internal set; }
            public int ReconnectedEchoes { get; internal set; }
            public int RecoveredRpcDispatches { get; internal set; }
            public long NewGeneration { get; internal set; }
            public bool OldGenerationReleasedBeforeReconnect { get; internal set; }
            public bool OldServerReleasedBeforeReconnect { get; internal set; }
            public bool GrantRevoked { get; internal set; }
            public string? ConnectErrorType { get; internal set; }
            public string? ClientErrorType { get; internal set; }
            public ushort? ClientCloseReason { get; internal set; }
            public string? RequestErrorType { get; internal set; }
            public ushort? ServerCloseReason { get; internal set; }
            public string? ServerErrorType { get; internal set; }
            public int RequestTasks { get; internal set; }
            public int UnresolvedRequests { get; internal set; }
            public int CreatedSessions { get; internal set; }
            public int ClosedSessions { get; internal set; }
            public int UnexpectedSessionResults { get; internal set; }
            public bool ListenerStopped { get; internal set; }
            public bool WorkerStopped { get; internal set; }
            public bool ClientsReleased { get; internal set; }
            public long FinalClientOwnedBytes { get; internal set; }
            public long FinalServerOwnedBytes { get; internal set; }
            public KcpRelayReport? Relay { get; internal set; }
            public string? ErrorType { get; internal set; }
            public string? ErrorStack { get; internal set; }
            public List<string> CleanupErrorTypes { get; } = new();
            public bool ResourcesReleased => ClientsReleased && ListenerStopped && WorkerStopped &&
                FinalClientOwnedBytes == 0 && FinalServerOwnedBytes == 0 && UnresolvedRequests == 0 &&
                CreatedSessions == ClosedSessions && Relay is { Released: true, AccountingValid: true, QueueOverflowDrops: 0 };
            public bool Healthy => ExpectationsMet && FaultPhaseCompleted && ControlEchoes > 0 &&
                ReconnectedEchoes == 1 && OldGenerationReleasedBeforeReconnect && OldServerReleasedBeforeReconnect &&
                ResourcesReleased && Relay is { Healthy: true, FaultExercised: true } &&
                UnexpectedSessionResults == 0 && ErrorType == null && CleanupErrorTypes.Count == 0;
        }

        private sealed class FaultSession : ServerSession
        {
            private readonly Action<ServerCloseInfo> closed;
            internal FaultSession(Action<ServerCloseInfo> closed) => this.closed = closed;
            public override ValueTask ClosedAsync(ServerConnectionContext connection, ServerCloseInfo result)
            {
                closed(result);
                return ValueTask.CompletedTask;
            }
        }

        [Test]
        [Explicit("Full real-time fault matrix; run with Tools/network_load.py --transport kcp --scenario fault.")]
        public async Task KcpFaultMatrix()
        {
#if DEBUG
            Assert.Fail("Fault validation requires the Release native/test build.");
#endif
            string selected = TestContext.Parameters.Get("FaultCase", "all");
            Assert.That(selected == "all" || FaultCases.Contains(selected), Is.True);
            int repeat = int.Parse(TestContext.Parameters.Get("Repeat", "1"), CultureInfo.InvariantCulture);
            Assert.That(repeat, Is.InRange(1, 10));
            KcpTestLibrary.Load();
            var runs = new List<FaultResult>();
            for (int iteration = 0; iteration < repeat; iteration++)
            {
                foreach (string faultCase in selected == "all" ? FaultCases : new[] { selected })
                {
                    FaultResult result = await RunFaultAsync(faultCase);
                    runs.Add(result);
                    TestContext.Progress.WriteLine($"KCP fault {faultCase} {iteration + 1}/{repeat}: healthy={result.Healthy}, released={result.ResourcesReleased}");
                }
            }

            var report = new
            {
                SchemaVersion = 1, Transport = "kcp", Scenario = "kcp-fault", FaultCase = selected,
                Native = JsonSerializer.Deserialize<JsonElement>(TestContext.Parameters.Get("NativeProof", "null")),
                TimestampUtc = DateTimeOffset.UtcNow,
                Sdk = TestContext.Parameters.Get("Sdk", "unspecified"),
                Revision = TestContext.Parameters.Get("Revision", "unspecified"),
                WorktreeDirty = TestContext.Parameters.Get("WorktreeDirty", "unspecified"),
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Configuration = new
                {
                    Clients = "one affected generation + one continuously probed control; then explicit fresh generation",
                    PumpMilliseconds = 1, ControlIntervalMilliseconds = 500, OutageMilliseconds = 3000,
                    CloseLossMilliseconds = 300, PendingRpcTimeoutSeconds = 30,
                    Scope = "Same-process real UDP/DTLS; default transport/Hello/heartbeat/close deadlines; opaque per-direction UDP cuts; no application retry",
                    Reconnect = "Real ConnectionContext generations on a shared worker/listener; Unity public NetworkClient lifecycle is a separate validation layer",
                    ServerNativeOwnedBytesMeasured = false
                },
                Runs = runs
            };
            string json = JsonSerializer.Serialize(report, JsonOptions);
            TestContext.Progress.WriteLine(json);
            string path = TestContext.Parameters.Get("JsonPath", "");
            if (path.Length != 0)
            {
                await File.WriteAllTextAsync(path, json);
            }

            Assert.That(runs.All(run => run.Healthy), Is.True, "Inspect the fault report; expected network failures are checked explicitly.");
        }

        [TestCase("dtls-loss")]
        [TestCase("dtls-cancel")]
        [TestCase("hello-cancel")]
        [TestCase("outage-recover")]
        [TestCase("close-client-loss")]
        [TestCase("close-server-loss")]
        [TestCase("reconnect")]
        public async Task KcpFaultBoundaryReleasesAndKeepsOtherConnectionHealthy(string faultCase)
        {
            KcpTestLibrary.Load();
            FaultResult result = await RunFaultAsync(faultCase);
            Assert.That(result.Healthy, Is.True, JsonSerializer.Serialize(result, JsonOptions));
        }

        private static bool FaultClientReleased(Client client)
        {
            NetworkMetricsSnapshot metrics = client.Context.Metrics();
            KcpMetricsSnapshot native = metrics.Kcp;
            return client.Context.ConnectTask.IsCompleted && client.Context.IoTask.IsCompletedSuccessfully &&
                client.Context.CloseTask.IsCompletedSuccessfully && metrics.OwnedBytes == 0 &&
                metrics.PendingCount == 0 && metrics.DeadlineCount == 0 &&
                metrics.ReliableSendCount == 0 && metrics.ReliableReceiveCount == 0 &&
                native.NativeOwnedBytes == 0 && native.NativeReservedBytes == 0 && native.Waitsnd == 0 &&
                native.InputQueueCount == 0 && native.OutputQueueCount == 0 && native.PlaintextQueueCount == 0;
        }

        private static async Task<Exception?> FaultOutcomeAsync(Task task)
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

        private static void RequireFault(bool condition, string invariant)
        {
            if (!condition)
            {
                throw new InvalidOperationException(invariant);
            }
        }

        private static async Task<FaultResult> RunFaultAsync(string faultCase)
        {
            var result = new FaultResult { Case = faultCase };
            var clients = new List<Client>(3);
            var requests = new List<Task<EchoResponse>>(4);
            var credentials = new List<NetworkConnectOptions>(3);
            var sessions = new ConcurrentDictionary<ulong, ServerConnectionContext>();
            var closeResults = new ConcurrentDictionary<ulong, ServerCloseInfo>();
            var handlerCalls = new ConcurrentDictionary<(ulong Grant, string Text), int>();
            var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var factoryRelease = new ManualResetEventSlim(false);
            var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var connectCancellation = new CancellationTokenSource();
            ulong targetGrant = 0;
            bool holdHandler = false;
            var application = new ServerApplication(GameNetProtocol.Catalog, connection =>
            {
                sessions[connection.GrantId] = connection;
                if (faultCase.StartsWith("hello-", StringComparison.Ordinal) && connection.GrantId == targetGrant)
                {
                    factoryEntered.TrySetResult();
                    // Deliberately pause publication, without blocking the listener/native owner.
                    factoryRelease.Wait();
                }

                return new FaultSession(info => closeResults[connection.GrantId] = info);
            });
            application.Rpc(DiagnosticsRoutes.Echo, async (connection, request, token) =>
            {
                handlerCalls.AddOrUpdate((connection.GrantId, request.Text), 1, (_, count) => count + 1);
                if (connection.GrantId == targetGrant && Volatile.Read(ref holdHandler))
                {
                    handlerEntered.TrySetResult();
                    await handlerRelease.Task.WaitAsync(token);
                }

                return new EchoResponse { Text = request.Text };
            });
            foreach (NetRoute route in GameNetProtocol.Catalog.Routes)
            {
                if (route.Kind != NetRouteKind.Push && route != DiagnosticsRoutes.Echo)
                {
                    application.Reject(route);
                }
            }

            var memory = new FrameMemoryBudget(3L * new NetworkClientOptions().MaxOwnedBytes);
            KcpListenerHost? host = null;
            KcpWorker? worker = null;
            KcpLoadRelay? relay = null;
            Client? control = null;
            Task<EchoResponse>? controlRequest = null;
            long nextControl = 0;
            bool probeControl = true;
            long closeStarted = 0;
            Client? closingClient = null;
            ServerConnectionContext? closingServer = null;

            void TickControl()
            {
                if (control == null || !control.Context.ConnectTask.IsCompletedSuccessfully || !probeControl)
                {
                    return;
                }

                RequireFault(control.StateError == null && control.Context.State == NetworkClientState.Connected,
                    "The unaffected control connection failed.");
                if (controlRequest is { IsCompleted: true })
                {
                    RequireFault(controlRequest.GetAwaiter().GetResult().Text == "control", "Control echo mismatch.");
                    result.ControlEchoes++;
                    controlRequest = null;
                }

                if (controlRequest == null && NetworkClock.GetMilliseconds() >= nextControl)
                {
                    controlRequest = control.Context.Request(DiagnosticsRoutes.Echo, new EchoRequest { Text = "control" },
                        TimeSpan.FromSeconds(3), default);
                    nextControl = NetworkClock.GetMilliseconds() + 500;
                }
            }

            async Task WaitAsync(Func<bool> completed, int milliseconds = 6000)
            {
                long started = Stopwatch.GetTimestamp();
                while (true)
                {
                    Pump(clients);
                    TickControl();
                    if (closeStarted != 0)
                    {
                        double elapsed = Stopwatch.GetElapsedTime(closeStarted).TotalMilliseconds;
                        if (closingClient!.Context.IoTask.IsCompleted && result.ClientIoMilliseconds == null)
                        {
                            result.ClientIoMilliseconds = elapsed;
                        }

                        if (closingServer!.Completion.IsCompleted && result.ServerCompletionMilliseconds == null)
                        {
                            result.ServerCompletionMilliseconds = elapsed;
                        }
                    }

                    if (completed())
                    {
                        return;
                    }

                    if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds)
                    {
                        throw new TimeoutException("Fault harness observation deadline exceeded.");
                    }

                    await Task.Delay(1);
                }
            }

            async Task PauseAsync(int milliseconds)
            {
                long until = NetworkClock.GetMilliseconds() + milliseconds;
                await WaitAsync(() => NetworkClock.GetMilliseconds() >= until, milliseconds + 3000);
            }

            async Task<(Client Client, ulong Grant)> StartAsync(int index, long generation, CancellationToken token)
            {
                byte[] identity = RandomNumberGenerator.GetBytes(16);
                byte[] psk = RandomNumberGenerator.GetBytes(32);
                byte[] ticket = RandomNumberGenerator.GetBytes(32);
                NetworkConnectOptions? connect = null;
                try
                {
                    long deadline = NetworkClock.GetMilliseconds() + 30000;
                    ulong grant = await host!.RegisterGrantAsync((uint)index + 1, identity, psk, ticket);
                    connect = NetworkConnectOptions.Kcp(relay!.Endpoint(index), ticket, (uint)index + 1,
                        identity, psk, deadline, unreliableLimit: 0);
                    credentials.Add(connect);
                    if (index == 0)
                    {
                        targetGrant = grant;
                    }

                    var context = new ConnectionContext(generation,
                        new NetworkClientOptions { TrustedUdpEndpoints = new[] { connect.Endpoint } },
                        GameNetProtocol.Catalog, connect, memory, token, kcpWorker: worker);
                    var client = new Client(context, "fault", 1, 0);
                    context.StateChanged = (_, error) =>
                    {
                        if (error != null)
                        {
                            client.StateError = error;
                        }
                    };
                    clients.Add(client);
                    context.Start();
                    return (client, grant);
                }
                finally
                {
                    if (connect == null)
                    {
                        CryptographicOperations.ZeroMemory(identity);
                        CryptographicOperations.ZeroMemory(psk);
                        CryptographicOperations.ZeroMemory(ticket);
                    }
                }
            }

            Task<EchoResponse> Request(Client client, string text, int seconds = 30)
            {
                Task<EchoResponse> request = client.Context.Request(DiagnosticsRoutes.Echo, new EchoRequest { Text = text },
                    TimeSpan.FromSeconds(seconds), default);
                requests.Add(request);
                return request;
            }

            try
            {
                host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), application);
                await host.Started.WaitAsync(TimeSpan.FromSeconds(5));
                worker = new KcpWorker(3);
                relay = new KcpLoadRelay(host.LocalEndpoint!, 3, "direct", 1);
                (control, _) = await StartAsync(1, 1, default);
                await WaitAsync(() => control.Context.ConnectTask.IsCompleted);
                await control.Context.ConnectTask;
                await WaitAsync(() => result.ControlEchoes > 0);

                bool dtls = faultCase.StartsWith("dtls-", StringComparison.Ordinal);
                bool hello = faultCase.StartsWith("hello-", StringComparison.Ordinal);
                bool handshake = dtls || hello;
                if (dtls && faultCase != "dtls-loss")
                {
                    relay.SetBlackhole(0, true);
                }
                else if (faultCase == "dtls-loss")
                {
                    relay.DropNext(0, 0, 1);
                    relay.DropNext(0, 1, 1);
                }

                long faultStarted = Stopwatch.GetTimestamp();
                var (affected, grant) = await StartAsync(0, 1, connectCancellation.Token);
                if (hello)
                {
                    await WaitAsync(() => factoryEntered.Task.IsCompleted);
                    relay.SetBlackhole(0, true, 1);
                    factoryRelease.Set();
                }

                if (handshake && faultCase != "dtls-loss")
                {
                    await WaitAsync(() => hello ? relay.Snapshot().ServerToClientFaultDrops > 0 :
                        relay.Snapshot().ClientToServerFaultDrops > 0);
                    bool cancel = faultCase.EndsWith("cancel", StringComparison.Ordinal);
                    if (cancel)
                    {
                        connectCancellation.Cancel();
                    }

                    await WaitAsync(() => affected.Context.ConnectTask.IsCompleted, 13000);
                    Exception? connectError = await FaultOutcomeAsync(affected.Context.ConnectTask);
                    result.ConnectErrorType = connectError?.GetType().FullName;
                    if (cancel)
                    {
                        RequireFault(connectError is OperationCanceledException canceled &&
                            canceled.CancellationToken == connectCancellation.Token, "Connect must retain the caller cancellation token.");
                    }
                    else
                    {
                        RequireFault(!connectCancellation.IsCancellationRequested &&
                            (connectError is OperationCanceledException || connectError is NetworkException) &&
                            Stopwatch.GetElapsedTime(faultStarted).TotalSeconds >= (hello ? 4.5 : 9),
                            "Connect must fail at the unchanged handshake/Hello deadline.");
                    }

                    affected.Context.BeginClose();
                    await WaitAsync(() => affected.Context.CloseTask.IsCompleted && affected.Context.IoTask.IsCompleted);
                    await host.RevokeGrantAsync(grant);
                    result.GrantRevoked = true;
                    await WaitAsync(() => !sessions.TryGetValue(grant, out var connection) || connection.Completion.IsCompleted);
                }
                else
                {
                    await WaitAsync(() => affected.Context.ConnectTask.IsCompleted, 11000);
                    await affected.Context.ConnectTask;
                    Task<EchoResponse> initial = Request(affected, "before");
                    await WaitAsync(() => initial.IsCompleted);
                    RequireFault((await initial).Text == "before", "Initial echo mismatch.");
                    if (faultCase != "dtls-loss")
                    {
                        faultStarted = Stopwatch.GetTimestamp();
                    }
                    ServerConnectionContext server = sessions[grant];
                    if (faultCase == "dtls-loss")
                    {
                        RequireFault(relay.Snapshot().ClientToServerFaultDrops == 1 &&
                            relay.Snapshot().ServerToClientFaultDrops == 1, "Both handshake directions must lose a packet.");
                        affected.Context.BeginClose();
                    }
                    else if (faultCase == "reconnect")
                    {
                        relay.SetHold(0, 1, true);
                        Task<EchoResponse> stale = Request(affected, "old-generation", 1);
                        await WaitAsync(() => relay.Snapshot().HeldPackets > 0);
                        await WaitAsync(() => stale.IsCompleted);
                        Exception? error = await FaultOutcomeAsync(stale);
                        result.RequestErrorType = error?.GetType().FullName;
                        RequireFault(error is NetworkRequestTimeoutException, "Held old response must time out exactly once.");
                        affected.Context.BeginClose();
                    }
                    else if (faultCase.StartsWith("outage-", StringComparison.Ordinal))
                    {
                        relay.SetBlackhole(0, true);
                        Task<EchoResponse> inFlight = Request(affected, "outage", faultCase == "outage-recover" ? 8 : 30);
                        await WaitAsync(() => relay.Snapshot().ClientToServerFaultDrops > 0);
                        if (faultCase == "outage-recover")
                        {
                            await PauseAsync(3000);
                            RequireFault(!inFlight.IsCompleted && affected.StateError == null, "A recoverable cut must leave the RPC pending.");
                            relay.SetBlackhole(0, false);
                            await WaitAsync(() => inFlight.IsCompleted, 8000);
                            RequireFault((await inFlight).Text == "outage" && affected.StateError == null,
                                "KCP must recover the existing RPC without an application retry.");
                            result.RecoveredRpcDispatches = handlerCalls[(grant, "outage")];
                            RequireFault(result.RecoveredRpcDispatches == 1, "The recovered RPC must execute once on the server.");
                        }
                        else
                        {
                            await WaitAsync(() => affected.Context.IoTask.IsCompleted && server.Completion.IsCompleted, 26000);
                            await WaitAsync(() => inFlight.IsCompleted);
                            Exception? error = await FaultOutcomeAsync(inFlight);
                            result.RequestErrorType = error?.GetType().FullName;
                            RequireFault(error is NetworkException && error is not NetworkRequestTimeoutException &&
                                error is not NetworkConnectionClosedException, "Permanent outage must fault the connection and pending RPC.");
                            RequireFault(affected.StateError is NetworkException && (await server.Completion).Error is NetworkException,
                                "Both peers must observe their own transport/heartbeat failure.");
                            RequireFault(affected.StateError!.Message == "Application heartbeat timed out." &&
                                (await server.Completion).Error!.Message == "Child heartbeat deadline elapsed.",
                                "A permanent outage must reach the existing heartbeat deadlines, not an unrelated failure.");
                        }

                        affected.Context.BeginClose();
                    }
                    else
                    {
                        bool clientClose = faultCase.StartsWith("close-client", StringComparison.Ordinal);
                        bool permanent = faultCase.EndsWith("blackhole", StringComparison.Ordinal);
                        Volatile.Write(ref holdHandler, true);
                        Task<EchoResponse> inFlight = Request(affected, "close");
                        await WaitAsync(() => handlerEntered.Task.IsCompleted);
                        relay.SetBlackhole(0, true);
                        closingClient = affected;
                        closingServer = server;
                        closeStarted = Stopwatch.GetTimestamp();
                        if (clientClose)
                        {
                            affected.Context.BeginClose();
                            handlerRelease.TrySetResult();
                        }
                        else
                        {
                            server.RequestClose(3);
                        }

                        await WaitAsync(() => relay.Snapshot().DroppedPackets > 0);
                        if (!permanent)
                        {
                            await PauseAsync(300);
                            relay.SetBlackhole(0, false);
                        }

                        await WaitAsync(() => affected.Context.IoTask.IsCompleted && server.Completion.IsCompleted, 26000);
                        affected.Context.BeginClose();
                        await WaitAsync(() => inFlight.IsCompleted);
                        Exception? error = await FaultOutcomeAsync(inFlight);
                        result.RequestErrorType = error?.GetType().FullName;
                        ServerCloseInfo info = await server.Completion;
                        RequireFault(clientClose ? result.ClientIoMilliseconds < 5000 : result.ServerCompletionMilliseconds < 5000,
                            "The closing initiator must release promptly even if the peer cannot receive Close.");
                        if (clientClose)
                        {
                            RequireFault(error?.GetType() == typeof(NetworkException) && affected.StateError == null,
                                "Local close must finish the pending RPC without reporting a client transport fault.");
                            RequireFault(permanent ? info.Error is NetworkException : info.Reason == 1 && info.Error == null,
                                "The server must receive Close or independently time out when all close traffic is lost.");
                        }
                        else
                        {
                            RequireFault(info.Reason == 3 && info.Error == null, "Server must preserve its own requested close reason.");
                            RequireFault(permanent ? error is NetworkException && error is not NetworkConnectionClosedException :
                                error is NetworkConnectionClosedException { ReasonCode: 3 },
                                "Remote Close reason is available only if it actually reaches the client.");
                        }
                    }

                    await WaitAsync(() => affected.Context.CloseTask.IsCompleted && affected.Context.IoTask.IsCompleted);
                    if (faultCase != "reconnect")
                    {
                        await WaitAsync(() => server.Completion.IsCompleted, 26000);
                    }
                    else
                    {
                        // All server ACKs are held too, so local best-effort Close
                        // cannot promise remote receipt. The test authority explicitly
                        // revokes the old grant before admitting the fresh generation.
                        await host.RevokeGrantAsync(grant);
                        result.GrantRevoked = true;
                        await WaitAsync(() => server.Completion.IsCompleted);
                        ServerCloseInfo revoked = await server.Completion;
                        RequireFault(revoked.Error is NetworkException || (revoked.Reason == 1 && revoked.Error == null),
                            "The old server must complete after explicit revocation or received Close.");
                    }
                }

                result.FaultMilliseconds = Stopwatch.GetElapsedTime(faultStarted).TotalMilliseconds;
                result.FaultPhaseCompleted = true;
                result.ClientErrorType = affected.StateError?.GetType().FullName;
                result.ClientCloseReason = (affected.StateError as NetworkConnectionClosedException)?.ReasonCode;
                if (sessions.TryGetValue(grant, out var oldServer) && oldServer.Completion.IsCompleted)
                {
                    ServerCloseInfo info = await oldServer.Completion;
                    result.ServerCloseReason = info.Reason;
                    result.ServerErrorType = info.Error?.GetType().FullName;
                }

                RequireFault(FaultClientReleased(affected), "Old context must release IO, pending, deadlines, native and memory before reconnect.");
                result.OldGenerationReleasedBeforeReconnect = true;
                RequireFault(!sessions.TryGetValue(grant, out var previous) || previous.Completion.IsCompletedSuccessfully,
                    "The old published server session must finish before the new grant is used.");
                result.OldServerReleasedBeforeReconnect = true;
                relay.SetBlackhole(0, false);
                var (next, _) = await StartAsync(2, 2, default);
                await WaitAsync(() => next.Context.ConnectTask.IsCompleted);
                await next.Context.ConnectTask;
                if (faultCase == "reconnect")
                {
                    RequireFault(relay.Snapshot().HeldPackets > 0, "Old datagrams must still be held after the fresh generation connects.");
                    relay.SetHold(0, 1, false);
                    await WaitAsync(() => relay.Snapshot().QueuePackets == 0);
                }

                Task<EchoResponse> after = Request(next, "new-generation");
                await WaitAsync(() => after.IsCompleted);
                RequireFault((await after).Text == "new-generation" && next.StateError == null &&
                    next.Context.Generation == 2 && FaultClientReleased(affected), "Late old traffic must not affect the new generation.");
                result.NewGeneration = next.Context.Generation;
                result.ReconnectedEchoes = 1;
                int controlEchoesBeforeFinalProbe = result.ControlEchoes;
                nextControl = 0;
                await WaitAsync(() => result.ControlEchoes > controlEchoesBeforeFinalProbe);
                result.ExpectationsMet = true;
            }
            catch (Exception error)
            {
                result.ErrorType = error.GetType().FullName;
                result.ErrorStack = error.StackTrace;
            }
            finally
            {
                probeControl = false;
                factoryRelease.Set();
                handlerRelease.TrySetResult();
                async Task CleanupAsync(Func<Task> action)
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception error)
                    {
                        result.CleanupErrorTypes.Add(error.GetType().FullName!);
                    }
                }

                // Keep the old-path fault active until all assertions have run; only final cleanup releases it.
                if (relay != null)
                {
                    relay.SetBlackhole(0, false);
                    relay.SetHold(0, 1, false);
                }

                foreach (Client client in clients)
                {
                    await CleanupAsync(() =>
                    {
                        client.Context.BeginClose();
                        return Task.CompletedTask;
                    });
                }

                await CleanupAsync(() => WaitAsync(() => clients.All(client => client.Context.CloseTask.IsCompleted && client.Context.IoTask.IsCompleted), 10000));
                foreach (Client client in clients)
                {
                    await CleanupAsync(async () =>
                    {
                        await client.Context.IoTask.WaitAsync(TimeSpan.FromSeconds(10));
                        await client.Context.CloseTask.WaitAsync(TimeSpan.FromSeconds(10));
                        await FaultOutcomeAsync(client.Context.ConnectTask).WaitAsync(TimeSpan.FromSeconds(1));
                    });
                }

                if (controlRequest != null)
                {
                    await CleanupAsync(async () =>
                    {
                        await FaultOutcomeAsync(controlRequest).WaitAsync(TimeSpan.FromSeconds(1));
                    });
                }
                foreach (Task<EchoResponse> request in requests)
                {
                    await CleanupAsync(async () =>
                    {
                        await FaultOutcomeAsync(request).WaitAsync(TimeSpan.FromSeconds(1));
                    });
                }
                if (worker != null)
                {
                    await CleanupAsync(async () =>
                    {
                        await worker.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        result.WorkerStopped = true;
                    });
                }
                if (relay != null)
                {
                    await CleanupAsync(async () =>
                    {
                        await relay.DisposeAsync();
                    });
                    result.Relay = relay.Snapshot();
                }
                if (host != null)
                {
                    await CleanupAsync(async () =>
                    {
                        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                        result.ListenerStopped = true;
                    });
                    result.FinalServerOwnedBytes = host.OwnedBytes;
                }

                result.RequestTasks = requests.Count;
                result.UnresolvedRequests = requests.Count(request => !request.IsCompleted);
                result.CreatedSessions = sessions.Count;
                result.ClosedSessions = closeResults.Count;
                result.UnexpectedSessionResults = closeResults.Count(pair => pair.Key != targetGrant &&
                    (pair.Value.Reason != 1 || pair.Value.Error != null));
                result.UnexpectedSessionResults += clients.Count(client =>
                    (ReferenceEquals(client, control) || client.Context.Generation == 2) && client.StateError != null);
                if (closeResults.TryGetValue(targetGrant, out ServerCloseInfo finalOld))
                {
                    result.ServerCloseReason = finalOld.Reason;
                    result.ServerErrorType = finalOld.Error?.GetType().FullName;
                    if ((faultCase == "dtls-loss" || faultCase == "outage-recover") &&
                        (finalOld.Reason != 1 || finalOld.Error != null))
                    {
                        result.UnexpectedSessionResults++;
                    }
                }

                result.ClientsReleased = clients.All(FaultClientReleased) && sessions.Values.All(connection => connection.Completion.IsCompletedSuccessfully);
                result.FinalClientOwnedBytes = memory.OwnedBytes;
                foreach (NetworkConnectOptions connect in credentials)
                {
                    ClearGrant(connect);
                }
            }

            return result;
        }
    }
}
