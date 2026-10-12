using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Threading.Channels;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;

namespace GameFrame.Server.Network
{

    internal sealed class ServerConnection
    {
        private sealed class SendItem
        {
            public readonly OwnedFrame Frame;
            public readonly bool Control;
            public readonly bool CloseOutput;
            public readonly TaskCompletionSource? Completion;

            public SendItem(OwnedFrame frame, bool control, bool closeOutput, bool waitForSend)
            {
                Frame = frame;
                Control = control;
                CloseOutput = closeOutput;
                Completion = waitForSend ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            }
        }

        private readonly object queueGate = new object();
        private readonly WssServerOptions settings;
        private readonly ServerApplication application;
        internal ServerConnectionContext Context { get; }
        private Exception? failure;
        private Exception? cleanupFailure;
        internal Exception? CleanupFailure => Volatile.Read(ref cleanupFailure);
        private int closeReason;
        private readonly FrameMemoryBudget memory;
        private readonly FrameMemoryBudget sendMemory;
        private readonly FrameMemoryBudget controlMemory;
        private readonly FrameMemoryBudget receiveMemory;
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationTokenSource sendingLifetime;
        private readonly Channel<OwnedFrame> received;
        private readonly Channel<SendItem> businessSend;
        private readonly Channel<SendItem> controlSend;
        private readonly SemaphoreSlim sendSignal = new SemaphoreSlim(0);
        private readonly TaskCompletionSource finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource peerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocket? socket;
        private int reliableLimit = FrameCodec.MaxReliableFrameBytes;
        private int fullTrafficOnce;
        private int terminating;
        private volatile bool inputCompleted;
        private volatile bool transportEnded;
        private int receiveCount;
        private int receiveBytes;
        private int businessCount;
        private int businessBytes;
        private int controlCount;
        private int controlBytes;
        private long lastPing;
        private long pongDeadline;
        private ulong pendingNonce;
        private ulong nextNonce;
        private long rateWindow;
        private int ratePackets;
        private int rateBytes;
        private ulong lastCorrelation;
        private SendItem? inFlightControl;
        private readonly ILogger logger;
        private readonly Func<long> getMilliseconds;
        private readonly Func<int, CancellationToken, Task> delay;

        public bool WasPromoted => Volatile.Read(ref fullTrafficOnce) != 0;
        public Task Finished => finished.Task;

        public ServerConnection(WssServerOptions settings, ServerApplication application, FrameMemoryBudget memory,
            Action promoted, CancellationToken requestAborted, ILogger logger, ulong id, IPEndPoint remote,
            Func<long>? testClock = null, Func<int, CancellationToken, Task>? testDelay = null)
        {
            this.settings = settings;
            this.application = application;
            application.Freeze();
            this.memory = memory;
            sendMemory = new FrameMemoryBudget(settings.BusinessQueueBytes, memory);
            controlMemory = new FrameMemoryBudget(settings.ControlQueueBytes, memory);
            receiveMemory = new FrameMemoryBudget(settings.ReceiveQueueBytes, memory);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            sendingLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            received = Channel.CreateBounded<OwnedFrame>(settings.ReceiveQueueCount);
            businessSend = Channel.CreateBounded<SendItem>(settings.BusinessQueueCount);
            controlSend = Channel.CreateBounded<SendItem>(settings.ControlQueueCount);
            this.logger = logger;
            getMilliseconds = testClock ?? NetworkClock.GetMilliseconds;
            Context = new ServerConnectionContext(id, 1, 0, NetworkTransportKind.WebSocket, remote, application.Catalog,
                sendMemory, lifetime.Token, frame =>
                {
                    if (frame.Delivery != NetDelivery.Reliable)
                    {
                        throw new NotSupportedException("WSS does not provide unreliable Push.");
                    }

                    if (transportEnded || lifetime.IsCancellationRequested)
                    {
                        return false;
                    }

                    _ = QueueSend(frame, false, waitForSend: false);
                    return true;
                }, Stop, () =>
                {
                    Interlocked.Exchange(ref fullTrafficOnce, 1);
                    promoted();
                });
            Context.ReportSecondaryFailure = error => logger.LogError(error, "Application response cleanup failed.");
            delay = testDelay ?? Task.Delay;
        }

