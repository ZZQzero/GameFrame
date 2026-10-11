#nullable disable
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;

namespace GameFrame.Network.Connection
{
    internal sealed class ConnectionContext
    {
        internal sealed class Pending<T> : PendingRequest where T : class
        {
            private readonly NetMessageCodec<T> codec;
            private readonly int responseLimit;
            public readonly TaskCompletionSource<T> Source = new TaskCompletionSource<T>();
            public override int ResponseLimit => responseLimit;

            public Pending(NetMessageCodec<T> codec, int responseLimit)
            {
                this.codec = codec;
                this.responseLimit = responseLimit;
            }

            public override void Complete(OwnedFrame response)
            {
                if (Error is OperationCanceledException cancellation)
                {
                    Source.TrySetCanceled(cancellation.CancellationToken);
                }
                else if (Error != null)
                {
                    Source.TrySetException(Error);
                }
                else if (response.Header.StatusCode != 0)
                {
                    Source.TrySetException(new NetworkRemoteException(response.Header.StatusCode));
                }
                else
                {
                    try
                    {
                        Source.TrySetResult(codec.Decode(response.Body));
                    }
                    catch (Exception error)
                    {
                        Source.TrySetException(error);
                        throw;
                    }
                }
            }
        }

        private sealed class SendEntry
        {
            public OwnedFrame Frame;
            public PendingRequest Pending;
            public bool Control;
            public bool CloseOutput;
            public TaskCompletionSource<bool> Source;
        }

        private sealed class ReceiveEntry
        {
            public OwnedFrame Frame;
            public PendingRequest Pending;
            public bool End;
        }

        private const int PendingReservation = 512;
        private readonly object gate = new object();
        private readonly NetworkClientOptions options;
        private readonly NetProtocolCatalog catalog;
        private readonly NetworkConnectOptions connect;
        private readonly WebSocket socket;
        private readonly Func<Uri, CancellationToken, Task> connectSocket;
        private readonly Func<long> getMilliseconds;
        private readonly Func<int, CancellationToken, Task> delay;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly FrameMemoryBudget memory;
        private readonly FrameMemoryBudget sendMemory;
        private readonly FrameMemoryBudget controlMemory;
        private readonly FrameMemoryBudget receiveMemory;
        private readonly Queue<SendEntry> sends = new Queue<SendEntry>();
        private readonly Queue<SendEntry> controls = new Queue<SendEntry>();
        private readonly Queue<ReceiveEntry> receives = new Queue<ReceiveEntry>();
        private readonly Queue<PendingRequest> completions = new Queue<PendingRequest>();
        private readonly Dictionary<ulong, PendingRequest> pending;
        private readonly PendingDeadlineHeap deadlines;
        private readonly SemaphoreSlim sendSignal = new SemaphoreSlim(0);
        private SendEntry inFlightControl;
        private readonly TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>();
        private readonly TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>();
        private CancellationTokenRegistration connectRegistration;
        private int sendBytes;
        private int sendCount;
        private int controlBytes;
        private int controlCount;
        private int receiveBytes;
        private int receiveCount;
        private int metadataBytes;
        private ulong nextId;
        private bool stopped;
        private bool closing;
        private bool ready;
        private bool handshaking;
        private bool ioStopped;
        private bool terminalDelivered;
        private bool orderly;
        private Exception failure;
        private NetworkClientState publishedState = NetworkClientState.Connecting;
        private int reliableLimit;
        private long lastPing;
        private long pongDeadline;
        private ulong pendingNonce;
        private ulong nextNonce;
        private long sentFrames;
        private long receivedFrames;
        private long lateResponses;
        private long timeouts;
        private long cancellations;
        private long queueAgeWarnings;
        private bool disposed;
        private bool hostFailed;
        public readonly long Generation;
        public Task IoTask { get; private set; }
        public Task ConnectTask => connected.Task;
        public Task CloseTask => closed.Task;
        public NetworkClientState State
        {
            get
            {
                lock (gate)
                {
                    return closing ? NetworkClientState.Closing : failure != null ? NetworkClientState.Faulted : publishedState;
                }
            }
        }

        internal Action<NetworkClientState, Exception> StateChanged { get; set; }
        internal Action<uint, Exception> MessageFault { get; set; }
        internal Action<uint, OwnedFrame> Push { get; set; }

