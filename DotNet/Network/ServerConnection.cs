using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
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
        private readonly ServerSettings settings;
        private readonly AccountSessions accounts;
        private readonly FrameMemoryBudget memory;
        private readonly FrameMemoryBudget sendMemory;
        private readonly FrameMemoryBudget controlMemory;
        private readonly FrameMemoryBudget receiveMemory;
        private readonly Func<bool> admitLogin;
        private readonly Action authenticated;
        private readonly CancellationTokenSource lifetime;
        private readonly Channel<OwnedFrame> received;
        private readonly Channel<SendItem> businessSend;
        private readonly Channel<SendItem> controlSend;
        private readonly SemaphoreSlim sendSignal = new SemaphoreSlim(0);
        private readonly TaskCompletionSource finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource peerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocket? socket;
        private int reliableLimit = FrameCodec.MaxReliableFrameBytes;
        private long binding;
        private int authenticatedOnce;
        private int loginInFlight;
        private int terminating;
        private int receiveCount;
        private int receiveBytes;
        private int businessCount;
        private int businessBytes;
        private int controlCount;
        private int controlBytes;
        private long loginDeadline;
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

        public bool WasAuthenticated => Volatile.Read(ref authenticatedOnce) != 0;
        public Task Finished => finished.Task;

        public ServerConnection(ServerSettings settings, AccountSessions accounts, FrameMemoryBudget memory,
            Func<bool> admitLogin, Action authenticated, CancellationToken requestAborted, ILogger logger,
            Func<long>? testClock = null, Func<int, CancellationToken, Task>? testDelay = null)
        {
            this.settings = settings;
            this.accounts = accounts;
            this.memory = memory;
            sendMemory = new FrameMemoryBudget(settings.BusinessQueueBytes, memory);
            controlMemory = new FrameMemoryBudget(settings.ControlQueueBytes, memory);
            receiveMemory = new FrameMemoryBudget(settings.ReceiveQueueBytes, memory);
            this.admitLogin = admitLogin;
            this.authenticated = authenticated;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            received = Channel.CreateBounded<OwnedFrame>(settings.ReceiveQueueCount);
            businessSend = Channel.CreateBounded<SendItem>(settings.BusinessQueueCount);
            controlSend = Channel.CreateBounded<SendItem>(settings.ControlQueueCount);
            this.logger = logger;
            getMilliseconds = testClock ?? (() => AccountSessions.Now);
            delay = testDelay ?? Task.Delay;
        }

        public async Task RunAsync(WebSocket accepted)
        {
            socket = accepted;
            var tasks = new List<Task>();
            try
            {
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
                        ControlCodec.ReadClose(hello.Body);
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

                loginDeadline = getMilliseconds() + (long)settings.LoginTimeout.TotalMilliseconds;
                lastPing = getMilliseconds();
                tasks.Add(SendLoopAsync());
                tasks.Add(ReceiveLoopAsync());
                tasks.Add(DispatchLoopAsync());
                tasks.Add(HeartbeatLoopAsync());
                await await Task.WhenAny(tasks);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is NetworkException || error is WebSocketException || error is OperationCanceledException)
            {
                logger.LogInformation("WSS connection ended: {ErrorType}", error.GetType().Name);
            }
            catch (Exception error)
            {
                logger.LogError(error, "WSS connection failed.");
            }
            finally
            {
                Stop();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is NetworkException)
                {
                }
                finally
                {
                    Drain();
                    accounts.Detach(Interlocked.Read(ref binding));
                }
            }
        }

        private bool ValidateHello(OwnedFrame frame)
        {
            ClientHelloBody hello = ControlCodec.ReadClientHello(frame.Body, NetworkTransportKind.WebSocket);
            if (!hello.Hash.SequenceEqual(GameNetProtocol.Catalog.Hash) ||
                hello.ReliableLimit < GameNetProtocol.Catalog.MaxReliableBodyBytes + FrameCodec.HeaderBytes)
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
                    ControlCodec.WriteServerHello(body, GameNetProtocol.Catalog.Hash, id, reliableLimit, 0, 0, NetworkTransportKind.WebSocket);
                }
                else if (opcode == ControlOpcode.Reject)
                {
                    ControlCodec.WriteReject(body, 1, GameNetProtocol.Catalog.Hash);
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
                    peerClosed.TrySetResult();
                    if (Volatile.Read(ref terminating) == 0)
                    {
                        await CloseSocketAsync();
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

                    NetRoute route = GameNetProtocol.Catalog.Find(frame.Header.Opcode);
                    if (route.Delivery != NetDelivery.Reliable || frame.Header.BodyLength > route.MaxBodyBytes ||
                        (frame.Header.Kind == FrameKind.Request ? route.Kind != NetRouteKind.Rpc : route.Kind != NetRouteKind.Notify))
                    {
                        throw new NetworkProtocolException("Route/body/channel mismatch.");
                    }

                    bool login = ReferenceEquals(route, GameNetRoutes.Login) || ReferenceEquals(route, GameNetRoutes.ResumeLogin);
                    long current = Interlocked.Read(ref binding);
                    bool allowed = current == 0 ? login && frame.Header.BodyLength <= 2048 &&
                        Interlocked.CompareExchange(ref loginInFlight, 1, 0) == 0 && admitLogin() :
                        !login && accounts.IsCurrent(current);
                    if (!allowed)
                    {
                        if (frame.Header.Kind == FrameKind.Request)
                        {
                            await QueueError(frame.Header, 2);
                        }

                        await TerminateAsync(5);
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

            if (Interlocked.Read(ref binding) == 0 && header.BodyLength > 2048)
            {
                throw new NetworkProtocolException("Unauthenticated body exceeds 2048 bytes.");
            }

            NetRoute route = GameNetProtocol.Catalog.Find(header.Opcode);
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
            bool loggedIn = Interlocked.Read(ref binding) != 0;
            if (ratePackets > (loggedIn ? settings.MaxAuthenticatedPacketsPerSecond : 5) ||
                rateBytes > (loggedIn ? settings.MaxAuthenticatedBytesPerSecond : 16 * 1024))
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

                    pendingNonce = 0;
                    pongDeadline = 0;
                }
            }
            else
            {
                ControlCodec.ReadClose(frame.Body);
                await CloseSocketAsync();
                return false;
            }

            return true;
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
            FrameHeader header = frame.Header;
            if (header.Opcode == GameNetRoutes.Login.Opcode || header.Opcode == GameNetRoutes.ResumeLogin.Opcode)
            {
                (LoginResponse Response, long Binding) result;
                if (header.Opcode == GameNetRoutes.Login.Opcode)
                {
                    LoginRequest request = GameNetRoutes.Login.RequestCodec.Decode(frame.Body);
                    try
                    {
                        result = accounts.Login(request, Stop);
                    }
                    finally
                    {
                        request.Password = string.Empty;
                    }
                }
                else
                {
                    ResumeLoginRequest request = GameNetRoutes.ResumeLogin.RequestCodec.Decode(frame.Body);
                    try
                    {
                        result = accounts.Resume(request, Stop);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(request.ResumeToken);
                        CryptographicOperations.ZeroMemory(request.AccountSessionId);
                    }
                }

                Interlocked.Exchange(ref binding, result.Binding);
                if (result.Binding != 0)
                {
                    Interlocked.Exchange(ref authenticatedOnce, 1);
                    authenticated();
                }

                try
                {
                    await RespondAsync(GameNetRoutes.Login, header.CorrelationId, result.Response, header.Opcode);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(result.Response.ResumeToken);
                    CryptographicOperations.ZeroMemory(result.Response.AccountSessionId);
                }

                if (result.Binding == 0)
                {
                    await TerminateAsync(5);
                }

                return;
            }

            if (!accounts.IsCurrent(Interlocked.Read(ref binding)))
            {
                await QueueError(header, 2);
                await TerminateAsync(5);
                return;
            }

            if (header.Opcode == GameNetRoutes.Logout.Opcode)
            {
                GameNetRoutes.Logout.RequestCodec.Decode(frame.Body);
                bool revoked = accounts.Logout(Interlocked.Read(ref binding));
                await RespondAsync(GameNetRoutes.Logout, header.CorrelationId,
                    new OperationResponse { ResultCode = revoked ? ResultCode.Success : ResultCode.SessionRevoked });
                await TerminateAsync(1);
            }
            else if (header.Opcode == GameNetRoutes.Echo.Opcode)
            {
                EchoRequest request = GameNetRoutes.Echo.RequestCodec.Decode(frame.Body);
                if (request.DelayMs > 5000)
                {
                    await QueueError(header, 2);
                    return;
                }

                await Task.Delay((int)request.DelayMs, lifetime.Token);
                if (!accounts.IsCurrent(Interlocked.Read(ref binding)))
                {
                    await QueueError(header, 2);
                    await TerminateAsync(5);
                    return;
                }

                await RespondAsync(GameNetRoutes.Echo, header.CorrelationId, new EchoResponse { Text = request.Text });
            }
            else if (header.Opcode == GameNetRoutes.RequestWorldGrant.Opcode)
            {
                GameNetRoutes.RequestWorldGrant.RequestCodec.Decode(frame.Body);
                await RespondAsync(GameNetRoutes.RequestWorldGrant, header.CorrelationId,
                    new WorldGrantResponse { ResultCode = ResultCode.WorldUnavailable });
            }
            else if (header.Kind == FrameKind.Request)
            {
                // No world owner is installed yet. Preserve the distinction from authentication failure.
                await QueueError(header, 2);
            }
            else
            {
                await TerminateAsync(5);
            }
        }

        private Task RespondAsync<TRequest, TResponse>(NetRpc<TRequest, TResponse> route, ulong correlation,
            TResponse response, uint? opcode = null) where TRequest : class where TResponse : class
        {
            OwnedFrame frame = sendMemory.Allocate(FrameCodec.HeaderBytes + route.ResponseMaxBodyBytes);
            try
            {
                var writer = new BoundedFrameWriter(frame);
                route.ResponseCodec.Encode(writer, response);
                writer.Complete(FrameKind.Response, opcode ?? route.Opcode, correlation);
            }
            catch
            {
                frame.Dispose();
                throw;
            }

            return QueueSend(frame, false);
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
                    if (item.Control && getMilliseconds() - item.Frame.AdmittedAt >= 15000)
                    {
                        throw new NetworkException("Control frame send deadline elapsed.");
                    }

                    if (item.CloseOutput)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await socket!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                    }
                    else
                    {
                        await WebSocketFrameTransport.SendAsync(socket!, item.Frame, lifetime.Token);
                    }

                    lock (queueGate)
                    {
                        if (item.Frame.Header.Kind == FrameKind.Control && item.Frame.Header.Opcode == (uint)ControlOpcode.Ping &&
                            pendingNonce != 0 && pendingNonce == ControlCodec.ReadNonce(item.Frame.Body))
                        {
                            pongDeadline = getMilliseconds() + 15000;
                        }
                    }

                    item.Completion?.TrySetResult();
                }
                catch (Exception error)
                {
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
            long current = Interlocked.Read(ref binding);
            if (current == 0 && now >= loginDeadline)
            {
                await TerminateAsync(6);
                return;
            }

            if (current != 0 && !accounts.IsCurrent(current))
            {
                await TerminateAsync(5);
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

                if (pendingNonce != 0 && pongDeadline != 0 && now >= pongDeadline)
                {
                    throw new NetworkException("Application heartbeat timed out.");
                }

                if (pendingNonce == 0 && now - lastPing >= 5000)
                {
                    sendNonce = ++nextNonce;
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

                lifetime.Cancel();
                socket?.Abort();
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
                lifetime.Dispose();
                sendSignal.Dispose();
                finished.TrySetResult();
            }
        }
    }
}
