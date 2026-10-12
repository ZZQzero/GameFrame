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

        private static async Task<OwnedFrame?> Echo(OwnedFrame frame, FrameMemoryBudget budget, CancellationToken token)
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
                async (frame, budget, token) =>
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
            Task<OwnedFrame?> Handler(OwnedFrame input, FrameMemoryBudget budget, CancellationToken token)
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