        public async Task RunAsync(WebSocket accepted)
        {
            socket = accepted;
            var tasks = new List<Task>();
            try
            {
                application.Initialize(Context);
                using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    helloTimeout.CancelAfter(settings.HelloTimeout);
                    using OwnedFrame? hello = await WebSocketFrameTransport.ReceiveAsync(accepted, receiveMemory, reliableLimit,
                        FrameDirection.ClientToServer, true, helloTimeout.Token, header => ValidateControlHeader(header, false));
                    if (hello == null)
                    {
                        return;
                    }

                    if (hello.Header.Opcode == (uint)ControlOpcode.Close)
                    {
                        Interlocked.CompareExchange(ref closeReason, ControlCodec.ReadClose(hello.Body), 0);
                        await CloseSocketAsync(false);
                        return;
                    }

                    if (hello.Header.Opcode != (uint)ControlOpcode.ClientHello)
                    {
                        throw new NetworkProtocolException("First frame must be ClientHello.");
                    }

                    bool valid = ValidateHello(hello);
                    using OwnedFrame reply = CreateControl(valid ? ControlOpcode.ServerHello : ControlOpcode.Reject);
                    await WebSocketFrameTransport.SendAsync(accepted, reply, helloTimeout.Token);
                    if (!valid)
                    {
                        await CloseSocketAsync(false);
                        return;
                    }
                }

                await Context.Session.ConnectedAsync(Context);
                lastPing = getMilliseconds();
                Task sending = SendLoopAsync();
                Task receiving = ReceiveLoopAsync();
                Task dispatching = DispatchLoopAsync();
                Task heartbeat = HeartbeatLoopAsync();
                tasks.AddRange(new[] { sending, receiving, dispatching, heartbeat });
                Task first = await Task.WhenAny(tasks);
                await first;
                if ((ReferenceEquals(first, receiving) || ReferenceEquals(first, dispatching)) && inputCompleted && !lifetime.IsCancellationRequested)
                {
                    // EOF transfers ownership to the existing dispatch loop. Faults/watchdog still
                    // interrupt this drain; normal completion alone must not cancel accepted work.
                    await await Task.WhenAny(dispatching, sending, heartbeat);
                    await dispatching;
                    await CloseSocketAsync();
                    if (!peerClosed.Task.IsCompleted)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        using OwnedFrame? afterClose = await WebSocketFrameTransport.ReceiveAsync(socket!, receiveMemory,
                            reliableLimit, FrameDirection.ClientToServer, false, timeout.Token, ValidateBusinessHeader);
                        if (afterClose != null)
                        {
                            throw new NetworkProtocolException("Application input after orderly Close.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is NetworkException || error is WebSocketException || error is OperationCanceledException)
            {
                RecordFailure(error);
                logger.LogInformation("WSS connection ended: {ErrorType}", error.GetType().Name);
            }
            catch (Exception error)
            {
                RecordFailure(error);
                logger.LogError(error, "WSS connection failed.");
            }
            finally
            {
                Context.EndTraffic();
                Stop();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    RecordFailure(error);
                }
                finally
                {
                    Cleanup(Drain);
                    try
                    {
                        if (Context.Session != null)
                        {
                            await Context.Session.ClosedAsync(Context, new ServerCloseInfo((ushort)Volatile.Read(ref closeReason), failure));
                        }
                    }
                    catch (Exception error)
                    {
                        Interlocked.CompareExchange(ref cleanupFailure, error, null);
                        RecordFailure(error);
                    }
                }
            }
        }

        private bool ValidateHello(OwnedFrame frame)
        {
            ClientHelloBody hello = ControlCodec.ReadClientHello(frame.Body, NetworkTransportKind.WebSocket);
            if (!hello.Hash.SequenceEqual(application.Catalog.Hash) ||
                hello.ReliableLimit < application.Catalog.MaxReliableBodyBytes + FrameCodec.HeaderBytes)
            {
                return false;
            }

            reliableLimit = hello.ReliableLimit;
            return true;
        }

        private OwnedFrame CreateControl(ControlOpcode opcode, ulong nonce = 0, ushort reason = 0)
        {
            int bodySize = opcode == ControlOpcode.ServerHello ? 64 : opcode == ControlOpcode.Reject ? 36 :
                opcode == ControlOpcode.Close ? 4 : 8;
            OwnedFrame frame = controlMemory.Allocate(FrameCodec.HeaderBytes + bodySize);
            try
            {
                Span<byte> body = frame.Bytes.AsSpan(FrameCodec.HeaderBytes);
                if (opcode == ControlOpcode.ServerHello)
                {
                    byte[] id = RandomNumberGenerator.GetBytes(16);
                    ControlCodec.WriteServerHello(body, application.Catalog.Hash, id, reliableLimit, 0, 0, NetworkTransportKind.WebSocket);
                }
                else if (opcode == ControlOpcode.Reject)
                {
                    ControlCodec.WriteReject(body, 1, application.Catalog.Hash);
                }
                else if (opcode == ControlOpcode.Close)
                {
                    ControlCodec.WriteClose(body, reason);
                }
                else
                {
                    ControlCodec.WriteNonce(body, nonce);
                }

                frame.Header = new FrameHeader(FrameKind.Control, (uint)opcode, 0, bodySize);
                frame.Length = frame.Capacity;
                FrameCodec.WriteHeader(frame.Bytes, frame.Header);
                return frame;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        private async Task ReceiveLoopAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                OwnedFrame? frame = await WebSocketFrameTransport.ReceiveAsync(socket!, receiveMemory, reliableLimit,
                    FrameDirection.ClientToServer, false, lifetime.Token, ValidateBusinessHeader);
                if (frame == null)
                {
                    transportEnded = true;
                    sendingLifetime.Cancel();
                    peerClosed.TrySetResult();
                    if (Volatile.Read(ref terminating) == 0)
                    {
                        CompleteInput();
                    }

                    return;
                }

                bool transferred = false;
                try
                {
                    if (Volatile.Read(ref terminating) != 0)
                    {
                        throw new NetworkProtocolException("Business input after server termination.");
                    }

                    ApplyRateLimit(frame);
                    if (frame.Header.Kind == FrameKind.Control)
                    {
                        if (!await HandleControlAsync(frame))
                        {
                            return;
                        }

                        continue;
                    }

                    NetRoute route = application.Catalog.Find(frame.Header.Opcode);
                    if (route.Delivery != NetDelivery.Reliable || frame.Header.BodyLength > route.MaxBodyBytes ||
                        (frame.Header.Kind == FrameKind.Request ? route.Kind != NetRouteKind.Rpc : route.Kind != NetRouteKind.Notify))
                    {
                        throw new NetworkProtocolException("Route/body/channel mismatch.");
                    }

                    ServerAdmission admission = Context.Session.Admit(ServerApplication.Header(frame));
                    if (!admission.Allowed)
                    {
                        if (frame.Header.Kind == FrameKind.Request)
                        {
                            await QueueError(frame.Header, admission.StatusCode == 0 ? 2 : admission.StatusCode);
                        }

                        await TerminateAsync(admission.CloseReason == 0 ? (ushort)5 : admission.CloseReason);
                        return;
                    }

                    if (frame.Header.Kind == FrameKind.Request)
                    {
                        if (frame.Header.CorrelationId <= lastCorrelation)
                        {
                            throw new NetworkProtocolException("Request correlation IDs must strictly increase.");
                        }

                        lastCorrelation = frame.Header.CorrelationId;
                    }

                    lock (queueGate)
                    {
                        if (receiveCount >= settings.ReceiveQueueCount || frame.Capacity > settings.ReceiveQueueBytes - receiveBytes)
                        {
                            throw new NetworkBackpressureException("Reliable receive queue is full.");
                        }

                        if (!received.Writer.TryWrite(frame))
                        {
                            throw new NetworkBackpressureException("Reliable receive queue is full.");
                        }

                        receiveCount++;
                        receiveBytes += frame.Capacity;
                        transferred = true;
                    }
                }
                finally
                {
                    if (!transferred)
                    {
                        frame.Dispose();
                    }
                }
            }
        }

        private static void ValidateControlHeader(FrameHeader header, bool helloComplete)
        {
            ControlCodec.ValidatePhase((ControlOpcode)header.Opcode, helloComplete, FrameDirection.ClientToServer);
            ControlCodec.ValidateBodyLength((ControlOpcode)header.Opcode, header.BodyLength, NetworkTransportKind.WebSocket);
        }

        private void ValidateBusinessHeader(FrameHeader header)
        {
            if (header.Kind == FrameKind.Control)
            {
                ValidateControlHeader(header, true);
                return;
            }

            if (Context.IsRestricted && header.BodyLength > settings.RestrictedBodyBytes)
            {
                throw new NetworkProtocolException("Restricted body exceeds the configured limit.");
            }

            NetRoute route = application.Catalog.Find(header.Opcode);
            if (route.Delivery != NetDelivery.Reliable || header.BodyLength > route.MaxBodyBytes ||
                (header.Kind == FrameKind.Request ? route.Kind != NetRouteKind.Rpc : route.Kind != NetRouteKind.Notify))
            {
                throw new NetworkProtocolException("Route/body/channel mismatch.");
            }
        }

        private void ApplyRateLimit(OwnedFrame frame)
        {
            long window = getMilliseconds() / 1000;
            if (rateWindow != window)
            {
                rateWindow = window;
                ratePackets = 0;
                rateBytes = 0;
            }

            ratePackets++;
            rateBytes += frame.Length;
            bool fullTraffic = !Context.IsRestricted;
            if (ratePackets > (fullTraffic ? settings.MaxPacketsPerSecond : settings.RestrictedPacketsPerSecond) ||
                rateBytes > (fullTraffic ? settings.MaxBytesPerSecond : settings.RestrictedBytesPerSecond))
            {
                throw new NetworkBackpressureException("Connection ingress rate exceeded.");
            }
        }

        private async Task<bool> HandleControlAsync(OwnedFrame frame)
        {
            ControlOpcode opcode = (ControlOpcode)frame.Header.Opcode;
            ControlCodec.ValidatePhase(opcode, true, FrameDirection.ClientToServer);
            if (opcode == ControlOpcode.Ping)
            {
                await QueueSend(CreateControl(ControlOpcode.Pong, ControlCodec.ReadNonce(frame.Body)), true);
            }
            else if (opcode == ControlOpcode.Pong)
            {
                ulong nonce = ControlCodec.ReadNonce(frame.Body);
                lock (queueGate)
                {
                    if (pendingNonce == 0 || nonce != pendingNonce)
                    {
                        throw new NetworkProtocolException("Pong does not match the outstanding nonce.");
                    }

                    if (pongDeadline != 0 && getMilliseconds() >= pongDeadline)
                    {
                        throw new NetworkException("Application heartbeat timed out.");
                    }

                    pendingNonce = 0;
                    pongDeadline = 0;
                }
            }
            else
            {
                Interlocked.CompareExchange(ref closeReason, ControlCodec.ReadClose(frame.Body), 0);
                CompleteInput();
                return false;
            }

            return true;
        }

        private void CompleteInput()
        {
            inputCompleted = true;
            received.Writer.TryComplete();
        }

        private async Task DispatchLoopAsync()
        {
            while (await received.Reader.WaitToReadAsync(lifetime.Token))
            {
                while (received.Reader.TryRead(out OwnedFrame? frame))
                {
                    try
                    {
                        try
                        {
                            await DispatchAsync(frame);
                        }
                        catch (Exception error) when (error is not NetworkException && error is not OperationCanceledException &&
                            error is not WebSocketException)
                        {
                            RecordFailure(error);
                            logger.LogError(error, "WSS handler failed for opcode {Opcode}.", frame.Header.Opcode);
                            if (frame.Header.Kind == FrameKind.Request)
                            {
                                await QueueError(frame.Header, 3);
                            }

                            await TerminateAsync(1);
                        }
                    }
                    finally
                    {
                        lock (queueGate)
                        {
                            receiveCount--;
                            receiveBytes -= frame.Capacity;
                        }

                        frame.Dispose();
                    }
                }
            }
        }

        private async Task DispatchAsync(OwnedFrame frame)
        {
            OwnedFrame? response = await application.DispatchAsync(Context, frame, sendMemory);
            if (response != null)
            {
                await QueueSend(response, false);
            }

            ushort reason = Context.TakeCloseReason();
            if (reason != 0)
            {
                await TerminateAsync(reason);
            }
        }

        private Task QueueError(FrameHeader request, int status)
        {
            OwnedFrame frame = sendMemory.Allocate(FrameCodec.HeaderBytes);
            var writer = new BoundedFrameWriter(frame);
            writer.Complete(FrameKind.Response, request.Opcode, request.CorrelationId, status);
            return QueueSend(frame, false);
        }

        private Task QueueSend(OwnedFrame frame, bool control, bool closeOutput = false, bool waitForSend = true)
        {
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (transportEnded && !closeOutput)
                {
                    frame.Dispose();
                    return Task.CompletedTask;
                }
                var item = new SendItem(frame, control, closeOutput, waitForSend);
                lock (queueGate)
                {
                    int count = control ? controlCount : businessCount;
                    int bytes = control ? controlBytes : businessBytes;
                    int countLimit = control ? settings.ControlQueueCount : settings.BusinessQueueCount;
                    int bytesLimit = control ? settings.ControlQueueBytes : settings.BusinessQueueBytes;
                    if (count >= countLimit || frame.Capacity > bytesLimit - bytes)
                    {
                        throw new NetworkBackpressureException("Reliable send queue is full.");
                    }

                    frame.AdmittedAt = getMilliseconds();
                    if (!(control ? controlSend : businessSend).Writer.TryWrite(item))
                    {
                        throw new NetworkBackpressureException("Reliable send queue is full.");
                    }

                    if (control)
                    {
                        controlCount++;
                        controlBytes += frame.Capacity;
                    }
                    else
                    {
                        businessCount++;
                        businessBytes += frame.Capacity;
                    }
                }

                sendSignal.Release();
                return item.Completion?.Task ?? Task.CompletedTask;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        private async Task SendLoopAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await sendSignal.WaitAsync(lifetime.Token);
                SendItem? item;
                lock (queueGate)
                {
                    if (!controlSend.Reader.TryRead(out item) && !businessSend.Reader.TryRead(out item))
                    {
                        continue;
                    }

                    if (item.Control)
                    {
                        inFlightControl = item;
                    }
                }

                try
                {
                    if (transportEnded && !item.CloseOutput)
                    {
                        item.Completion?.TrySetResult();
                        continue;
                    }
                    if (item.Control && getMilliseconds() - item.Frame.AdmittedAt >= 15000)
                    {
                        throw new NetworkException("Control frame send deadline elapsed.");
                    }
                    if (!item.Control && getMilliseconds() - item.Frame.AdmittedAt >= 250)
                    {
                        throw new NetworkBackpressureException("Reliable server send queue age exceeded.");
                    }

                    if (item.CloseOutput)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await socket!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                    }
                    else
                    {
                        await WebSocketFrameTransport.SendAsync(socket!, item.Frame, sendingLifetime.Token);
                    }

                    lock (queueGate)
                    {
                        if (item.Frame.Header.Kind == FrameKind.Control && item.Frame.Header.Opcode == (uint)ControlOpcode.Ping &&
                            pendingNonce != 0 && pendingNonce == ControlCodec.ReadNonce(item.Frame.Body))
                        {
                            lastPing = getMilliseconds();
                            pongDeadline = lastPing + 15000;
                        }
                    }

                    item.Completion?.TrySetResult();
                }
                catch (Exception error)
                {
                    if (transportEnded && error is OperationCanceledException && !lifetime.IsCancellationRequested)
                    {
                        item.Completion?.TrySetResult();
                        continue;
                    }
                    item.Completion?.TrySetException(error);
                    throw;
                }
                finally
                {
                    ReleaseSend(item);
                }
            }
        }

