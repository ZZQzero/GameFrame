using System.Buffers;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Server.Network;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class ServerHeartbeatTests
    {
        private ServerSettings settings = null!;
        private AccountSessions accounts = null!;
        private FrameMemoryBudget memory = null!;
        private ServerConnection server = null!;
        private ConnectionRaceTests.ControlledSocket socket = null!;
        private CancellationTokenSource guard = null!;
        private Task running = null!;
        private long now;

        [SetUp]
        public async Task Authenticate()
        {
            now = 1000;
            guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            settings = new ServerSettings("test", "secret");
            accounts = new AccountSessions(settings);
            memory = new FrameMemoryBudget(64 * 1024 * 1024);
            socket = new ConnectionRaceTests.ControlledSocket(FrameDirection.ServerToClient);
            server = new ServerConnection(settings, accounts, memory, () => true, () => { }, guard.Token,
                NullLogger.Instance, () => Interlocked.Read(ref now), (_, token) => Task.Delay(Timeout.Infinite, token));
            byte[] hello = new byte[80];
            FrameCodec.WriteHeader(hello, new FrameHeader(FrameKind.Control, (uint)ControlOpcode.ClientHello, 0, 48));
            ControlCodec.WriteClientHello(hello.AsSpan(32), GameNetProtocol.Catalog.Hash, 32768,
                0, 0, ReadOnlySpan<byte>.Empty, NetworkTransportKind.WebSocket);
            socket.Enqueue(hello);
            running = server.RunAsync(socket);
            await socket.NextSend(guard.Token);
            await socket.NextCompletedSend(guard.Token);
            var writer = new ArrayBufferWriter<byte>();
            GameNetRoutes.Login.RequestCodec.Encode(writer, new LoginRequest { Account = "test", Password = "secret" });
            byte[] login = new byte[32 + writer.WrittenCount];
            FrameCodec.WriteHeader(login, new FrameHeader(FrameKind.Request, GameNetRoutes.Login.Opcode, 1, writer.WrittenCount));
            writer.WrittenSpan.CopyTo(login.AsSpan(32));
            socket.Enqueue(login);
            byte[] response = await socket.NextSend(guard.Token);
            await socket.NextCompletedSend(guard.Token);
            Assert.That(GameNetRoutes.Login.ResponseCodec.Decode(response.AsSpan(32)).ResultCode, Is.EqualTo(ResultCode.Success));
        }

        [TearDown]
        public async Task Shutdown()
        {
            server.Stop();
            await running.WaitAsync(guard.Token);
            server.Finish();
            socket.Dispose();
            accounts.Dispose();
            settings.Dispose();
            guard.Dispose();
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        private void Advance(long timestamp) => Interlocked.Exchange(ref now, timestamp);

        [Test]
        public async Task ServerWatchdogContinuesWhilePingSendIsBlocked()
        {
            socket.HoldPing = true;
            Advance(6000);
            await server.CheckHeartbeatAsync().WaitAsync(guard.Token);
            await socket.NextSend(guard.Token);
            Advance(20999);
            await server.CheckHeartbeatAsync().WaitAsync(guard.Token);
            Advance(21000);
            NetworkException? error = Assert.ThrowsAsync<NetworkException>(async () => await server.CheckHeartbeatAsync());
            Assert.That(error?.Message, Is.EqualTo("Control frame send deadline elapsed."));
        }

        [Test]
        public async Task ServerPongTimeoutStartsAfterSendAcceptanceWithoutAnotherPing()
        {
            socket.HoldPing = true;
            Advance(6000);
            await server.CheckHeartbeatAsync();
            await socket.NextSend(guard.Token);
            Advance(20999);
            await server.CheckHeartbeatAsync();
            socket.ReleaseSend();
            await socket.NextCompletedSend(guard.Token);
            // The controlled socket returns just before the send owner records its accepted-send clock.
            await WaitForPongDeadline();
            Advance(35998);
            await server.CheckHeartbeatAsync();
            Advance(35999);
            NetworkException? error = Assert.ThrowsAsync<NetworkException>(async () => await server.CheckHeartbeatAsync());
            Assert.That(error?.Message, Is.EqualTo("Application heartbeat timed out."));
        }

        private async Task WaitForPongDeadline()
        {
            var field = typeof(ServerConnection).GetField("pongDeadline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var gate = typeof(ServerConnection).GetField("queueGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
            long deadline;
            do
            {
                lock (gate)
                {
                    deadline = (long)field.GetValue(server)!;
                }

                if (deadline == 0)
                {
                    await Task.Delay(1, guard.Token);
                }
            }
            while (deadline == 0);
        }

        private async Task HoldEchoResponse()
        {
            socket.HoldBusiness = true;
            var writer = new ArrayBufferWriter<byte>();
            GameNetRoutes.Echo.RequestCodec.Encode(writer, new EchoRequest());
            byte[] request = new byte[32 + writer.WrittenCount];
            FrameCodec.WriteHeader(request, new FrameHeader(FrameKind.Request, GameNetRoutes.Echo.Opcode, 2, writer.WrittenCount));
            writer.WrittenSpan.CopyTo(request.AsSpan(32));
            socket.Enqueue(request);
            await socket.NextSend(guard.Token);
        }

        [Test]
        public async Task SlowBusinessResponseCannotStarveTheServersQueuedPing()
        {
            await HoldEchoResponse();
            Advance(6000);
            await server.CheckHeartbeatAsync().WaitAsync(guard.Token);
            Advance(20999);
            await server.CheckHeartbeatAsync();
            Advance(21000);
            NetworkException? error = Assert.ThrowsAsync<NetworkException>(async () => await server.CheckHeartbeatAsync());
            Assert.That(error?.Message, Is.EqualTo("Control frame send deadline elapsed."));
        }

        [Test]
        public async Task ExpiredQueuedControlIsRejectedAtTransferWithoutWatchdogTick()
        {
            await HoldEchoResponse();
            Advance(6000);
            await server.CheckHeartbeatAsync();
            Advance(21000);
            socket.ReleaseSend();
            await running.WaitAsync(guard.Token);
            Assert.That(socket.TryNextSend(out _), Is.False, "The expired Ping must never be passed to WebSocket Send.");
            Assert.That(memory.OwnedBytes, Is.Zero);
        }
    }
}
