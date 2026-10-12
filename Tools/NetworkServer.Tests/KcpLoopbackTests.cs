using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Network.Connection;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Network.Transport.Kcp;
using GameFrame.Server.Network;
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

        private static async Task<OwnedFrame?> Echo(KcpConnectionInfo connection, OwnedFrame frame, FrameMemoryBudget budget, CancellationToken token)
        {
            if (frame.Header.Opcode != GameNetRoutes.Echo.Opcode)
            {
                throw new NetworkProtocolException("The local test handler only accepts Echo.");
            }

            EchoRequest request = GameNetRoutes.Echo.RequestCodec.Decode(frame.Body);
            await Task.Delay((int)request.DelayMs, token);
            OwnedFrame reply = budget.Allocate(32 + GameNetRoutes.Echo.ResponseMaxBodyBytes);
            try
            {
                var writer = new BoundedFrameWriter(reply);
                GameNetRoutes.Echo.ResponseCodec.Encode(writer, new EchoResponse { Text = request.Text });
                writer.Complete(FrameKind.Response, frame.Header.Opcode, frame.Header.CorrelationId);
                return reply;
            }
            catch
            {
                reply.Dispose();
                throw;
            }
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
            var context = new ConnectionContext(1, options, GameNetProtocol.Catalog, connect, memory, default, kcpWorker: worker);
            context.Start();
            return context;
        }

        private static Task OnListener(KcpListenerHost host, Action<IntPtr> command)
        {
            var method = typeof(KcpListenerHost).GetMethod("CommandAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            return (Task)method.Invoke(host, new object[] { command })!;
        }

        [Test]
        public async Task NormalTransportEndDrainsAcceptedRequestsWithoutCancelingTheHandler()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<ulong>();
            KcpConnectionInfo identity = default;
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog,
                async (connection, frame, budget, token) =>
                {
                    identity = connection;
                    if (calls.Count == 0)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }
                    token.ThrowIfCancellationRequested();
                    calls.Add(frame.Header.CorrelationId);
                    if (calls.Count == 3)
                    {
                        drained.TrySetResult();
                    }
                    return null;
                });
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
                    requests.Add(context.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(5), default));
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
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog, Echo);
            var grant = await Grant(host, 701);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            var options = new NetworkClientOptions { BusinessBytesPerSecond = 1, BusinessBurstBytes = 8192 };
            var context = new ConnectionContext(1, options, GameNetProtocol.Catalog, grant.Connect, memory, default, kcpWorker: worker);
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
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog,
                (connection, frame, budget, token) => frame.Header.Opcode == GameNetRoutes.Echo.Opcode ?
                    Echo(connection, frame, budget, token) : Task.FromResult<OwnedFrame?>(null));
            var grant = await Grant(host, 751);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            var options = new NetworkClientOptions { UnreliableBytesPerSecond = 1, UnreliableBurstBytes = 1150 };
            var context = new ConnectionContext(1, options, GameNetProtocol.Catalog, grant.Connect, memory, default, kcpWorker: worker);
            context.Start();
            try
            {
                await PumpUntil(context, context.ConnectTask);
                var transport = (KcpTransport)typeof(ConnectionContext).GetField("transport",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context)!;
                // Consume the real transport bucket while the Context sender is idle. This creates
                // a delayed handoff without mutating owner state or replacing the native library.
                using (OwnedFrame probe = memory.Allocate(32 + GameNetRoutes.MoveInput.MaxBodyBytes))
                {
                    var writer = new BoundedFrameWriter(probe);
                    GameNetRoutes.MoveInput.Codec.Encode(writer, new MoveInput());
                    writer.Complete(FrameKind.Notify, GameNetRoutes.MoveInput.Opcode, 0);
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
                Assert.That(context.TrySendUnreliable(GameNetRoutes.MoveInput, new MoveInput()), Is.True);
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
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog, Echo);
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
            var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog,
                async (connection, frame, budget, token) =>
                {
                    using CancellationTokenRegistration registration = token.Register(() => throw callbackFailure);
                    entered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                    return null;
                });
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
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog, Echo);
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
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog, Echo);
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
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog, Echo);
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
            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog, Echo);
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
            Task<OwnedFrame?> Handler(KcpConnectionInfo connection, OwnedFrame input, FrameMemoryBudget budget, CancellationToken token)
            {
                MoveInput move = GameNetRoutes.MoveInput.Codec.Decode(input.Body);
                OwnedFrame reply = budget.Allocate(32 + GameNetRoutes.WorldSnapshot.MaxBodyBytes);
                var writer = new BoundedFrameWriter(reply);
                GameNetRoutes.WorldSnapshot.Codec.Encode(writer, new WorldSnapshot { ServerTick = move.ClientTick, Entities = Array.Empty<EntityState>() });
                writer.Complete(FrameKind.Push, GameNetRoutes.WorldSnapshot.Opcode, 0);
                reply.Delivery = NetDelivery.Unreliable;
                return Task.FromResult<OwnedFrame?>(reply);
            }

            await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), GameNetProtocol.Catalog, Handler);
            var grant = await Grant(host, 501);
            var memory = new FrameMemoryBudget(64 * 1024 * 1024);
            var worker = new KcpWorker();
            ConnectionContext context = Context(grant.Connect, memory, worker);
            var received = new TaskCompletionSource<WorldSnapshot>();
            context.Push = (opcode, frame) => received.TrySetResult(GameNetRoutes.WorldSnapshot.Codec.Decode(frame.Body));
            try
            {
                await PumpUntil(context, context.ConnectTask);
                Assert.That(context.TrySendUnreliable(GameNetRoutes.MoveInput, new MoveInput { ClientTick = 77 }), Is.True);
                await PumpUntil(context, received.Task);
                Assert.That(received.Task.Result.ServerTick, Is.EqualTo(77));
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