        public ConnectionContext(long generation, NetworkClientOptions options, NetProtocolCatalog catalog,
            NetworkConnectOptions connect, FrameMemoryBudget parent, CancellationToken cancellationToken,
            WebSocket testSocket = null, Func<long> testClock = null,
            Func<int, CancellationToken, Task> testDelay = null, Func<Uri, CancellationToken, Task> testConnect = null)
        {
            Generation = generation;
            this.options = options;
            this.catalog = catalog;
            this.connect = connect;
            socket = testSocket ?? new ClientWebSocket();
            if (socket is ClientWebSocket clientSocket)
            {
                clientSocket.Options.KeepAliveInterval = TimeSpan.Zero;
            }

            connectSocket = testConnect ?? ((ClientWebSocket)socket).ConnectAsync;
            getMilliseconds = testClock ?? NetworkClock.GetMilliseconds;
            delay = testDelay ?? Task.Delay;
            memory = new FrameMemoryBudget(options.MaxOwnedBytes, parent);
            sendMemory = new FrameMemoryBudget(options.ReliableSendBytes, memory);
            controlMemory = new FrameMemoryBudget(options.ControlQueueBytes, memory);
            receiveMemory = new FrameMemoryBudget(options.ReliableReceiveBytes, memory);
            pending = new Dictionary<ulong, PendingRequest>();
            deadlines = new PendingDeadlineHeap(Math.Min(options.PendingCount, options.PendingMetadataBytes / PendingReservation));
            reliableLimit = connect.ReliableLimit;
            // Register outside the state gate: already-canceled tokens invoke synchronously.
            connectRegistration = cancellationToken.Register(() => Fail(new OperationCanceledException(cancellationToken)));
        }

        public void Start()
        {
            IoTask = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            var tasks = new List<Task>(3);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await connectSocket(connect.Endpoint, timeout.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        handshaking = true;
                    }

                    using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using OwnedFrame hello = memory.Allocate(80);
                    ControlCodec.WriteClientHello(hello.Bytes.AsSpan(32), catalog.Hash, connect.ReliableLimit,
                        0, 0, ReadOnlySpan<byte>.Empty, NetworkTransportKind.WebSocket);
                    hello.Header = new FrameHeader(FrameKind.Control, 1, 0, 48);
                    hello.Length = 80;
                    FrameCodec.WriteHeader(hello.Bytes, hello.Header);
                    await WebSocketFrameTransport.SendAsync(socket, hello, helloTimeout.Token).ConfigureAwait(false);
                    using OwnedFrame reply = await WebSocketFrameTransport.ReceiveAsync(socket, receiveMemory, reliableLimit,
                        FrameDirection.ServerToClient, true, helloTimeout.Token, header => ValidateControlHeader(header, false)).ConfigureAwait(false);
                    if (reply == null)
                    {
                        throw new NetworkException("Transport closed during Hello.");
                    }

                    ValidateHello(reply);
                    lock (gate)
                    {
                        if (stopped)
                        {
                            lifetime.Token.ThrowIfCancellationRequested();
                        }

                        ready = true;
                        lastPing = getMilliseconds();
                    }
                }

                tasks.Add(SendLoopAsync());
                tasks.Add(ReceiveLoopAsync());
                tasks.Add(ClockLoopAsync());
                await await Task.WhenAny(tasks).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Fail(error);
            }
            finally
            {
                StopIo();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Fail(error);
                }

