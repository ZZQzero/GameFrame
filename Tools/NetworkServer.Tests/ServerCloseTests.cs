using System.Buffers;
using System.Net;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Server.Network;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class ServerCloseTests
    {
        private sealed class InitializingSession : ServerSession
        {
            internal readonly TaskCompletionSource Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int DeadlineChecks;
            internal int Closes;

            public override async ValueTask ConnectedAsync(ServerConnectionContext connection)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(connection.Cancellation);
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
        public async Task TransportHeartbeatRunsDuringInitializationButApplicationDeadlineWaitsForReadiness()
        {
            var session = new InitializingSession();
            var application = Application((_, _, _) => Task.FromResult(new EchoResponse()), _ => session);
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var memory = new GameFrame.Network.Transport.FrameMemoryBudget(1024 * 1024);
            using var socket = new ConnectionRaceTests.ControlledSocket(FrameDirection.ServerToClient);
            long now = 1000;
            var server = new ServerConnection(new WssServerOptions(), application, memory, () => { }, guard.Token,
                NullLogger.Instance, 1, new IPEndPoint(IPAddress.Loopback, 1000), () => Interlocked.Read(ref now),
                (_, token) => Task.Delay(Timeout.Infinite, token));
            byte[] hello = new byte[80];
            FrameCodec.WriteHeader(hello, new FrameHeader(FrameKind.Control, (uint)ControlOpcode.ClientHello, 0, 48));
            ControlCodec.WriteClientHello(hello.AsSpan(32), GameNetProtocol.Catalog.Hash, 32768, 0, 0,
                ReadOnlySpan<byte>.Empty, NetworkTransportKind.WebSocket);
            socket.Enqueue(hello);
            Task running = server.RunAsync(socket);
            try
            {
                await socket.NextSend(guard.Token);
                await session.Entered.Task.WaitAsync(guard.Token);
                Interlocked.Exchange(ref now, 6000);
                await server.CheckHeartbeatAsync();
                byte[] ping = await socket.NextSend(guard.Token);
                FrameHeader pingHeader = FrameCodec.ReadHeader(ping, 32768, NetDelivery.Reliable,
                    FrameDirection.ServerToClient, false);
                Assert.That(pingHeader.Opcode, Is.EqualTo((uint)ControlOpcode.Ping));
                Assert.That(ControlCodec.ReadNonce(ping.AsSpan(32)), Is.Not.Zero);
                Assert.That(session.DeadlineChecks, Is.Zero);

                socket.Enqueue(Request(1));
                session.Release.TrySetResult();
                byte[] response = await socket.NextSend(guard.Token);
                FrameHeader header = FrameCodec.ReadHeader(response, 32768, NetDelivery.Reliable,
                    FrameDirection.ServerToClient, false);
                Assert.That(header.Kind, Is.EqualTo(FrameKind.Response));
                await server.CheckHeartbeatAsync();
                Assert.That(session.DeadlineChecks, Is.EqualTo(1));
            }
            finally
            {
                server.Stop();
                await running.WaitAsync(guard.Token);
                server.Finish();
            }

            Assert.That(session.Closes, Is.EqualTo(1));
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        internal static ServerApplication Application(Func<ServerConnectionContext, EchoRequest, CancellationToken, Task<EchoResponse>> echo,
            Func<ServerConnectionContext, ServerSession>? session = null)
        {
            var application = new ServerApplication(GameNetProtocol.Catalog, session);
            application.Rpc(GameNetRoutes.Echo, echo);
            foreach (var route in GameNetProtocol.Catalog.Routes)
            {
                if (route.Kind != NetRouteKind.Push && route.Opcode != GameNetRoutes.Echo.Opcode)
                {
                    application.Reject(route);
                }
            }

            return application;
        }

        private static byte[] Request(ulong correlation)
        {
            var body = new ArrayBufferWriter<byte>();
            GameNetRoutes.Echo.RequestCodec.Encode(body, new EchoRequest());
            byte[] bytes = new byte[32 + body.WrittenCount];
            FrameCodec.WriteHeader(bytes, new FrameHeader(FrameKind.Request, GameNetRoutes.Echo.Opcode, correlation, body.WrittenCount));
            body.WrittenSpan.CopyTo(bytes.AsSpan(32));
            return bytes;
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExternalCloseSendsItsFirstReasonWhileHandlerIsBlockedAndDiscardsQueuedWork(bool pushAfterClose)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var application = Application(async (connection, _, token) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                if (pushAfterClose)
                {
                    await connection.PushAsync(GameNetRoutes.WorldSnapshotReliable, new WorldSnapshot());
                }
                else
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

                return new EchoResponse();
            });
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var memory = new GameFrame.Network.Transport.FrameMemoryBudget(1024 * 1024);
            using var socket = new ConnectionRaceTests.ControlledSocket(FrameDirection.ServerToClient);
            var server = new ServerConnection(new WssServerOptions(), application, memory, () => { }, guard.Token,
                NullLogger.Instance, 1, new IPEndPoint(IPAddress.Loopback, 1000));
            byte[] hello = new byte[80];
            FrameCodec.WriteHeader(hello, new FrameHeader(FrameKind.Control, (uint)ControlOpcode.ClientHello, 0, 48));
            ControlCodec.WriteClientHello(hello.AsSpan(32), GameNetProtocol.Catalog.Hash, 32768, 0, 0, ReadOnlySpan<byte>.Empty,
                NetworkTransportKind.WebSocket);
            socket.Enqueue(hello);
            Task running = server.RunAsync(socket);
            try
            {
                await socket.NextCompletedSend(guard.Token);
                await socket.NextSend(guard.Token);
                socket.Enqueue(Request(1));
                await entered.Task.WaitAsync(guard.Token);
                socket.Enqueue(Request(2));
                server.Context.RequestClose(2);
                server.Context.RequestClose(7);
                Assert.Catch<NetworkException>(server.Context.EnableFullTraffic);
                Assert.Catch<NetworkException>(() => server.Context.PushAsync(GameNetRoutes.WorldSnapshotReliable, new WorldSnapshot()));
                if (pushAfterClose)
                {
                    release.SetResult();
                }

                byte[] close = await socket.NextSend(guard.Token);
                Assert.That(ControlCodec.ReadClose(close.AsSpan(32)), Is.EqualTo(2));
                await socket.NextCompletedSend(guard.Token);
                socket.EnqueueTransportClose();
                await running.WaitAsync(guard.Token);
                server.Finish();
                ServerCloseInfo result = await server.Context.Completion;
                Assert.That(result.Reason, Is.EqualTo(2));
                Assert.That(result.Error, Is.Null);
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(memory.OwnedBytes, Is.Zero);
                await server.Context.CloseAsync(7);
            }
            finally
            {
                server.Stop();
                await running.WaitAsync(guard.Token);
                server.Finish();
            }
        }
    }
}
