using System.Buffers;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading.Channels;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Network.Connection;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class ConnectionRaceTests
    {
        internal sealed class ControlledSocket : WebSocket
        {
            private readonly Channel<byte[]> inbound = Channel.CreateBounded<byte[]>(32);
            private readonly Channel<byte[]> started = Channel.CreateBounded<byte[]>(32);
            private readonly Channel<byte[]> completed = Channel.CreateBounded<byte[]>(32);
            private readonly FrameDirection outgoingDirection;
            private readonly TaskCompletionSource released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private byte[]? receiving;
            private int position;
            private WebSocketState state = WebSocketState.Open;
            public bool HoldBusiness;
            public bool HoldPing;
            public Exception? DisposeFailure;
            public int Disposals;
            public int CloseOutputs;
            public bool AcknowledgeCloseOutput;
            public bool FailClosedSends;
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => state;
            public override string? SubProtocol => null;

            public ControlledSocket(FrameDirection outgoingDirection = FrameDirection.ClientToServer)
            {
                this.outgoingDirection = outgoingDirection;
            }

            public void Enqueue(byte[] frame) => Assert.That(inbound.Writer.TryWrite(frame), Is.True);
            public void EnqueueTransportClose() => Enqueue(Array.Empty<byte>());
            public Task<byte[]> NextSend(CancellationToken token) => started.Reader.ReadAsync(token).AsTask();
            public bool TryNextSend(out byte[]? frame) => started.Reader.TryRead(out frame);
            public Task<byte[]> NextCompletedSend(CancellationToken token) => completed.Reader.ReadAsync(token).AsTask();
            public void ReleaseSend() => released.TrySetResult();

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] frame = buffer.ToArray();
                FrameHeader header = FrameCodec.ReadHeader(frame, 32768, NetDelivery.Reliable, outgoingDirection, false);
                Assert.That(started.Writer.TryWrite(frame), Is.True);
                if ((HoldBusiness && header.Kind != FrameKind.Control) ||
                    (HoldPing && header.Kind == FrameKind.Control && header.Opcode == (uint)ControlOpcode.Ping))
                {
                    await released.Task.WaitAsync(cancellationToken);
                }

                if (FailClosedSends && state != WebSocketState.Open)
                {
                    throw new WebSocketException("Transport ended during the in-flight send.");
                }
                Assert.That(completed.Writer.TryWrite(frame), Is.True);
            }

            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            {
                receiving ??= await inbound.Reader.ReadAsync(cancellationToken);
                if (receiving.Length == 0)
                {
                    receiving = null;
                    state = WebSocketState.CloseReceived;
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }
                int count = Math.Min(buffer.Count, receiving.Length - position);
                receiving.AsSpan(position, count).CopyTo(buffer.AsSpan());
                position += count;
                bool end = position == receiving.Length;
                if (end)
                {
                    receiving = null;
                    position = 0;
                }

                return new WebSocketReceiveResult(count, WebSocketMessageType.Binary, end);
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
            {
                CloseOutputs++;
                state = state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
                if (AcknowledgeCloseOutput)
                {
                    EnqueueTransportClose();
                }
                return Task.CompletedTask;
            }

            public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) =>
                CloseOutputAsync(status, description, token);

            public override void Abort() => state = WebSocketState.Aborted;
            public override void Dispose()
            {
                Interlocked.Increment(ref Disposals);
                state = WebSocketState.Closed;
                if (DisposeFailure != null)
                {
                    throw DisposeFailure;
                }
            }
        }

        private ControlledSocket socket = null!;
        private ConnectionContext client = null!;
        private FrameMemoryBudget memory = null!;
        private CancellationTokenSource guard = null!;
        private long now;

        [SetUp]
        public async Task Connect()
        {
            now = 1000;
            guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            memory = new FrameMemoryBudget(64 * 1024 * 1024);
            socket = new ControlledSocket();
            byte[] hello = new byte[96];
            FrameCodec.WriteHeader(hello, new FrameHeader(FrameKind.Control, (uint)ControlOpcode.ServerHello, 0, 64));
            ControlCodec.WriteServerHello(hello.AsSpan(32), GameNetProtocol.Catalog.Hash, new byte[16],
                32768, 0, 0, NetworkTransportKind.WebSocket);
            socket.Enqueue(hello);
            client = new ConnectionContext(1, new NetworkClientOptions(), GameNetProtocol.Catalog,
                NetworkConnectOptions.WebSocket(new Uri("wss://localhost/network")), memory, default, socket,
                () => Interlocked.Read(ref now), (_, token) => Task.Delay(Timeout.Infinite, token),
                (_, _) => Task.CompletedTask);
            client.Start();
            await PumpUntil(client.ConnectTask);
            FrameHeader sent = Header(await socket.NextSend(guard.Token));
            Assert.That(sent.Opcode, Is.EqualTo((uint)ControlOpcode.ClientHello));
        }

        [TearDown]
        public async Task Close()
        {
            client.BeginClose();
            if (socket.DisposeFailure == null)
            {
                await PumpUntil(client.CloseTask);
            }
            else
            {
                Exception? error = Assert.ThrowsAsync<InvalidOperationException>(async () => await PumpUntil(client.CloseTask));
                Assert.That(error, Is.SameAs(socket.DisposeFailure));
            }
            Assert.That(client.IoTask.IsCompleted, Is.True);
            Assert.That(memory.OwnedBytes, Is.Zero);
            guard.Dispose();
        }

        private static FrameHeader Header(byte[] frame) => FrameCodec.ReadHeader(frame, 32768, NetDelivery.Reliable,
            FrameDirection.ClientToServer, false);

        private async Task WaitFor(Func<bool> condition)
        {
            while (!condition())
            {
                await Task.Delay(1, guard.Token);
            }
        }

        private async Task PumpUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                for (int i = 0; i < 16 && client.PumpOne(); i++)
                {
                }

                if (!task.IsCompleted)
                {
                    await Task.Delay(1, guard.Token);
                }
            }

            await task;
        }

        private void Advance(long timestamp)
        {
            Assert.That(timestamp, Is.GreaterThanOrEqualTo(now));
            Interlocked.Exchange(ref now, timestamp);
        }

        private Exception? Tick()
        {
            try
            {
                client.CheckClock();
                return null;
            }
            catch (Exception error)
            {
                // Match the production IO owner's exception boundary, with time controlled by the test.
                client.Fail(error);
                return error;
            }
        }

        private void Respond(ulong correlation, bool malformed = false)
        {
            var writer = new ArrayBufferWriter<byte>();
            GameNetRoutes.Echo.ResponseCodec.Encode(writer, new EchoResponse { Text = "result" });
            byte[] body = malformed ? new byte[] { 255 } : writer.WrittenSpan.ToArray();
            byte[] frame = new byte[32 + body.Length];
            FrameCodec.WriteHeader(frame, new FrameHeader(FrameKind.Response, GameNetRoutes.Echo.Opcode, correlation, body.Length));
            body.CopyTo(frame, 32);
            socket.Enqueue(frame);
        }

        private void EnqueueClose()
        {
            byte[] frame = new byte[36];
            FrameCodec.WriteHeader(frame, new FrameHeader(FrameKind.Control, (uint)ControlOpcode.Close, 0, 4));
            ControlCodec.WriteClose(frame.AsSpan(32), 0);
            socket.Enqueue(frame);
        }

        [Test]
        public async Task RemoteTransportCloseAcknowledgesOnceAndPreservesEarlierResponse()
        {
            Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            FrameHeader sent = Header(await socket.NextSend(guard.Token));
            Respond(sent.CorrelationId);
            EnqueueClose();
            socket.EnqueueTransportClose();
            await WaitFor(() => client.IoTask.IsCompleted);
            await PumpUntil(request);
            Assert.That(request.Result.Text, Is.EqualTo("result"));
            Assert.That(socket.CloseOutputs, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TransportEofStopsAdmissionAndQueuedSendsBeforeAcknowledgement(bool failInFlight)
        {
            socket.HoldBusiness = true;
            socket.FailClosedSends = failInFlight;
            Task<EchoResponse> first = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            FrameHeader sent = Header(await socket.NextSend(guard.Token));
            Task<EchoResponse> queued = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            Respond(sent.CorrelationId);
            socket.EnqueueTransportClose();
            await WaitFor(() => socket.State == WebSocketState.CloseReceived);
            await WaitFor(() => (bool)typeof(ConnectionContext).GetField("stopped", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!);
            Assert.Throws<InvalidOperationException>(() => client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default));
            socket.ReleaseSend();
            await WaitFor(() => client.IoTask.IsCompleted);
            await PumpUntil(first);
            Assert.That(first.Result.Text, Is.EqualTo("result"));
            Assert.ThrowsAsync<NetworkException>(async () => await PumpUntil(queued));
            Assert.That(socket.TryNextSend(out _), Is.False);
            Assert.That(socket.CloseOutputs, Is.EqualTo(1));
        }

        [Test]
        public async Task ExplicitCloseWaitsForTransportAcknowledgementWithoutASecondCloseOutput()
        {
            socket.AcknowledgeCloseOutput = true;
            client.BeginClose();
            await PumpUntil(client.CloseTask);
            Assert.That(socket.CloseOutputs, Is.EqualTo(1));
            Assert.That(client.IoTask.IsCompleted, Is.True);
        }

        [Test]
        public async Task PushFaultSupersedesAlreadyReceivedOrderlyClose()
        {
            var expected = new InvalidOperationException("push failure");
            int calls = 0;
            Exception? observed = null;
            client.Push = (_, _) =>
            {
                calls++;
                throw expected;
            };
            client.StateChanged = (_, error) => observed = error ?? observed;
            for (int i = 0; i < 2; i++)
            {
                byte[] frame = new byte[33];
                FrameCodec.WriteHeader(frame, new FrameHeader(FrameKind.Push, GameNetRoutes.WorldSnapshotReliable.Opcode, 0, 1));
                socket.Enqueue(frame);
            }

            EnqueueClose();
            await WaitFor(() => client.IoTask.IsCompleted);
            while (client.PumpOne())
            {
            }

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Faulted));
            Assert.That(observed, Is.SameAs(expected));
        }

        [Test]
        public async Task ResponseDecodeFaultFailsLaterReservedResponseBeforeOrderlyClose()
        {
            Task<EchoResponse> first = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            FrameHeader firstHeader = Header(await socket.NextSend(guard.Token));
            Task<EchoResponse> second = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            FrameHeader secondHeader = Header(await socket.NextSend(guard.Token));
            Respond(firstHeader.CorrelationId, malformed: true);
            Respond(secondHeader.CorrelationId);
            EnqueueClose();
            await WaitFor(() => client.IoTask.IsCompleted);
            while (client.PumpOne())
            {
            }

            Assert.That(first.IsFaulted, Is.True);
            Assert.That(second.IsFaulted, Is.True);
            Assert.That(second.Exception!.InnerException, Is.SameAs(first.Exception!.InnerException));
        }

        [TestCase(1999, true)]
        [TestCase(2000, false)]
        [TestCase(2001, false)]
        public async Task ResponseArrivalUsesStrictDeadlineBoundary(long arrival, bool success)
        {
            Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            FrameHeader sent = Header(await socket.NextSend(guard.Token));
            Advance(arrival);
            Respond(sent.CorrelationId, malformed: !success);
            await WaitFor(() => success ? client.Metrics().ReliableReceiveCount == 1 : client.Metrics().RequestTimeouts == 1);
            Assert.That(request.IsCompleted, Is.False, "Only the main-thread owner can deliver the result.");
            Assert.That(client.Metrics().DeadlineCount, Is.Zero);
            if (success)
            {
                Advance(2010);
                Assert.That(Tick(), Is.Null, "A reserved response must survive a later deadline tick.");
                await PumpUntil(request);
                Assert.That(request.Result.Text, Is.EqualTo("result"));
            }
            else
            {
                Assert.ThrowsAsync<NetworkRequestTimeoutException>(async () => await PumpUntil(request));
                Assert.That(client.Metrics().LateResponses, Is.EqualTo(1));
                Assert.That(client.State, Is.EqualTo(NetworkClientState.Connected), "Expired malformed DTO must never be decoded.");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task DeadlineRemovesQueuedRequestBeforeItReachesSlowSocket(bool runClock)
        {
            socket.HoldBusiness = true;
            using var cancellation = new CancellationTokenSource();
            Task<EchoResponse> first = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromMinutes(1), cancellation.Token);
            await socket.NextSend(guard.Token);
            Task<EchoResponse> queued = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromMilliseconds(100), default);
            Advance(1100);
            if (runClock)
            {
                Assert.That(Tick(), Is.Null);
            }

            socket.ReleaseSend();
            await WaitFor(() => client.Metrics().ReliableSendCount == 0);
            Assert.ThrowsAsync<NetworkRequestTimeoutException>(async () => await PumpUntil(queued));
            Assert.That(client.Metrics().SentFrames, Is.EqualTo(1), "Only the already in-flight request is sent.");
            cancellation.Cancel();
            Assert.ThrowsAsync<TaskCanceledException>(async () => await PumpUntil(first));
        }

        [Test]
        public async Task SlowBusinessSendCannotPostponeQueuedControlDeadline()
        {
            socket.HoldBusiness = true;
            Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromMinutes(1), default);
            await socket.NextSend(guard.Token);
            Advance(6000);
            Assert.That(Tick(), Is.Null);
            Advance(20999);
            Assert.That(Tick(), Is.Null);
            Advance(21000);
            Exception? error = Tick();
            Assert.That(error?.Message, Is.EqualTo("Control frame send deadline elapsed."));
            Exception? requestError = Assert.ThrowsAsync<NetworkException>(async () => await PumpUntil(request));
            Assert.That(requestError, Is.SameAs(error));
        }

        [Test]
        public async Task SlowControlSendCannotWaitBeyondItsOwnDeadline()
        {
            socket.HoldPing = true;
            Advance(6000);
            Assert.That(Tick(), Is.Null);
            Assert.That(Header(await socket.NextSend(guard.Token)).Opcode, Is.EqualTo((uint)ControlOpcode.Ping));
            Advance(20999);
            Assert.That(Tick(), Is.Null);
            Advance(21000);
            Assert.That(Tick()?.Message, Is.EqualTo("Control frame send deadline elapsed."));
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Faulted));
        }

        [Test]
        public async Task PongDeadlineStartsWhenPingSendCompletesAndDoesNotMoveWithoutPong()
        {
            socket.HoldPing = true;
            Advance(6000);
            Assert.That(Tick(), Is.Null);
            await socket.NextSend(guard.Token);
            Advance(20999);
            Assert.That(Tick(), Is.Null);
            socket.ReleaseSend();
            await WaitFor(() => client.Metrics().SentFrames == 1);
            Advance(35998);
            Assert.That(Tick(), Is.Null);
            Assert.That(client.Metrics().SentFrames, Is.EqualTo(1), "No replacement Ping may reset the outstanding nonce.");
            Advance(35999);
            Assert.That(Tick()?.Message, Is.EqualTo("Application heartbeat timed out."));
        }

        [Test]
        public async Task ControlQueueHasPriorityAfterInFlightBusinessSendReturns()
        {
            socket.HoldBusiness = true;
            using var cancellation = new CancellationTokenSource();
            Task<EchoResponse> first = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromMinutes(1), cancellation.Token);
            await socket.NextSend(guard.Token);
            Advance(6000);
            Task<EchoResponse> second = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromMinutes(1), cancellation.Token);
            Assert.That(Tick(), Is.Null);
            socket.ReleaseSend();
            Assert.That(Header(await socket.NextSend(guard.Token)).Opcode, Is.EqualTo((uint)ControlOpcode.Ping));
            Assert.That(Header(await socket.NextSend(guard.Token)).Opcode, Is.EqualTo(GameNetRoutes.Echo.Opcode));
            cancellation.Cancel();
            Assert.ThrowsAsync<TaskCanceledException>(async () => await PumpUntil(first));
            Assert.ThrowsAsync<TaskCanceledException>(async () => await PumpUntil(second));
        }

        [Test]
        public async Task UnmatchedPongFailsTheConnectionWithTheOriginalProtocolError()
        {
            byte[] pong = new byte[40];
            FrameCodec.WriteHeader(pong, new FrameHeader(FrameKind.Control, (uint)ControlOpcode.Pong, 0, 8));
            ControlCodec.WriteNonce(pong.AsSpan(32), 99);
            socket.Enqueue(pong);
            await WaitFor(() => client.IoTask.IsCompleted);
            Exception? observed = null;
            client.StateChanged = (_, error) => observed = error;
            while (client.PumpOne())
            {
            }

            Assert.That(client.State, Is.EqualTo(NetworkClientState.Faulted));
            Assert.That(observed, Is.TypeOf<NetworkProtocolException>());
            Assert.That(observed?.Message, Is.EqualTo("Unmatched Pong."));
        }

        [Test]
        public async Task MatchingPongAtDeadlineCannotBeatTheWatchdog()
        {
            Advance(6000);
            Assert.That(Tick(), Is.Null);
            byte[] ping = await socket.NextSend(guard.Token);
            await WaitFor(() => client.Metrics().SentFrames == 1);
            Advance(21000);
            byte[] pong = new byte[40];
            FrameCodec.WriteHeader(pong, new FrameHeader(FrameKind.Control, (uint)ControlOpcode.Pong, 0, 8));
            ControlCodec.WriteNonce(pong.AsSpan(32), ControlCodec.ReadNonce(ping.AsSpan(32)));
            socket.Enqueue(pong);
            await WaitFor(() => client.IoTask.IsCompleted);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Faulted));
        }

        [Test]
        public async Task DisposeFailureStillCompletesCloseAndReleasesFramesWithoutRetry()
        {
            socket.HoldBusiness = true;
            Task<EchoResponse> request = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(2), default);
            await socket.NextSend(guard.Token);
            socket.DisposeFailure = new InvalidOperationException("Injected transport disposal failure.");
            client.BeginClose();
            Exception? error = Assert.ThrowsAsync<InvalidOperationException>(async () => await PumpUntil(client.CloseTask));
            Assert.That(error, Is.SameAs(socket.DisposeFailure));
            Assert.That(request.IsCompleted, Is.True);
            Assert.That(memory.OwnedBytes, Is.Zero);
            Assert.That(socket.Disposals, Is.EqualTo(1));
            Assert.That(client.IoTask.IsCompletedSuccessfully, Is.True);
        }
    }
}