                lock (gate)
                {
                    DrainSends(sends);
                    DrainSends(controls);
                    socket.Dispose();
                    ioStopped = true;
                }
            }
        }

        private void ValidateHello(OwnedFrame reply)
        {
            if (reply.Header.Opcode == (uint)ControlOpcode.Close)
            {
                ushort reason = ControlCodec.ReadClose(reply.Body);
                throw new NetworkException($"Server closed during Hello ({reason}).");
            }

            if (reply.Header.Opcode == (uint)ControlOpcode.Reject)
            {
                ushort reason = ControlCodec.ReadReject(reply.Body, out ReadOnlySpan<byte> ignored);
                throw new NetworkProtocolException($"Server rejected Hello ({reason}).");
            }

            if (reply.Header.Opcode != (uint)ControlOpcode.ServerHello)
            {
                throw new NetworkProtocolException("Expected ServerHello.");
            }

            ServerHelloBody accepted = ControlCodec.ReadServerHello(reply.Body, NetworkTransportKind.WebSocket);
            var proposed = new ClientHelloBody(catalog.Hash, connect.ReliableLimit, 0, 0, ReadOnlySpan<byte>.Empty);
            ControlCodec.ValidateNegotiation(accepted, proposed, catalog.MaxReliableBodyBytes, catalog.MaxUnreliableBodyBytes);
            reliableLimit = accepted.ReliableLimit;
        }

        public Task<TResponse> Request<TRequest, TResponse>(NetRpc<TRequest, TResponse> route, TRequest request,
            TimeSpan timeout, CancellationToken cancellationToken) where TRequest : class where TResponse : class
        {
            catalog.RequireRegistered(route);
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > long.MaxValue - getMilliseconds())
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            cancellationToken.ThrowIfCancellationRequested();
            OwnedFrame frame = Encode(route.RequestCodec, route.MaxBodyBytes, request);
            var item = new Pending<TResponse>(route.ResponseCodec, route.ResponseMaxBodyBytes)
            {
                Opcode = route.Opcode,
                CancellationToken = cancellationToken
            };
            try
            {
                lock (gate)
                {
                    RequireConnected();
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckSendCapacity(frame, false);
                    if (pending.Count >= options.PendingCount || PendingReservation > options.PendingMetadataBytes - metadataBytes)
                    {
                        throw new NetworkBackpressureException("Pending capacity exceeded.");
                    }

                    if (nextId == ulong.MaxValue)
                    {
                        throw new NetworkException("Correlation IDs exhausted.");
                    }

                    memory.Reserve(PendingReservation);
                    metadataBytes += PendingReservation;
                    item.Id = ++nextId;
                    item.Deadline = checked(getMilliseconds() + (long)Math.Ceiling(timeout.TotalMilliseconds));
                    frame.Header = new FrameHeader(FrameKind.Request, route.Opcode, item.Id, frame.Length - 32);
                    FrameCodec.WriteHeader(frame.Bytes, frame.Header);
                    pending.Add(item.Id, item);
                    deadlines.Add(item);
                    EnqueueSend(frame, item, false);
                }
            }
            catch
            {
                frame.Dispose();
                throw;
            }

            item.Registration = cancellationToken.Register(() => Cancel(item, cancellationToken));
            sendSignal.Release();
            return item.Source.Task;
        }

        public void Send<T>(NetSend<T> route, T message, CancellationToken cancellationToken) where T : class
        {
            catalog.RequireRegistered(route);
            cancellationToken.ThrowIfCancellationRequested();
            OwnedFrame frame = Encode(route.Codec, route.MaxBodyBytes, message);
            try
            {
                lock (gate)
                {
                    RequireConnected();
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckSendCapacity(frame, false);
                    frame.Header = new FrameHeader(FrameKind.Notify, route.Opcode, 0, frame.Length - 32);
                    FrameCodec.WriteHeader(frame.Bytes, frame.Header);
                    EnqueueSend(frame, null, false);
                }
            }
            catch
            {
                frame.Dispose();
                throw;
            }

            sendSignal.Release();
        }

        private OwnedFrame Encode<T>(NetMessageCodec<T> codec, int maxBody, T message) where T : class
        {
            lock (gate)
            {
                RequireConnected();
            }

            OwnedFrame frame = sendMemory.Allocate(32 + maxBody);
            try
            {
                var writer = new BoundedFrameWriter(frame);
                codec.Encode(writer, message);
                frame.Length = 32 + writer.BodyLength;
                return frame;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        private void RequireConnected()
        {
            if (stopped || closing || failure != null || publishedState != NetworkClientState.Connected)
            {
                throw new InvalidOperationException("Network client is not Connected.");
            }
        }

        private void CheckSendCapacity(OwnedFrame frame, bool control)
        {
            if (control ? controlCount >= options.ControlQueueCount || frame.Capacity > options.ControlQueueBytes - controlBytes :
                sendCount >= options.ReliableSendCount || frame.Capacity > options.ReliableSendBytes - sendBytes)
            {
                throw new NetworkBackpressureException("Reliable send capacity exceeded.");
            }
        }

        private void EnqueueSend(OwnedFrame frame, PendingRequest request, bool control)
        {
            frame.AdmittedAt = getMilliseconds();
            (control ? controls : sends).Enqueue(new SendEntry { Frame = frame, Pending = request, Control = control });
            if (control)
            {
                controlCount++;
                controlBytes += frame.Capacity;
            }
            else
            {
                sendCount++;
                sendBytes += frame.Capacity;
            }
        }

        private async Task SendLoopAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await sendSignal.WaitAsync(lifetime.Token).ConfigureAwait(false);
                SendEntry item;
                bool discard;
                Exception sendFailure = null;
                lock (gate)
                {
                    Queue<SendEntry> queue = controls.Count != 0 ? controls : sends;
                    if (queue.Count == 0)
                    {
                        continue;
                    }

                    item = queue.Dequeue();
                    if (item.Control)
                    {
                        inFlightControl = item;
                    }
                    discard = item.Pending != null && item.Pending.State != 0;
                    long now = getMilliseconds();
                    if (!discard && item.Pending != null)
                    {
                        if (item.Pending.CancellationToken.IsCancellationRequested)
                        {
                            SetTerminal(item.Pending, new OperationCanceledException(item.Pending.CancellationToken));
                            cancellations++;
                            discard = true;
                        }
                        else if (now >= item.Pending.Deadline)
                        {
                            SetTerminal(item.Pending, new NetworkRequestTimeoutException("RPC deadline elapsed."));
                            timeouts++;
                            discard = true;
                        }
                    }

                    if (item.Control && now - item.Frame.AdmittedAt >= 15000)
                    {
                        sendFailure = new NetworkException("Control frame send deadline elapsed.");
                    }

                    if (now - item.Frame.AdmittedAt > options.MaxSendQueueAge.TotalMilliseconds)
                    {
                        queueAgeWarnings++;
                        if (!item.Control && !discard)
                        {
                            var error = new NetworkBackpressureException("Reliable send queue age exceeded.");
                            if (item.Pending != null)
                            {
                                SetTerminal(item.Pending, error);
                                discard = true;
                            }
                            else
                            {
                                sendFailure = error;
                            }
                        }
                    }
                }

                try
                {
                    if (sendFailure != null)
                    {
                        throw sendFailure;
                    }

                    if (!discard)
                    {
                        if (item.CloseOutput)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, lifetime.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await WebSocketFrameTransport.SendAsync(socket, item.Frame, lifetime.Token).ConfigureAwait(false);
                        }
                        lock (gate)
                        {
                            sentFrames++;
                            if (item.Frame.Header.Opcode == (uint)ControlOpcode.Ping && pendingNonce != 0 &&
                                pendingNonce == ControlCodec.ReadNonce(item.Frame.Body))
                            {
                                pongDeadline = getMilliseconds() + 15000;
                            }
                        }
                    }

                    item.Source?.TrySetResult(true);
                }
                catch
                {
                    item.Source?.TrySetCanceled();
                    throw;
                }
                finally
                {
                    lock (gate)
                    {
                        ReleaseSend(item);
                        if (item.Control)
                        {
                            inFlightControl = null;
                        }
                    }
                }
            }
        }

        private void ReleaseSend(SendEntry item)
        {
            if (item.Control)
            {
                controlCount--;
                controlBytes -= item.Frame.Capacity;
            }
            else
            {
                sendCount--;
                sendBytes -= item.Frame.Capacity;
            }

            item.Frame.Dispose();
        }

        private void DrainSends(Queue<SendEntry> queue)
        {
            while (queue.Count != 0)
            {
                SendEntry entry = queue.Dequeue();
                entry.Source?.TrySetCanceled();
                ReleaseSend(entry);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                OwnedFrame frame = await WebSocketFrameTransport.ReceiveAsync(socket, receiveMemory, reliableLimit,
                    FrameDirection.ServerToClient, false, lifetime.Token, ValidateHeader).ConfigureAwait(false);
                if (frame == null)
                {
                    lock (gate)
                    {
                        orderly = true;
                        stopped = true;
                        receives.Enqueue(new ReceiveEntry { End = true });
                    }

                    return;
                }

                bool transferred = false;
                try
                {
                    if (frame.Header.Kind == FrameKind.Control)
                    {
                        HandleControl(frame);
                        continue;
                    }

                    lock (gate)
                    {
                        receivedFrames++;
                        PendingRequest request = null;
                        if (frame.Header.Kind == FrameKind.Response)
                        {
                            if (!pending.TryGetValue(frame.Header.CorrelationId, out request) || request.State != 0)
                            {
                                lateResponses++;
                                continue;
                            }

                            if (request.Opcode != frame.Header.Opcode)
                            {
                                throw new NetworkProtocolException("Response opcode mismatches the pending request.");
                            }

                            if (getMilliseconds() >= request.Deadline)
                            {
                                SetTerminal(request, new NetworkRequestTimeoutException("RPC deadline elapsed."));
                                timeouts++;
                                lateResponses++;
                                continue;
                            }

                            request.State = 1;
                            deadlines.Remove(request);
                        }

                        if (receiveCount >= options.ReliableReceiveCount || frame.Capacity > options.ReliableReceiveBytes - receiveBytes)
                        {
                            throw new NetworkBackpressureException("Reliable receive capacity exceeded.");
                        }

                        receives.Enqueue(new ReceiveEntry { Frame = frame, Pending = request });
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

        private void ValidateHeader(FrameHeader header)
        {
            if (header.Kind == FrameKind.Control)
            {
                ValidateControlHeader(header, true);
                return;
            }

            lock (gate)
            {
                if (header.Kind == FrameKind.Response)
                {
                    // Unknown IDs are bounded by the fixed header and then discarded without deserialization.
                    if (pending.TryGetValue(header.CorrelationId, out PendingRequest request) && request.State == 0 &&
                        (request.Opcode != header.Opcode || header.BodyLength > request.ResponseLimit))
                    {
                        throw new NetworkProtocolException("Response opcode/body mismatches the pending request.");
                    }
                }
                else
                {
                    NetRoute route = catalog.Find(header.Opcode);
                    if (route.Kind != NetRouteKind.Push || route.Delivery != NetDelivery.Reliable || header.BodyLength > route.MaxBodyBytes)
                    {
                        throw new NetworkProtocolException("Push route/body/channel mismatch.");
                    }
                }
            }
        }

        private static void ValidateControlHeader(FrameHeader header, bool helloComplete)
        {
            ControlCodec.ValidatePhase((ControlOpcode)header.Opcode, helloComplete, FrameDirection.ServerToClient);
            ControlCodec.ValidateBodyLength((ControlOpcode)header.Opcode, header.BodyLength, NetworkTransportKind.WebSocket);
        }

        private void HandleControl(OwnedFrame frame)
        {
            ControlOpcode opcode = (ControlOpcode)frame.Header.Opcode;
            ControlCodec.ValidatePhase(opcode, true, FrameDirection.ServerToClient);
            if (opcode == ControlOpcode.Ping)
            {
                QueueControl(ControlOpcode.Pong, ControlCodec.ReadNonce(frame.Body));
            }
            else if (opcode == ControlOpcode.Pong)
            {
                ulong nonce = ControlCodec.ReadNonce(frame.Body);
                lock (gate)
                {
                    if (pendingNonce == 0 || pendingNonce != nonce)
                    {
                        throw new NetworkProtocolException("Unmatched Pong.");
                    }

                    pendingNonce = 0;
                    pongDeadline = 0;
                }
            }
            else
            {
                ushort reason = ControlCodec.ReadClose(frame.Body);
                lock (gate)
                {
                    if (reason != 0)
                    {
                        MessageFaultMarker = new NetworkException($"Server closed the connection ({reason}).");
                    }

                    orderly = true;
                    stopped = true;
                    receives.Enqueue(new ReceiveEntry { End = true });
                }

                // Reliable packets validated before Close are delivered before the end marker.
                StopIo();
            }
        }

        private Exception MessageFaultMarker;

        private void QueueControl(ControlOpcode opcode, ulong nonce)
        {
            OwnedFrame frame = controlMemory.Allocate(40);
            try
            {
                ControlCodec.WriteNonce(frame.Bytes.AsSpan(32), nonce);
                frame.Header = new FrameHeader(FrameKind.Control, (uint)opcode, 0, 8);
                frame.Length = 40;
                FrameCodec.WriteHeader(frame.Bytes, frame.Header);
                lock (gate)
                {
                    if (stopped)
                    {
                        frame.Dispose();
                        return;
                    }

                    CheckSendCapacity(frame, true);
                    EnqueueSend(frame, null, true);
                }

                sendSignal.Release();
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        private async Task ClockLoopAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await delay(20, lifetime.Token).ConfigureAwait(false);
                CheckClock();
            }
        }

        internal void CheckClock()
        {
            long now = getMilliseconds();
            ulong nonce = 0;
            lock (gate)
            {
                while (deadlines.First != null && now >= deadlines.First.Deadline)
                {
                    SetTerminal(deadlines.First, new NetworkRequestTimeoutException("RPC deadline elapsed."));
                    timeouts++;
                }

                SendEntry oldestControl = inFlightControl ?? (controls.Count == 0 ? null : controls.Peek());
                if (oldestControl != null && now - oldestControl.Frame.AdmittedAt >= 15000)
                {
                    throw new NetworkException("Control frame send deadline elapsed.");
                }

                if (pendingNonce != 0 && pongDeadline != 0 && now >= pongDeadline)
                {
                    throw new NetworkException("Application heartbeat timed out.");
                }

                if (!stopped && pendingNonce == 0 && now - lastPing >= 5000)
                {
                    pendingNonce = nonce = ++nextNonce;
                    lastPing = now;
                }
            }

            if (nonce != 0)
            {
                QueueControl(ControlOpcode.Ping, nonce);
            }
        }

        private void Cancel(PendingRequest request, CancellationToken token)
        {
            lock (gate)
            {
                if (request.State < 2)
                {
                    SetTerminal(request, new OperationCanceledException(token));
                    cancellations++;
                }
            }
        }

        private void SetTerminal(PendingRequest request, Exception error)
        {
            if (request.State >= 2)
            {
                return;
            }

            request.State = 2;
            request.Error = error;
            deadlines.Remove(request);
            completions.Enqueue(request);
        }

        public void Fail(Exception error)
        {
            lock (gate)
            {
                if (!closing && !orderly && failure == null)
                {
                    failure = error;
                    foreach (PendingRequest request in pending.Values)
                    {
                        SetTerminal(request, error);
                    }
                }

                stopped = true;
            }

            StopIo();
        }

        // Host failure supersedes an orderly end marker: reliable dispatch is no longer safe.
        internal void FailHost(Exception error)
        {
            lock (gate)
            {
                if (disposed || hostFailed)
                {
                    return;
                }

                hostFailed = true;
                failure = failure ?? error;
                orderly = false;
                stopped = true;
                deadlines.Clear();
                completions.Clear();
            }

            StopIo();
        }

        internal bool HostCleanupComplete
        {
            get
            {
                lock (gate)
                {
                    return ioStopped && terminalDelivered && connected.Task.IsCompleted && pending.Count == 0 &&
                        receives.Count == 0 && completions.Count == 0 && (!closing || closed.Task.IsCompleted);
                }
            }
        }

        private void StopIo()
        {
            lock (gate)
            {
                if (disposed || ioStopped)
                {
                    return;
                }

                lifetime.Cancel();
                socket.Abort();
            }
        }

        public void BeginClose()
        {
            bool notifyFailure = false;
            lock (gate)
            {
                if (disposed || closing)
                {
                    return;
                }

                if (failure != null && !terminalDelivered)
                {
                    terminalDelivered = true;
                    publishedState = NetworkClientState.Faulted;
                    notifyFailure = true;
                }

                closing = true;
                stopped = true;
                foreach (PendingRequest request in pending.Values)
                {
                    SetTerminal(request, failure ?? new NetworkException("Connection explicitly closed."));
                }
            }

            if (notifyFailure)
            {
                StateChanged?.Invoke(NetworkClientState.Faulted, failure);
            }

            StateChanged?.Invoke(NetworkClientState.Closing, failure);
            CloseIoTask = Task.Run(CloseIoAsync);
        }

        internal Task CloseIoTask { get; private set; }

        private async Task CloseIoAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                Task send = QueueClose(false);
                Task completed = await Task.WhenAny(send, Task.Delay(2000, timeout.Token)).ConfigureAwait(false);
                if (ReferenceEquals(completed, send))
                {
                    await send.ConfigureAwait(false);
                    Task output = QueueClose(true);
                    await Task.WhenAny(output, Task.Delay(2000, timeout.Token)).ConfigureAwait(false);
                    if (output.IsCompleted)
                    {
                        await output.ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException || error is WebSocketException ||
                error is NetworkException || error is ObjectDisposedException)
            {
                // Graceful close is bounded best effort. Abort below still waits for the real IO owners.
            }
            finally
            {
                StopIo();
            }
        }

        private Task QueueClose(bool output)
        {
            lock (gate)
            {
                if (ioStopped || lifetime.IsCancellationRequested)
                {
                    return Task.CompletedTask;
                }

                OwnedFrame frame = controlMemory.Allocate(output ? 32 : 36);
                try
                {
                    if (!output)
                    {
                        ControlCodec.WriteClose(frame.Bytes.AsSpan(32), 1);
                        frame.Header = new FrameHeader(FrameKind.Control, 6, 0, 4);
                        frame.Length = 36;
                        FrameCodec.WriteHeader(frame.Bytes, frame.Header);
                    }

                    CheckSendCapacity(frame, true);
                    var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    frame.AdmittedAt = getMilliseconds();
                    controls.Enqueue(new SendEntry { Frame = frame, Control = true, CloseOutput = output, Source = source });
                    controlCount++;
                    controlBytes += frame.Capacity;
                    sendSignal.Release();
                    return source.Task;
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }
        }

        // Called only by the owning main-thread pump. Each completion/dispatch/cleanup consumes one unit.
        public bool PumpOne()
        {
            if (hostFailed)
            {
                return PumpHostFailureOne();
            }

            PendingRequest completion = null;
            ReceiveEntry receive = null;
            NetworkClientState? transition = null;
            Exception stateError = null;
            bool completeConnect = false;
            bool completeClose = false;
            lock (gate)
            {
                if (handshaking && publishedState == NetworkClientState.Connecting && !terminalDelivered)
                {
                    publishedState = NetworkClientState.Handshaking;
                    transition = publishedState;
                }
                else if (ready && !stopped && publishedState == NetworkClientState.Handshaking)
                {
                    publishedState = NetworkClientState.Connected;
                    transition = publishedState;
                    completeConnect = true;
                }
                else if (failure != null && !terminalDelivered)
                {
                    terminalDelivered = true;
                    publishedState = NetworkClientState.Faulted;
                    transition = publishedState;
                    stateError = failure;
                }
                else if (completions.Count != 0)
                {
                    completion = completions.Dequeue();
                    RemovePending(completion);
                }
                else if (receives.Count != 0)
                {
                    receive = receives.Dequeue();
                    if (!receive.End)
                    {
                        receiveCount--;
                        receiveBytes -= receive.Frame.Capacity;
                        if (receive.Pending != null && receive.Pending.State == 1 && !closing && failure == null)
                        {
                            completion = receive.Pending;
                            RemovePending(completion);
                        }
                    }
                }
                else if (ioStopped && (CloseIoTask == null || CloseIoTask.IsCompleted) && (closing || failure != null || orderly))
                {
                    if (!connected.Task.IsCompleted)
                    {
                        completeConnect = true;
                        stateError = failure ?? new NetworkException("Connection closed before completing Hello.");
                    }

                    completeClose = closing && pending.Count == 0 && !closed.Task.IsCompleted;
                    if (!completeConnect && !completeClose)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            if (completeConnect)
            {
                connectRegistration.Dispose();
                lock (gate)
                {
                    stateError = stateError ?? failure;
                    if (stateError != null && publishedState == NetworkClientState.Connected)
                    {
                        terminalDelivered = true;
                        publishedState = NetworkClientState.Faulted;
                        transition = publishedState;
                    }
                }
            }

            if (transition.HasValue)
            {
                StateChanged?.Invoke(transition.Value, stateError);
            }

            if (completeConnect)
            {
                lock (gate)
                {
                    // State observers may explicitly close the connection before Connect commits.
                    stateError = stateError ?? failure ?? (closing ? new NetworkException("Connection closed before Connect completed.") : null);
                }

                if (stateError is OperationCanceledException canceled)
                {
                    connected.TrySetCanceled(canceled.CancellationToken);
                }
                else if (stateError != null)
                {
                    connected.TrySetException(stateError);
                }
                else
                {
                    connected.TrySetResult(true);
                }
            }

            try
            {
                if (completion != null)
                {
                    completion.Registration.Dispose();
                    completion.Complete(receive?.Frame);
                }
                else if (receive != null && !receive.End && receive.Frame.Header.Kind == FrameKind.Push && !closing && failure == null)
                {
                    Push?.Invoke(receive.Frame.Header.Opcode, receive.Frame);
                }
                else if (receive != null && receive.End)
                {
                    FailOrderly();
                }
            }
            catch (Exception error)
            {
                Fail(error);
                MessageFault?.Invoke(receive?.Frame?.Header.Opcode ?? completion?.Opcode ?? 0, error);
            }
            finally
            {
                receive?.Frame?.Dispose();
            }

            if (completeClose)
            {
                CompleteClose();
            }

            return true;
        }

        // Bypasses DTO decoding, Push dispatch, deadline ordering and reliable end markers.
        // Only the main-thread cleanup owner uses this path after the host stops normal driving.
        private bool PumpHostFailureOne()
        {
            PendingRequest completion = null;
            OwnedFrame frame = null;
            bool notifyFailure = false;
            bool completeConnect = false;
            bool completeClose = false;
            lock (gate)
            {
                if (!terminalDelivered)
                {
                    terminalDelivered = true;
                    publishedState = NetworkClientState.Faulted;
                    notifyFailure = true;
                }
                else if (!connected.Task.IsCompleted)
                {
                    completeConnect = true;
                }
                else if (pending.Count != 0)
                {
                    using (Dictionary<ulong, PendingRequest>.ValueCollection.Enumerator iterator = pending.Values.GetEnumerator())
                    {
                        iterator.MoveNext();
                        completion = iterator.Current;
                    }

                    if (completion.State < 2)
                    {
                        completion.Error = failure;
                    }

                    RemovePending(completion);
                }
                else if (receives.Count != 0)
                {
                    ReceiveEntry receive = receives.Dequeue();
                    frame = receive.Frame;
                    if (frame != null)
                    {
                        receiveCount--;
                        receiveBytes -= frame.Capacity;
                    }
                }
                else if (ioStopped && (CloseIoTask == null || CloseIoTask.IsCompleted))
                {
                    completions.Clear();
                    completeClose = closing && !closed.Task.IsCompleted;
                    if (!completeClose)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            if (notifyFailure)
            {
                StateChanged?.Invoke(NetworkClientState.Faulted, failure);
            }

            if (completeConnect)
            {
                connectRegistration.Dispose();
                if (failure is OperationCanceledException canceled)
                {
                    connected.TrySetCanceled(canceled.CancellationToken);
                }
                else
                {
                    connected.TrySetException(failure);
                }
            }

            if (completion != null)
            {
                completion.Registration.Dispose();
                completion.Complete(null);
            }

            frame?.Dispose();
            if (completeClose)
            {
                CompleteClose();
            }

            return true;
        }

        private void CompleteClose()
        {
            lock (gate)
            {
                disposed = true;
                socket.Dispose();
                lifetime.Dispose();
                sendSignal.Dispose();
                publishedState = NetworkClientState.Idle;
            }

            StateChanged?.Invoke(NetworkClientState.Idle, null);
            closed.TrySetResult(true);
        }

        private void RemovePending(PendingRequest request)
        {
            request.State = 3;
            pending.Remove(request.Id);
            deadlines.Remove(request);
            metadataBytes -= PendingReservation;
            memory.Release(PendingReservation);
        }

        private void FailOrderly()
        {
            lock (gate)
            {
                if (closing)
                {
                    return;
                }

                orderly = false;
                failure = MessageFaultMarker ?? new NetworkException("Remote connection closed.");
                foreach (PendingRequest request in pending.Values)
                {
                    SetTerminal(request, failure);
                }
            }
        }

        public NetworkMetricsSnapshot Metrics()
        {
            lock (gate)
            {
                return new NetworkMetricsSnapshot(Generation, memory.OwnedBytes, sendCount, receiveCount, pending.Count,
                    deadlines.Count, sentFrames, receivedFrames, lateResponses, timeouts, cancellations, queueAgeWarnings);
            }
        }
    }
}
