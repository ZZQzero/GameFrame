using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Game.Protocol;
using GameFrame.Network;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class KcpLoopbackTests
    {
        [OneTimeSetUp]
        public void LoadVerifiedLibrary()
        {
            string? path = Environment.GetEnvironmentVariable("GF_KCP_NATIVE_LIBRARY");
            if (string.IsNullOrEmpty(path))
            {
                Assert.Ignore("Set GF_KCP_NATIVE_LIBRARY to a verified host build to run native UDP integration.");
            }

            NativeLibrary.SetDllImportResolver(typeof(KcpNative).Assembly, (name, assembly, search) =>
                name == "gameframe_kcp" ? NativeLibrary.Load(path!) : IntPtr.Zero);
            KcpNative.ValidateAbi();
        }

        private static async Task<EchoResponse> Echo(ServerConnectionContext connection, EchoRequest request, CancellationToken token)
        {
            await Task.Delay((int)request.DelayMs, token);
            return new EchoResponse { Text = request.Text };
        }

        private static ServerApplication Application(
            Func<ServerConnectionContext, EchoRequest, CancellationToken, Task<EchoResponse>>? echo = null,
            Func<ServerConnectionContext, EchoRequest, CancellationToken, Task>? move = null,
            Func<ServerConnectionContext, ServerSession>? session = null)
        {
            var application = new ServerApplication(TransportTestProtocol.Catalog, session);
            application.Rpc(GameNetRoutes.Echo, echo ?? Echo);
            application.Notify(TransportTestProtocol.UnreliableSend, move ?? ((_, _, _) => Task.CompletedTask));
            application.Notify(TransportTestProtocol.ReliableSend, move ?? ((_, _, _) => Task.CompletedTask));
            foreach (NetRoute route in GameNetProtocol.Catalog.Routes)
            {
                if (route.Kind != NetRouteKind.Push && route != GameNetRoutes.Echo)
                {
                    application.Reject(route);
                }
            }
            return application;
        }

        private static async Task<(NetworkConnectOptions Connect, ulong Grant)> Grant(KcpListenerHost host, uint conv)
        {
            await host.Started.WaitAsync(TimeSpan.FromSeconds(5));
            byte[] identity = RandomNumberGenerator.GetBytes(16), psk = RandomNumberGenerator.GetBytes(32), ticket = RandomNumberGenerator.GetBytes(32);
            ulong id = await host.RegisterGrantAsync(conv, identity, psk, ticket);
            var uri = new Uri($"udp://{host.LocalEndpoint!.Address}:{host.LocalEndpoint.Port}");
            return (NetworkConnectOptions.Kcp(uri, ticket, conv, identity, psk, NetworkClock.GetMilliseconds() + 30000), id);
        }

        private static async Task PumpUntil(ConnectionContext context, Task task, int milliseconds = 5000)
        {
            long end = NetworkClock.GetMilliseconds() + milliseconds;
            while (!task.IsCompleted && NetworkClock.GetMilliseconds() < end)
            {
                for (int i = 0; i < 128 && context.PumpOne(); i++)
                {
                }

                await Task.Delay(2);
            }

            await task.WaitAsync(TimeSpan.FromSeconds(1));
        }

        private static async Task Close(ConnectionContext context)
        {
            context.BeginClose();
            await PumpUntil(context, context.CloseTask);
            await context.IoTask;
        }

        private static ConnectionContext Context(NetworkConnectOptions connect, FrameMemoryBudget memory, KcpWorker worker)
        {
            var options = new NetworkClientOptions { TrustedUdpEndpoints = new[] { connect.Endpoint } };
            var context = new ConnectionContext(1, options, TransportTestProtocol.Catalog, connect, memory, default, kcpWorker: worker);
            context.Start();
            return context;
        }

        private static Task OnListener(KcpListenerHost host, Action<IntPtr> command)
        {
            var method = typeof(KcpListenerHost).GetMethod("CommandAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            return (Task)method.Invoke(host, new object[] { command })!;
        }

        private sealed class CaptureSession : ServerSession
        {
            private readonly Channel<ServerConnectionContext> connected;
            internal int Closed;

            internal CaptureSession(Channel<ServerConnectionContext> connected)
            {
                this.connected = connected;
            }

            public override ValueTask ConnectedAsync(ServerConnectionContext connection)
            {
                Assert.That(Thread.CurrentThread.Name, Is.Not.EqualTo("GameFrame KCP Listener"));
                Assert.That(connected.Writer.TryWrite(connection), Is.True);
                return ValueTask.CompletedTask;
            }

            public override ValueTask ClosedAsync(ServerConnectionContext connection, ServerCloseInfo result)
            {
                Assert.That(Thread.CurrentThread.Name, Is.Not.EqualTo("GameFrame KCP Listener"));
                Interlocked.Increment(ref Closed);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class InitializingSession : ServerSession
        {
            internal readonly TaskCompletionSource<ServerConnectionContext> Entered =
                new TaskCompletionSource<ServerConnectionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly Channel<ServerMessageHeader> Admissions = Channel.CreateBounded<ServerMessageHeader>(4);
            internal int DeadlineChecks;
            internal int Closes;

            public override async ValueTask ConnectedAsync(ServerConnectionContext connection)
            {
                Entered.TrySetResult(connection);
                await Release.Task.WaitAsync(connection.Cancellation);
            }

            public override ServerAdmission Admit(ServerMessageHeader header)
            {
                Assert.That(Thread.CurrentThread.Name, Is.EqualTo("GameFrame KCP Listener"));
                Assert.That(Admissions.Writer.TryWrite(header), Is.True);
                return ServerAdmission.Allow;
            }

            public override ushort? CheckDeadline(long nowMilliseconds)
            {
                Interlocked.Increment(ref DeadlineChecks);
                return null;
            }

            public override ValueTask ClosedAsync(ServerConnectionContext connection, ServerCloseInfo result)
            {
                Interlocked.Increment(ref Closes);
                return ValueTask.CompletedTask;
            }
        }

        [Test]
        public async Task HeartbeatAndAdmissionContinueDuringSlowInitializationAndDispatchStaysOrdered()
        {
            var session = new InitializingSession();
            var calls = new ConcurrentQueue<string>();
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(
                (_, request, _) =>
                {
                    calls.Enqueue(request.Text);
                    return Task.FromResult(new EchoResponse { Text = request.Text });
                }, session: _ => session));
            var grant = await Grant(host, 891);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext client = Context(grant.Connect, memory, worker);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                await PumpUntil(client, client.ConnectTask);
                ServerConnectionContext server = await session.Entered.Task.WaitAsync(timeout.Token);
                Task<EchoResponse> first = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = "first" }, TimeSpan.FromSeconds(30), default);
                Task<EchoResponse> second = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = "second" }, TimeSpan.FromSeconds(30), default);
                ServerMessageHeader one = await session.Admissions.Reader.ReadAsync(timeout.Token);
                ServerMessageHeader two = await session.Admissions.Reader.ReadAsync(timeout.Token);
                Assert.That(two.CorrelationId, Is.GreaterThan(one.CorrelationId));
                // Cross the first Ping + Pong deadline with business frames ahead of control traffic.
                // Client application Pump stays paused while both transport owners keep running.
                await Task.Delay(21500, timeout.Token);
                Assert.That(server.Completion.IsCompleted, Is.False);
                Assert.That(client.IoTask.IsCompleted, Is.False);
                Assert.That(calls, Is.Empty);
                Assert.That(first.IsCompleted, Is.False);
                Assert.That(session.DeadlineChecks, Is.Zero);
                session.Release.TrySetResult();
                await PumpUntil(client, Task.WhenAll(first, second));
                Assert.That(first.Result.Text, Is.EqualTo("first"));
                Assert.That(second.Result.Text, Is.EqualTo("second"));
                Assert.That(calls.ToArray(), Is.EqualTo(new[] { "first", "second" }));
                await Close(client);
                await server.Completion.WaitAsync(timeout.Token);
                Assert.That(session.Closes, Is.EqualTo(1));
            }
            finally
            {
                session.Release.TrySetResult();
                client.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [TestCase("failure")]
        [TestCase("request")]
        [TestCase("peer")]
        public async Task InitializationShutdownPreservesTheFailureCancellationAndOrderlyDrainContracts(string shutdown)
        {
            var session = new InitializingSession();
            int calls = 0;
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(
                (_, _, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(new EchoResponse());
                }, session: _ => session));
            var grant = await Grant(host, 892);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext client = Context(grant.Connect, memory, worker);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await PumpUntil(client, client.ConnectTask);
                ServerConnectionContext server = await session.Entered.Task.WaitAsync(timeout.Token);
                Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(5), default);
                await session.Admissions.Reader.ReadAsync(timeout.Token);
                var failure = new InvalidOperationException("Initialization failed.");
                if (shutdown == "failure")
                {
                    session.Release.TrySetException(failure);
                }
                else if (shutdown == "request")
                {
                    server.RequestClose(2);
                }
                else
                {
                    await Close(client);
                    // An orderly peer close preserves accepted reliable work, including
                    // initialization. Only failures and explicit server shutdown cancel it.
                    Assert.That(server.Completion.IsCompleted, Is.False);
                    Assert.That(calls, Is.Zero);
                    session.Release.TrySetResult();
                }

                ServerCloseInfo result = await server.Completion.WaitAsync(timeout.Token);
                Assert.That(result.Error, shutdown == "failure" ? Is.SameAs(failure) : Is.Null);
                Assert.That(result.Reason, Is.EqualTo(shutdown == "request" ? 2 : shutdown == "peer" ? 1 : 0));
                Assert.That(calls, Is.EqualTo(shutdown == "peer" ? 1 : 0));
                Assert.That(session.Closes, Is.EqualTo(1));
                Assert.CatchAsync<NetworkException>(async () => await PumpUntil(client, request));
                await Close(client);
            }
            finally
            {
                session.Release.TrySetResult();
                client.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task EnabledMetricsObserveNormalLoopbackWorkAndQueuesWithoutRetainingFrames()
        {
            var measurements = new ConcurrentQueue<(string Name, double Value)>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Name == "GameFrame.Server.Network.Kcp")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => measurements.Enqueue((instrument.Name, value)));
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurements.Enqueue((instrument.Name, value)));
            listener.Start();
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application());
            var grant = await Grant(host, 894);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext client = Context(grant.Connect, memory, worker);
            try
            {
                await PumpUntil(client, client.ConnectTask);
                // A small single-connection sample validates instruments; it is not a capacity gate.
                for (int i = 0; i < 144; i++)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    string text = $"sample-{i}";
                    Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = text }, TimeSpan.FromSeconds(2), default);
                    await PumpUntil(client, request);
                    Assert.That(request.Result.Text, Is.EqualTo(text));
                    if (i == 15)
                    {
                        measurements.Clear();
                    }
                    else if (i >= 16)
                    {
                        measurements.Enqueue(("loopback.rpc.rtt", System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                    }
                }

                var sample = measurements.ToArray();
                foreach (string name in new[] { "gameframe.kcp.owner.work", "gameframe.kcp.owner.interval",
                    "gameframe.kcp.owner.allocated", "gameframe.kcp.send.queue", "gameframe.kcp.dispatch.queue", "loopback.rpc.rtt" })
                {
                    double[] values = sample.Where(item => item.Name == name).Select(item => item.Value).Order().ToArray();
                    Assert.That(values, Is.Not.Empty, name);
                    Assert.That(values.All(value => value >= 0 && double.IsFinite(value)), Is.True, name);
                    TestContext.WriteLine($"Loopback sample {name}: count={values.Length}, p50={values[values.Length / 2]:F3}, p95={values[(values.Length - 1) * 95 / 100]:F3}, max={values[^1]:F3}");
                }
                await Close(client);
            }
            finally
            {
                client.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task ServerHelloWaitsForSessionPublicationAndFactoryDoesNotBlockListener()
        {
            using var release = new ManualResetEventSlim(false);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(session: _ =>
            {
                Assert.That(Thread.CurrentThread.Name, Is.Not.EqualTo("GameFrame KCP Listener"));
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Factory test release timed out.");
                }
                return new ServerSession();
            }));
            var grant = await Grant(host, 893);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext client = Context(grant.Connect, memory, worker);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await OnListener(host, _ =>
                {
                }).WaitAsync(TimeSpan.FromSeconds(1));
                await Task.Delay(100);
                Assert.That(client.ConnectTask.IsCompleted, Is.False);
                release.Set();
                await PumpUntil(client, client.ConnectTask);
                Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = "published" }, TimeSpan.FromSeconds(2), default);
                await PumpUntil(client, request);
                Assert.That(request.Result.Text, Is.EqualTo("published"));
                await Close(client);
            }
            finally
            {
                release.Set();
                client.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task ActivePushAndPublicCloseDoNotAffectANewerConnection()
        {
            var connected = Channel.CreateBounded<ServerConnectionContext>(2);
            var sessions = new List<CaptureSession>();
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(session: _ =>
            {
                var state = new CaptureSession(connected);
                lock (sessions)
                {
                    sessions.Add(state);
                }

                return state;
            }));
            var firstGrant = await Grant(host, 851);
            var secondGrant = await Grant(host, 852);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext first = Context(firstGrant.Connect, memory, worker);
            ConnectionContext second = Context(secondGrant.Connect, memory, worker);
            try
            {
                await PumpUntil(first, first.ConnectTask);
                await PumpUntil(second, second.ConnectTask);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                ServerConnectionContext one = await connected.Reader.ReadAsync(timeout.Token);
                ServerConnectionContext two = await connected.Reader.ReadAsync(timeout.Token);
                ServerConnectionContext firstServer = one.GrantId == firstGrant.Grant ? one : two;
                ServerConnectionContext secondServer = one.GrantId == firstGrant.Grant ? two : one;
                var received = new TaskCompletionSource<EchoRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
                second.Push = (_, frame) => received.TrySetResult(TransportTestProtocol.ReliablePush.Codec.Decode(frame.Body));
                await secondServer.PushAsync(TransportTestProtocol.ReliablePush,
                    new EchoRequest { DelayMs = 91 });
                await PumpUntil(second, received.Task);
                Assert.That(received.Task.Result.DelayMs, Is.EqualTo(91));
                Assert.Throws<ArgumentException>(() => secondServer.TryPushUnreliable(TransportTestProtocol.ReliablePush, new EchoRequest()));
                await firstServer.CloseAsync().WaitAsync(timeout.Token);
                await firstServer.CloseAsync().WaitAsync(timeout.Token);
                Task<EchoResponse> rpc = second.Request(GameNetRoutes.Echo, new EchoRequest { Text = "isolated" }, TimeSpan.FromSeconds(2), default);
                await PumpUntil(second, rpc);
                Assert.That(rpc.Result.Text, Is.EqualTo("isolated"));
                Assert.Throws<OperationCanceledException>(() => firstServer.PushAsync(TransportTestProtocol.ReliablePush, new EchoRequest()));
                await Close(first);
                await Close(second);
                await secondServer.Completion.WaitAsync(timeout.Token);
                lock (sessions)
                {
                    Assert.That(sessions.All(state => state.Closed == 1), Is.True);
                }
            }
            finally
            {
                first.FailHost(new NetworkException("Test cleanup."));
                second.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExternalCloseSendsReasonWhileHandlerIsBlockedAndLeavesOtherConnectionsHealthy(bool pushAfterClose)
        {
            var connected = Channel.CreateBounded<ServerConnectionContext>(2);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int oldCalls = 0;
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(
                async (connection, request, token) =>
                {
                    if (request.Text == "blocked")
                    {
                        Interlocked.Increment(ref oldCalls);
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                        if (pushAfterClose)
                        {
                            await connection.PushAsync(TransportTestProtocol.ReliablePush, new EchoRequest());
                        }
                        else
                        {
                            await Task.Delay(Timeout.Infinite, token);
                        }
                    }

                    return new EchoResponse { Text = request.Text };
                }, session: _ => new CaptureSession(connected)));
            var firstGrant = await Grant(host, 871);
            var secondGrant = await Grant(host, 872);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext first = Context(firstGrant.Connect, memory, worker);
            ConnectionContext second = Context(secondGrant.Connect, memory, worker);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await PumpUntil(first, first.ConnectTask);
                await PumpUntil(second, second.ConnectTask);
                ServerConnectionContext one = await connected.Reader.ReadAsync(timeout.Token);
                ServerConnectionContext two = await connected.Reader.ReadAsync(timeout.Token);
                ServerConnectionContext server = one.GrantId == firstGrant.Grant ? one : two;
                Task<EchoResponse> blocked = first.Request(GameNetRoutes.Echo, new EchoRequest { Text = "blocked" },
                    TimeSpan.FromSeconds(4), default);
                await entered.Task.WaitAsync(timeout.Token);
                Task<EchoResponse> queued = first.Request(GameNetRoutes.Echo, new EchoRequest { Text = "blocked" },
                    TimeSpan.FromSeconds(4), default);
                Exception? remoteError = null;
                first.StateChanged = (_, error) => remoteError ??= error;
                server.RequestClose(2);
                server.RequestClose(7);
                if (pushAfterClose)
                {
                    release.SetResult();
                }

                await PumpUntil(first, server.CloseAsync(7));
                Assert.That((await server.Completion).Reason, Is.EqualTo(2));
                Assert.That((await server.Completion).Error, Is.Null);
                await PumpUntil(first, first.IoTask);
                while (first.PumpOne())
                {
                }

                Assert.That(remoteError?.Message, Does.Contain("(2)"));
                Assert.CatchAsync<NetworkException>(async () => await blocked);
                Assert.CatchAsync<NetworkException>(async () => await queued);
                Assert.That(oldCalls, Is.EqualTo(1));
                Task<EchoResponse> healthy = second.Request(GameNetRoutes.Echo, new EchoRequest { Text = "healthy" },
                    TimeSpan.FromSeconds(2), default);
                await PumpUntil(second, healthy);
                Assert.That(healthy.Result.Text, Is.EqualTo("healthy"));
                await Close(first);
                await Close(second);
            }
            finally
            {
                first.FailHost(new NetworkException("Test cleanup."));
                second.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task InjectedLoggerRecordsKcpHandlerFailure()
        {
            var logs = new WssLifecycleTests.LogCapture();
            using ILoggerFactory logging = LoggerFactory.Create(builder => builder.AddProvider(logs));
            var connected = Channel.CreateBounded<ServerConnectionContext>(1);
            var failure = new InvalidOperationException("Injected KCP handler failure.");
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0),
                Application((_, _, _) => throw failure, session: _ => new CaptureSession(connected)),
                logger: logging.CreateLogger<KcpListenerHost>());
            var grant = await Grant(host, 881);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext client = Context(grant.Connect, memory, worker);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await PumpUntil(client, client.ConnectTask);
                ServerConnectionContext server = await connected.Reader.ReadAsync(timeout.Token);
                Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = "private-message" },
                    TimeSpan.FromSeconds(3), default);
                Assert.CatchAsync<NetworkException>(async () => await PumpUntil(client, request));
                await server.Completion.WaitAsync(timeout.Token);
                Assert.That(logs.Entries.Any(entry => entry.Category == typeof(KcpListenerHost).FullName &&
                    entry.Error?.InnerException == failure), Is.True);
                Assert.That(logs.Entries.All(entry => !entry.Text.Contains("private-message", StringComparison.Ordinal)), Is.True);
                await Close(client);
            }
            finally
            {
                client.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task CloseAfterResponseDeliversTheReplyBeforeClosingAndCompletesCleanup()
        {
            ServerConnectionContext server = null!;
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(
                (connection, request, _) =>
                {
                    server = connection;
                    connection.CloseAfterResponse(7);
                    return Task.FromResult(new EchoResponse { Text = request.Text });
                }));
            var grant = await Grant(host, 861);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext client = Context(grant.Connect, memory, worker);
            try
            {
                await PumpUntil(client, client.ConnectTask);
                Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = "last reply" },
                    TimeSpan.FromSeconds(2), default);
                await PumpUntil(client, request);
                Assert.That(request.Result.Text, Is.EqualTo("last reply"));
                await PumpUntil(client, server.Completion);
                ServerCloseInfo result = await server.Completion;
                Assert.That(result.Reason, Is.EqualTo(7));
                Assert.That(result.Error, Is.Null);
                Assert.Throws<OperationCanceledException>(server.EnableFullTraffic);
                IPEndPoint endpoint = server.RemoteEndpoint;
                int originalPort = endpoint.Port;
                endpoint.Port = 1;
                Assert.That(server.RemoteEndpoint.Port, Is.EqualTo(originalPort));
                await Close(client);
            }
            finally
            {
                client.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task NormalTransportEndDrainsAcceptedRequestsWithoutCancelingTheHandler()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<ulong>();
            ServerConnectionContext identity = null!;
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(
                async (connection, request, token) =>
                {
                    identity = connection;
                    if (calls.Count == 0)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }
                    token.ThrowIfCancellationRequested();
                    calls.Add(ulong.Parse(request.Text));
                    if (calls.Count == 3)
                    {
                        drained.TrySetResult();
                    }
                    return new EchoResponse();
                }));
            var grant = await Grant(host, 601);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext context = Context(grant.Connect, memory, worker);
            var requests = new List<Task<EchoResponse>>();
            try
            {
                await PumpUntil(context, context.ConnectTask);
                for (int i = 0; i < 3; i++)
                {
                    requests.Add(context.Request(GameNetRoutes.Echo, new EchoRequest { Text = (i + 1).ToString() }, TimeSpan.FromSeconds(5), default));
                }
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                bool queued = false;
                using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!queued)
                {
                    await OnListener(host, _ =>
                    {
                        var children = (System.Collections.IDictionary)typeof(KcpListenerHost)
                            .GetField("children", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
                        object child = children.Values.Cast<object>().Single();
                        object incoming = child.GetType().GetField("Incoming", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(child)!;
                        queued = (int)incoming.GetType().GetProperty("Count")!.GetValue(incoming)! == 2;
                    }).WaitAsync(guard.Token);
                    if (!queued)
                    {
                        await Task.Delay(1, guard.Token);
                    }
                }
                Assert.That(identity.GrantId, Is.EqualTo(grant.Grant));
                // The C harness separately proves real close-notify leaves the same Closed state
                // and queued frames. Reproduce that state on the managed owner's thread.
                await OnListener(host, listener => KcpNative.Check(KcpNative.CloseConnection(listener,
                    identity.Id, identity.Generation), "test transport EOF"));
                release.TrySetResult();
                await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
                CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, calls);
                await Close(context);
                foreach (Task<EchoResponse> request in requests)
                {
                    Assert.ThrowsAsync<NetworkException>(async () => await request);
                }
            }
            finally
            {
                release.TrySetResult();
                context.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }
            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task ReliableAdmissionRejectsBeforeAllocatingCorrelationOrPendingSlot()
        {
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application());
            var grant = await Grant(host, 701);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            var options = new NetworkClientOptions { BusinessBytesPerSecond = 1, BusinessBurstBytes = 8192 };
            var context = new ConnectionContext(1, options, TransportTestProtocol.Catalog, grant.Connect, memory, default, kcpWorker: worker);
            var requests = new List<Task<EchoResponse>>();
            context.Start();
            try
            {
                await PumpUntil(context, context.ConnectTask);
                var idField = typeof(ConnectionContext).GetField("nextId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                bool rejected = false;
                for (int i = 0; i < 32; i++)
                {
                    ulong before = (ulong)idField.GetValue(context)!;
                    int pending = context.Metrics().PendingCount;
                    try
                    {
                        requests.Add(context.Request(GameNetRoutes.Echo, new EchoRequest { Text = new string('界', 128) },
                            TimeSpan.FromSeconds(5), default));
                    }
                    catch (NetworkBackpressureException)
                    {
                        Assert.That((ulong)idField.GetValue(context)!, Is.EqualTo(before));
                        Assert.That(context.Metrics().PendingCount, Is.EqualTo(pending));
                        rejected = true;
                        break;
                    }
                }
                Assert.That(rejected, Is.True);
                await Close(context);
                foreach (Task<EchoResponse> request in requests)
                {
                    if (request.IsFaulted)
                    {
                        Assert.ThrowsAsync<NetworkException>(async () => await request);
                    }
                }
            }
            finally
            {
                context.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task ReliableRpcPassesAnUnreliableFrameWaitingForItsTokenBucket()
        {
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application());
            var grant = await Grant(host, 751);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            var options = new NetworkClientOptions { UnreliableBytesPerSecond = 1, UnreliableBurstBytes = 1150 };
            var context = new ConnectionContext(1, options, TransportTestProtocol.Catalog, grant.Connect, memory, default, kcpWorker: worker);
            context.Start();
            try
            {
                await PumpUntil(context, context.ConnectTask);
                var transport = (KcpTransport)typeof(ConnectionContext).GetField("transport",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context)!;
                // Consume the real transport bucket while the Context sender is idle. This creates
                // a delayed handoff without mutating owner state or replacing the native library.
                using (OwnedFrame probe = memory.Allocate(32 + TransportTestProtocol.UnreliableSend.MaxBodyBytes))
                {
                    var writer = new BoundedFrameWriter(probe);
                    TransportTestProtocol.UnreliableSend.Codec.Encode(writer, new EchoRequest());
                    writer.Complete(FrameKind.Notify, TransportTestProtocol.UnreliableSend.Opcode, 0);
                    probe.Delivery = NetDelivery.Unreliable;
                    bool blocked = false;
                    for (int i = 0; i < 64; i++)
                    {
                        if (!await transport.TrySendAsync(probe, default, () => true).WaitAsync(TimeSpan.FromSeconds(2)))
                        {
                            blocked = true;
                            break;
                        }
                    }
                    Assert.That(blocked, Is.True);
                }
                Assert.That(context.TrySendUnreliable(TransportTestProtocol.UnreliableSend, new EchoRequest()), Is.True);
                Task<EchoResponse> reliable = context.Request(GameNetRoutes.Echo, new EchoRequest { Text = "priority" },
                    TimeSpan.FromSeconds(2), default);
                await PumpUntil(context, reliable);
                Assert.That(reliable.Result.Text, Is.EqualTo("priority"));
                Assert.That((int)typeof(ConnectionContext).GetField("unreliableSendCount",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context)!, Is.EqualTo(1), "Reliable response must arrive while the lossy frame is still waiting.");
                await Close(context);
            }
            finally
            {
                context.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }
            await host.DisposeAsync();
            Assert.That(memory.OwnedBytes, Is.Zero);
            Assert.That(host.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task HelloEchoAndCloseReleaseBothOwners()
        {
            ServerConnectionContext server = null!;
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application((connection, request, token) =>
            {
                server = connection;
                return Echo(connection, request, token);
            }));
            var grant = await Grant(host, 101);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext context = Context(grant.Connect, memory, worker);
            try
            {
                await PumpUntil(context, context.ConnectTask);
                Assert.That(context.State, Is.EqualTo(NetworkClientState.Connected));
                Assert.That(memory.OwnedBytes, Is.GreaterThanOrEqualTo(4 * 1024 * 1024));
                Task<EchoResponse> request = context.Request(GameNetRoutes.Echo, new EchoRequest { Text = "native UDP" }, TimeSpan.FromSeconds(2), default);
                await PumpUntil(context, request);
                Assert.That(request.Result.Text, Is.EqualTo("native UDP"));
                await Close(context);
                ServerCloseInfo closed = await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(closed.Reason, Is.EqualTo(1));
                Assert.That(closed.Error, Is.Null);
                Assert.That(memory.OwnedBytes, Is.Zero);
            }
            finally
            {
                context.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }

            await host.DisposeAsync();
            Assert.That(host.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task ThrowingApplicationCancellationCannotSkipListenerCleanup()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackFailure = new InvalidOperationException("Application cancellation failed.");
            var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(
                async (connection, request, token) =>
                {
                    using CancellationTokenRegistration registration = token.Register(() => throw callbackFailure);
                    entered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                    return new EchoResponse();
                }));
            var worker = new KcpWorker();
            ConnectionContext? context = null;
            try
            {
                var grant = await Grant(host, 151);
                var memory = new FrameMemoryBudget(64 * 1024 * 1024);
                context = Context(grant.Connect, memory, worker);
                await PumpUntil(context, context.ConnectTask);
                Task<EchoResponse> request = context.Request(GameNetRoutes.Echo, new EchoRequest { Text = "cancel" },
                    TimeSpan.FromSeconds(5), default);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                AggregateException error = Assert.ThrowsAsync<AggregateException>(async () =>
                    await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))!;
                Assert.That(error.InnerExceptions, Does.Contain(callbackFailure));
                Assert.That(host.OwnedBytes, Is.Zero);
                Assert.ThrowsAsync<AggregateException>(async () => await host.DisposeAsync());

                context.FailHost(new NetworkException("Test cleanup."));
                await Close(context);
                Assert.ThrowsAsync<NetworkException>(async () => await request);
                Assert.That(memory.OwnedBytes, Is.Zero);
            }
            finally
            {
                context?.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
                try
                {
                    await host.DisposeAsync();
                }
                catch (AggregateException error) when (error.InnerExceptions.Contains(callbackFailure))
                {
                }
            }
        }

        [Test]
        public async Task ClosingOneChildPreservesSharedListenerAndOtherClient()
        {
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application());
            var firstGrant = await Grant(host, 201);
            var secondGrant = await Grant(host, 202);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext first = Context(firstGrant.Connect, memory, worker), second = Context(secondGrant.Connect, memory, worker);
            try
            {
                await PumpUntil(first, first.ConnectTask);
                await PumpUntil(second, second.ConnectTask);
                await Close(first);
                Task<EchoResponse> request = second.Request(GameNetRoutes.Echo, new EchoRequest { Text = "still alive" }, TimeSpan.FromSeconds(2), default);
                await PumpUntil(second, request);
                Assert.That(request.Result.Text, Is.EqualTo("still alive"));
                await Close(second);
                Assert.That(memory.OwnedBytes, Is.Zero);
            }
            finally
            {
                first.FailHost(new NetworkException("Test cleanup."));
                second.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }
        }

        [Test]
        public async Task RejectedHandshakePreservesEstablishedChildAndNewAdmission()
        {
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application());
            var grant = await Grant(host, 251);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext healthy = Context(grant.Connect, memory, worker);
            ConnectionContext? rejected = null, next = null;
            try
            {
                await PumpUntil(healthy, healthy.ConnectTask);
                var invalid = NetworkConnectOptions.Kcp(grant.Connect.Endpoint, RandomNumberGenerator.GetBytes(32),
                    252, RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), NetworkClock.GetMilliseconds() + 1000);
                rejected = Context(invalid, memory, worker);
                Assert.ThrowsAsync<NetworkException>(async () => await PumpUntil(rejected, rejected.ConnectTask));
                await Close(rejected);

                Task<EchoResponse> reply = healthy.Request(GameNetRoutes.Echo, new EchoRequest { Text = "handshake isolated" },
                    TimeSpan.FromSeconds(2), default);
                await PumpUntil(healthy, reply);
                Assert.That(reply.Result.Text, Is.EqualTo("handshake isolated"));
                Assert.That(host.Stopped.IsCompleted, Is.False);

                var nextGrant = await Grant(host, 253);
                next = Context(nextGrant.Connect, memory, worker);
                await PumpUntil(next, next.ConnectTask);
                await Close(next);
                await Close(healthy);
            }
            finally
            {
                healthy.FailHost(new NetworkException("Test cleanup."));
                rejected?.FailHost(new NetworkException("Test cleanup."));
                next?.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }
            await host.DisposeAsync();
            Assert.That(memory.OwnedBytes, Is.Zero);
            Assert.That(host.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task MainThreadPauseDoesNotStopUdpAndDeadline()
        {
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application());
            var grant = await Grant(host, 301);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext context = Context(grant.Connect, memory, worker);
            try
            {
                await PumpUntil(context, context.ConnectTask);
                Task<EchoResponse> slow = context.Request(GameNetRoutes.Echo, new EchoRequest { Text = "late", DelayMs = 100 }, TimeSpan.FromMilliseconds(30), default);
                // Cross a heartbeat interval while application Pump is paused; both IO owners keep responding.
                await Task.Delay(6000);
                Assert.ThrowsAsync<NetworkRequestTimeoutException>(async () => await PumpUntil(context, slow));
                Task<EchoResponse> next = context.Request(GameNetRoutes.Echo, new EchoRequest { Text = "continued" }, TimeSpan.FromSeconds(2), default);
                await PumpUntil(context, next);
                Assert.That(next.Result.Text, Is.EqualTo("continued"));
                await Close(context);
                Assert.That(memory.OwnedBytes, Is.Zero);
            }
            finally
            {
                context.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }
        }

        [Test]
        public async Task GrantRevocationStopsDeliveryAndLeavesListenerUsable()
        {
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application());
            var grant = await Grant(host, 401);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext context = Context(grant.Connect, memory, worker);
            try
            {
                await PumpUntil(context, context.ConnectTask);
                await host.RevokeGrantAsync(grant.Grant);
                Task<EchoResponse> request = context.Request(GameNetRoutes.Echo, new EchoRequest { Text = "revoked" }, TimeSpan.FromMilliseconds(150), default);
                Assert.ThrowsAsync<NetworkRequestTimeoutException>(async () => await PumpUntil(context, request));
                await Close(context);
                var nextGrant = await Grant(host, 402);
                ConnectionContext next = Context(nextGrant.Connect, memory, worker);
                await PumpUntil(next, next.ConnectTask);
                await Close(next);
                Assert.That(memory.OwnedBytes, Is.Zero);
            }
            finally
            {
                context.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }
        }

        [Test]
        public async Task UnreliableNotifyAndPushUseTheSameDtlsSession()
        {
            Task Handler(ServerConnectionContext connection, EchoRequest move, CancellationToken token)
            {
                Assert.That(connection.TryPushUnreliable(TransportTestProtocol.UnreliablePush,
                    new EchoRequest { DelayMs = move.DelayMs }), Is.True);
                return Task.CompletedTask;
            }

            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), Application(move: Handler));
            var grant = await Grant(host, 501);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext context = Context(grant.Connect, memory, worker);
            var received = new TaskCompletionSource<EchoRequest>();
            context.Push = (opcode, frame) => received.TrySetResult(TransportTestProtocol.UnreliablePush.Codec.Decode(frame.Body));
            try
            {
                await PumpUntil(context, context.ConnectTask);
                Assert.That(context.TrySendUnreliable(TransportTestProtocol.UnreliableSend, new EchoRequest { DelayMs = 77 }), Is.True);
                await PumpUntil(context, received.Task);
                Assert.That(received.Task.Result.DelayMs, Is.EqualTo(77));
                await Close(context);
                Assert.That(memory.OwnedBytes, Is.Zero);
            }
            finally
            {
                context.FailHost(new NetworkException("Test cleanup."));
                await worker.ShutdownAsync();
            }
        }
    }
}