        private void ReleaseSend(SendItem item)
        {
            lock (queueGate)
            {
                if (item.Control)
                {
                    if (ReferenceEquals(inFlightControl, item))
                    {
                        inFlightControl = null;
                    }

                    controlCount--;
                    controlBytes -= item.Frame.Capacity;
                }
                else
                {
                    businessCount--;
                    businessBytes -= item.Frame.Capacity;
                }
            }

            item.Frame.Dispose();
        }

        private async Task HeartbeatLoopAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await delay(50, lifetime.Token);
                await CheckHeartbeatAsync();
            }
        }

        internal async Task CheckHeartbeatAsync()
        {
            long now = getMilliseconds();
            ushort? applicationReason = Context.Session.CheckDeadline(now);
            if (applicationReason.HasValue)
            {
                await TerminateAsync(applicationReason.Value);
                return;
            }

            ulong sendNonce = 0;
            lock (queueGate)
            {
                SendItem? oldestControl = inFlightControl;
                if (oldestControl == null)
                {
                    controlSend.Reader.TryPeek(out oldestControl);
                }

                if (oldestControl != null && now - oldestControl.Frame.AdmittedAt >= 15000)
                {
                    throw new NetworkException("Control frame send deadline elapsed.");
                }

                if (!inputCompleted && pendingNonce != 0 && pongDeadline != 0 && now >= pongDeadline)
                {
                    throw new NetworkException("Application heartbeat timed out.");
                }

                if (!inputCompleted && pendingNonce == 0 && now - lastPing >= 5000)
                {
                    sendNonce = nextNonce = checked(nextNonce + 1);
                    pendingNonce = sendNonce;
                    lastPing = now;
                }
            }

            if (sendNonce != 0)
            {
                // The send owner reports failures; the watchdog must keep running during slow sends.
                _ = QueueSend(CreateControl(ControlOpcode.Ping, sendNonce), true, waitForSend: false);
            }
        }

        private async Task TerminateAsync(ushort reason)
        {
            Interlocked.CompareExchange(ref closeReason, reason, 0);
            if (Interlocked.Exchange(ref terminating, 1) != 0)
            {
                await Task.Delay(Timeout.Infinite, lifetime.Token);
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await QueueSend(CreateControl(ControlOpcode.Close, reason: reason), true).WaitAsync(timeout.Token);
                await CloseSocketAsync();
                // Keep the receive owner alive for the peer's close acknowledgement. Abort before
                // this boundary can reset TLS/TCP while a failure response is still in transit.
                await peerClosed.Task.WaitAsync(timeout.Token);
            }
            finally
            {
                Stop();
            }
        }

        private async Task CloseSocketAsync(bool sendOwnerStarted = true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (sendOwnerStarted)
            {
                await QueueSend(controlMemory.Allocate(FrameCodec.HeaderBytes), true, true).WaitAsync(timeout.Token);
            }
            else
            {
                await socket!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
            }
        }

        public void Stop()
        {
            lock (finished)
            {
                if (finished.Task.IsCompleted)
                {
                    return;
                }
            }

            Cleanup(() => lifetime.Cancel());
            Cleanup(() => socket?.Abort());
        }

        private void RecordFailure(Exception error)
        {
            Exception? previous = Interlocked.CompareExchange(ref failure, error, null);
            if (previous != null && !ReferenceEquals(previous, error))
            {
                logger.LogError(error, "Additional WSS cleanup failure.");
            }
        }

        private void Cleanup(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref cleanupFailure, error, null);
                RecordFailure(error);
            }
        }

        private void Drain()
        {
            while (received.Reader.TryRead(out OwnedFrame? frame))
            {
                frame.Dispose();
            }

            foreach (Channel<SendItem> queue in new[] { controlSend, businessSend })
            {
                while (queue.Reader.TryRead(out SendItem? item))
                {
                    item.Completion?.TrySetCanceled();
                    ReleaseSend(item);
                }
            }
        }

        public void Finish()
        {
            lock (finished)
            {
                if (finished.Task.IsCompleted)
                {
                    return;
                }

                Cleanup(lifetime.Dispose);
                Cleanup(sendingLifetime.Dispose);
                Cleanup(sendSignal.Dispose);
                Context.Complete(new ServerCloseInfo((ushort)Volatile.Read(ref closeReason), failure));
                finished.TrySetResult();
            }
        }
    }
}
