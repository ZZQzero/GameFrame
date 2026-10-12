#nullable disable
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Network.Transport.Kcp;

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
        private readonly IFrameTransport transport;
        private readonly Func<long> getMilliseconds;
        private readonly Func<int, CancellationToken, Task> delay;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly FrameMemoryBudget memory;
        private readonly FrameMemoryBudget sendMemory;
        private readonly FrameMemoryBudget controlMemory;
        private readonly FrameMemoryBudget receiveMemory;
        private readonly FrameMemoryBudget unreliableMemory;
        private readonly FrameMemoryBudget unreliableReceiveMemory;
        private readonly Queue<ReceiveEntry> unreliableReceives = new Queue<ReceiveEntry>();
        private int unreliableSendCount, unreliableSendBytes, unreliableReceiveBytes;
        private int unreliableLimit;
        private double unreliableTokens;
        private long unreliableTokenTime;
        private int reliablePumpCount;
        private long unreliableDropped;
        private readonly Queue<SendEntry> sends = new Queue<SendEntry>();
        private readonly Queue<SendEntry> unreliableSends = new Queue<SendEntry>();
        private readonly Queue<SendEntry> controls = new Queue<SendEntry>();
        private readonly Queue<ReceiveEntry> receives = new Queue<ReceiveEntry>();
        private readonly Queue<PendingRequest> completions = new Queue<PendingRequest>();
        private readonly Dictionary<ulong, PendingRequest> pending;
        private readonly PendingDeadlineHeap deadlines;
        private readonly SemaphoreSlim sendSignal = new SemaphoreSlim(0);
        private SendEntry inFlightControl;
        private readonly TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>();
        private readonly TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>();
        private readonly TaskCompletionSource<bool> peerClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task outputCloseTask;
        private bool remoteCloseReceived;
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
        private Exception cleanupFailure;
        private NetworkClientState publishedState = NetworkClientState.Connecting;
        private int reliableLimit;
        private long lastPing;
        private long pongDeadline;
        private ulong pendingNonce;
        private ulong nextNonce;
        private long sentFrames;
        private long receivedFrames;
        private long sentApplicationBytes, receivedApplicationBytes, sentBusinessBodyBytes, receivedBusinessBodyBytes, pingRoundTripMilliseconds;
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
            Func<int, CancellationToken, Task> testDelay = null, Func<Uri, CancellationToken, Task> testConnect = null, KcpWorker kcpWorker = null)
        {
            Generation = generation;
            this.options = options;
            this.catalog = catalog;
            this.connect = connect;
            getMilliseconds = testClock ?? NetworkClock.GetMilliseconds;
            delay = testDelay ?? Task.Delay;
            memory = new FrameMemoryBudget(options.MaxOwnedBytes, parent);
            sendMemory = new FrameMemoryBudget(options.ReliableSendBytes, memory);
            controlMemory = new FrameMemoryBudget(options.ControlQueueBytes, memory);
            receiveMemory = new FrameMemoryBudget(options.ReliableReceiveBytes, memory);
            unreliableMemory = new FrameMemoryBudget(options.UnreliableQueueBytes, memory);
            unreliableReceiveMemory = new FrameMemoryBudget(options.UnreliableQueueBytes, memory);
            transport = connect.Transport == NetworkTransportKind.Kcp
                ? (IFrameTransport)new KcpTransport(kcpWorker ?? throw new ArgumentNullException(nameof(kcpWorker)),
                    connect, options, memory, CheckTransportClock)
                : new WebSocketTransport(testSocket, testConnect);
            pending = new Dictionary<ulong, PendingRequest>();
            deadlines = new PendingDeadlineHeap(Math.Min(options.PendingCount, options.PendingMetadataBytes / PendingReservation));
            reliableLimit = connect.ReliableLimit;
            unreliableLimit = connect.UnreliableLimit;
            unreliableTokens = options.UnreliableBurstBytes;
            unreliableTokenTime = getMilliseconds();
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
                    await transport.ConnectAsync(connect.Endpoint, timeout.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        handshaking = true;
                    }

                    using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using OwnedFrame hello = memory.Allocate(80 + connect.Ticket.Length);
                    ControlCodec.WriteClientHello(hello.Bytes.AsSpan(32), catalog.Hash, connect.ReliableLimit,
                        connect.UnreliableLimit, connect.UnreliableLimit == 0 ? 0 : ControlCodec.UnreliableFeature, connect.Ticket, connect.Transport);
                    hello.Header = new FrameHeader(FrameKind.Control, 1, 0, 48 + connect.Ticket.Length);
                    hello.Length = 80 + connect.Ticket.Length;
                    FrameCodec.WriteHeader(hello.Bytes, hello.Header);
                    await transport.SendAsync(hello, helloTimeout.Token).ConfigureAwait(false);
                    using OwnedFrame reply = await transport.ReceiveAsync(receiveMemory, reliableLimit, unreliableLimit,
                        FrameDirection.ServerToClient, true, helloTimeout.Token, (header, delivery) => ValidateControlHeader(header, false)).ConfigureAwait(false);
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
                if (!transport.OwnsClock)
                {
                    tasks.Add(ClockLoopAsync());
                }
                await await Task.WhenAny(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // Stopping sibling IO is cleanup; dispatch faults still supersede an orderly end.
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
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    Fail(error);
                }

                try
                {
                    transport.Dispose();
                }
                catch (Exception error)
                {
                    RecordCleanupFailure(error);
                }

                try
                {
                    await transport.StoppedTask.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    RecordCleanupFailure(error);
                }

                lock (gate)
                {
                    DrainSends(sends);
                    DrainSends(unreliableSends);
                    DrainSends(controls);
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

            ServerHelloBody accepted = ControlCodec.ReadServerHello(reply.Body, connect.Transport);
            var proposed = new ClientHelloBody(catalog.Hash, connect.ReliableLimit, connect.UnreliableLimit,
                connect.UnreliableLimit == 0 ? 0 : ControlCodec.UnreliableFeature, connect.Ticket);
            ControlCodec.ValidateNegotiation(accepted, proposed, catalog.MaxReliableBodyBytes, catalog.MaxUnreliableBodyBytes);
            reliableLimit = accepted.ReliableLimit;
            unreliableLimit = accepted.UnreliableLimit;
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
                    CheckKcpAdmission(frame, timeout.TotalMilliseconds);
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
                    CheckKcpAdmission(frame, double.PositiveInfinity);
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

        public bool TrySendUnreliable<T>(NetUnreliable<T> route, T message) where T : class
        {
            catalog.RequireRegistered(route);
            lock (gate)
            {
                RequireConnected();
                if (connect.Transport != NetworkTransportKind.Kcp || unreliableLimit == 0)
                {
                    throw new PlatformNotSupportedException("This connection has no unreliable channel.");
                }

                if (unreliableSendCount >= options.UnreliableQueueCount ||
                    32 + route.MaxBodyBytes > options.UnreliableQueueBytes - unreliableSendBytes)
                {
                    return false;
                }
            }

            OwnedFrame frame;
            try
            {
                frame = unreliableMemory.Allocate(32 + route.MaxBodyBytes);
            }
            catch (NetworkBackpressureException)
            {
                return false;
            }

            bool transferred = false;
            try
            {
                var writer = new BoundedFrameWriter(frame);
                route.Codec.Encode(writer, message);
                writer.Complete(FrameKind.Notify, route.Opcode, 0);
                frame.Delivery = NetDelivery.Unreliable;
                lock (gate)
                {
                    RequireConnected();
                    long now = getMilliseconds();
                    unreliableTokens = Math.Min(options.UnreliableBurstBytes, unreliableTokens +
                        Math.Max(0, now - unreliableTokenTime) * (double)options.UnreliableBytesPerSecond / 1000);
                    unreliableTokenTime = now;
                    if (unreliableTokens < frame.Length || unreliableSendCount >= options.UnreliableQueueCount ||
                        frame.Capacity > options.UnreliableQueueBytes - unreliableSendBytes)
                    {
                        return false;
                    }

                    unreliableTokens -= frame.Length;
                    EnqueueSend(frame, null, false);
                    transferred = true;
                }

                sendSignal.Release();
                return true;
            }
            finally
            {
                if (!transferred)
                {
                    frame.Dispose();
                }
            }
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

        private void CheckKcpAdmission(OwnedFrame frame, double remainingMilliseconds)
        {
            if (transport is KcpTransport kcp)
            {
                long queuedBytes = 0;
                foreach (SendEntry entry in sends)
                {
                    if (!entry.Frame.AcceptedByTransport && (entry.Pending == null || entry.Pending.State == 0))
                    {
                        queuedBytes += entry.Frame.Length;
                    }
                }
                double wait = kcp.EstimateReliableWaitMilliseconds(queuedBytes, frame.Length, getMilliseconds());
                if (wait >= Math.Min(options.MaxSendQueueAge.TotalMilliseconds, remainingMilliseconds))
                {
                    throw new NetworkBackpressureException("Estimated KCP send wait exceeds the admission deadline.");
                }
            }
        }

        private void EnqueueSend(OwnedFrame frame, PendingRequest request, bool control)
        {
            frame.AdmittedAt = getMilliseconds();
            (control ? controls : frame.Delivery == NetDelivery.Unreliable ? unreliableSends : sends)
                .Enqueue(new SendEntry { Frame = frame, Pending = request, Control = control });
            if (control)
            {
                controlCount++;
                controlBytes += frame.Capacity;
            }
            else if (frame.Delivery == NetDelivery.Unreliable)
            {
                unreliableSendCount++;
                unreliableSendBytes += frame.Capacity;
            }
            else
            {
                sendCount++;
                sendBytes += frame.Capacity;
            }
        }

        private async Task SendLoopAsync()
        {
            var kcp = transport as KcpTransport;
            bool retry = false;
            bool tryUnreliable = false;
            while (!lifetime.IsCancellationRequested)
            {
                if (kcp == null)
                {
                    await sendSignal.WaitAsync(lifetime.Token).ConfigureAwait(false);
                }
                else
                {
                    bool empty;
                    lock (gate)
                    {
                        empty = controls.Count == 0 && sends.Count == 0 && unreliableSends.Count == 0;
                    }
                    if (retry || empty)
                    {
                        await sendSignal.WaitAsync(retry ? 10 : Timeout.Infinite, lifetime.Token).ConfigureAwait(false);
                    }
                }
                SendEntry item;
                Queue<SendEntry> queue;
                bool retained = false;
                bool discard;
                Exception sendFailure = null;
                lock (gate)
                {
                    queue = controls.Count != 0 ? controls :
                        unreliableSends.Count != 0 && (sends.Count == 0 || tryUnreliable) ? unreliableSends : sends;
                    if (queue.Count == 0)
                    {
                        continue;
                    }

                    item = kcp == null ? queue.Dequeue() : queue.Peek();
                    if (item.Control)
                    {
                        inFlightControl = item;
                    }
                    discard = (item.Pending != null && item.Pending.State != 0) ||
                        (!item.Control && stopped) || (!item.CloseOutput && peerClosed.Task.IsCompleted);
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

                    if (item.Control && !peerClosed.Task.IsCompleted && now - item.Frame.AdmittedAt >= 15000)
                    {
                        sendFailure = new NetworkException("Control frame send deadline elapsed.");
                    }

                    if (now - item.Frame.AdmittedAt > options.MaxSendQueueAge.TotalMilliseconds)
                    {
                        queueAgeWarnings++;
                        if (item.Frame.Delivery == NetDelivery.Unreliable)
                        {
                            discard = true;
                            unreliableDropped++;
                        }
                        else if (!item.Control && !discard)
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
                            await transport.CloseOutputAsync(lifetime.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            if (kcp != null && !await kcp.TrySendAsync(item.Frame, lifetime.Token, () => CanSend(item)).ConfigureAwait(false))
                            {
                                retained = true;
                                retry = true;
                                tryUnreliable = !item.Control && item.Frame.Delivery == NetDelivery.Reliable;
                                continue;
                            }
                            if (kcp == null)
                            {
                                await transport.SendAsync(item.Frame, lifetime.Token, () => CanSend(item)).ConfigureAwait(false);
                            }
                        }
                        lock (gate)
                        {
                            if (item.Frame.AcceptedByTransport)
                            {
                                sentFrames++;
                                sentApplicationBytes += item.Frame.Length;
                                if (!item.Control)
                                {
                                    sentBusinessBodyBytes += item.Frame.Header.BodyLength;
                                }
                            }

                            if (item.Frame.AcceptedByTransport && item.Frame.Header.Opcode == (uint)ControlOpcode.Ping && pendingNonce != 0 &&
                                pendingNonce == ControlCodec.ReadNonce(item.Frame.Body))
                            {
                                lastPing = getMilliseconds();
                                pongDeadline = lastPing + 15000;
                            }
                        }
                    }

                    item.Source?.TrySetResult(true);
                    retry = false;
                    tryUnreliable = false;
                }
                catch (Exception error)
                {
                    if (peerClosed.Task.IsCompleted && error is WebSocketException)
                    {
                        item.Source?.TrySetResult(true);
                        retry = false;
                        tryUnreliable = false;
                        continue;
                    }
                    if (error is OperationCanceledException canceled)
                    {
                        item.Source?.TrySetCanceled(canceled.CancellationToken);
                    }
                    else
                    {
                        item.Source?.TrySetException(error);
                    }
                    throw;
                }
                finally
                {
                    lock (gate)
                    {
                        if (!retained)
                        {
                            if (kcp != null)
                            {
                                queue.Dequeue();
                            }
                            ReleaseSend(item);
                        }
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
            else if (item.Frame.Delivery == NetDelivery.Unreliable)
            {
                unreliableSendCount--;
                unreliableSendBytes -= item.Frame.Capacity;
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
                OwnedFrame frame = await transport.ReceiveAsync(receiveMemory, reliableLimit, unreliableLimit,
                    FrameDirection.ServerToClient, false, lifetime.Token, ValidateHeader, unreliableReceiveMemory).ConfigureAwait(false);
                if (frame == null)
                {
                    peerClosed.TrySetResult(true);
                    lock (gate)
                    {
                        orderly = true;
                        stopped = true;
                    }
                    if (connect.Transport == NetworkTransportKind.WebSocket)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        Task output = QueueClose(true);
                        Task completed = await Task.WhenAny(output, Task.Delay(2000, timeout.Token)).ConfigureAwait(false);
                        if (ReferenceEquals(completed, output))
                        {
                            try
                            {
                                await output.ConfigureAwait(false);
                            }
                            catch (WebSocketException)
                            {
                                // The peer already ended the transport. Keep earlier responses
                                // ordered even when its close acknowledgement cannot be sent.
                            }
                        }
                    }
                    lock (gate)
                    {
                        if (!remoteCloseReceived)
                        {
                            receives.Enqueue(new ReceiveEntry { End = true });
                        }
                    }

                    return;
                }

                bool transferred = false;
                try
                {
                    if (remoteCloseReceived)
                    {
                        throw new NetworkProtocolException("Application input after remote Close.");
                    }
                    lock (gate)
                    {
                        receivedApplicationBytes += frame.Length;
                        if (frame.Header.Kind != FrameKind.Control)
                        {
                            receivedBusinessBodyBytes += frame.Header.BodyLength;
                        }
                    }
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

                        if (frame.Delivery == NetDelivery.Unreliable)
                        {
                            while (unreliableReceives.Count >= options.UnreliableQueueCount ||
                                frame.Capacity > options.UnreliableQueueBytes - unreliableReceiveBytes)
                            {
                                ReceiveEntry oldest = unreliableReceives.Dequeue();
                                unreliableReceiveBytes -= oldest.Frame.Capacity;
                                oldest.Frame.Dispose();
                                unreliableDropped++;
                            }

                            unreliableReceives.Enqueue(new ReceiveEntry { Frame = frame });
                            unreliableReceiveBytes += frame.Capacity;
                            transferred = true;
                            continue;
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

        private void ValidateHeader(FrameHeader header, NetDelivery delivery)
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
                    if (route.Kind != NetRouteKind.Push || route.Delivery != delivery || header.BodyLength > route.MaxBodyBytes)
                    {
                        throw new NetworkProtocolException("Push route/body/channel mismatch.");
                    }
                    if (delivery == NetDelivery.Unreliable)
                    {
                        int capacity = 32 + header.BodyLength + FrameMemoryBudget.FrameRecordReservation;
                        while (unreliableReceives.Count != 0 && (unreliableReceives.Count >= options.UnreliableQueueCount ||
                            capacity > options.UnreliableQueueBytes - unreliableReceiveMemory.OwnedBytes))
                        {
                            ReceiveEntry oldest = unreliableReceives.Dequeue();
                            unreliableReceiveBytes -= oldest.Frame.Capacity;
                            oldest.Frame.Dispose();
                            unreliableDropped++;
                        }
                    }

                }
            }
        }

        private void ValidateControlHeader(FrameHeader header, bool helloComplete)
        {
            ControlCodec.ValidatePhase((ControlOpcode)header.Opcode, helloComplete, FrameDirection.ServerToClient);
            ControlCodec.ValidateBodyLength((ControlOpcode)header.Opcode, header.BodyLength, connect.Transport);
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

                    if (pongDeadline != 0 && getMilliseconds() >= pongDeadline)
                    {
                        throw new NetworkException("Application heartbeat timed out.");
                    }

                    pingRoundTripMilliseconds = Math.Max(0, getMilliseconds() - lastPing);
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
                    remoteCloseReceived = true;
                    receives.Enqueue(new ReceiveEntry { End = true });
                }

                // Reliable packets validated before Close are delivered before the end marker.
                if (connect.Transport == NetworkTransportKind.WebSocket)
                {
                    CloseIoTask = CloseIoTask ?? Task.Run(() => CloseIoAsync(true));
                }
                else
                {
                    StopIo();
                }
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

        private void CheckTransportClock()
        {
            lock (gate)
            {
                if (!ready || stopped)
                {
                    return;
                }
            }

            CheckClock();
        }

        private bool CanSend(SendEntry item)
        {
            lock (gate)
            {
                if ((!item.Control && stopped) || peerClosed.Task.IsCompleted)
                {
                    return false;
                }
                if (item.Pending == null)
                {
                    return !stopped || item.Control;
                }

                if (item.Pending.State != 0)
                {
                    return false;
                }

                if (item.Pending.CancellationToken.IsCancellationRequested)
                {
                    SetTerminal(item.Pending, new OperationCanceledException(item.Pending.CancellationToken));
                    cancellations++;
                    return false;
                }

                if (getMilliseconds() >= item.Pending.Deadline)
                {
                    SetTerminal(item.Pending, new NetworkRequestTimeoutException("RPC deadline elapsed."));
                    timeouts++;
                    return false;
                }

                if (getMilliseconds() - item.Frame.AdmittedAt >= options.MaxSendQueueAge.TotalMilliseconds)
                {
                    SetTerminal(item.Pending, new NetworkBackpressureException("KCP application send queue age exceeded."));
                    return false;
                }

                return true;
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
                if (!stopped && oldestControl != null && now - oldestControl.Frame.AdmittedAt >= 15000)
                {
                    throw new NetworkException("Control frame send deadline elapsed.");
                }

                if (!stopped && pendingNonce != 0 && pongDeadline != 0 && now >= pongDeadline)
                {
                    throw new NetworkException("Application heartbeat timed out.");
                }

                if (!stopped && pendingNonce == 0 && now - lastPing >= 5000)
                {
                    pendingNonce = nonce = nextNonce = checked(nextNonce + 1);
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
                if (!closing && failure == null)
                {
                    failure = error;
                    orderly = false;
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
                        receives.Count == 0 && unreliableReceives.Count == 0 && completions.Count == 0 && (!closing || closed.Task.IsCompleted);
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
                transport.Abort();
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
            CloseIoTask = Task.Run(() => CloseIoAsync());
        }

        internal Task CloseIoTask { get; private set; }

        private async Task CloseIoAsync(bool remote = false)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                Task send = remote ? Task.CompletedTask : QueueClose(false);
                Task completed = await Task.WhenAny(send, Task.Delay(2000, timeout.Token)).ConfigureAwait(false);
                if (ReferenceEquals(completed, send))
                {
                    await send.ConfigureAwait(false);
                    Task output = QueueClose(true);
                    await Task.WhenAny(output, Task.Delay(2000, timeout.Token)).ConfigureAwait(false);
                    if (output.IsCompleted)
                    {
                        await output.ConfigureAwait(false);
                        if (connect.Transport == NetworkTransportKind.WebSocket)
                        {
                            await Task.WhenAny(peerClosed.Task, Task.Delay(2000, timeout.Token)).ConfigureAwait(false);
                        }
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
                if (output && outputCloseTask != null)
                {
                    return outputCloseTask;
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
                    if (output)
                    {
                        outputCloseTask = source.Task;
                    }
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
                else if (unreliableReceives.Count != 0 && (receives.Count == 0 || reliablePumpCount >= 16))
                {
                    receive = unreliableReceives.Dequeue();
                    unreliableReceiveBytes -= receive.Frame.Capacity;
                    reliablePumpCount = reliablePumpCount < 16 ? 17 : reliablePumpCount == 19 ? 0 : reliablePumpCount + 1;
                }
                else if (receives.Count != 0)
                {
                    receive = receives.Dequeue();
                    reliablePumpCount = Math.Min(16, reliablePumpCount + 1);
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
                    if (receive.Frame.Delivery == NetDelivery.Unreliable &&
                        getMilliseconds() - receive.Frame.AdmittedAt >= options.UnreliableMaxAge.TotalMilliseconds)
                    {
                        unreliableDropped++;
                    }
                    else
                    {
                        Push?.Invoke(receive.Frame.Header.Opcode, receive.Frame);
                    }
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
                else if (unreliableReceives.Count != 0)
                {
                    frame = unreliableReceives.Dequeue().Frame;
                    unreliableReceiveBytes -= frame.Capacity;
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
                lifetime.Dispose();
                sendSignal.Dispose();
                publishedState = NetworkClientState.Idle;
            }

            StateChanged?.Invoke(NetworkClientState.Idle, null);
            if (cleanupFailure == null)
            {
                closed.TrySetResult(true);
            }
            else
            {
                closed.TrySetException(cleanupFailure);
            }
        }

        private void RecordCleanupFailure(Exception error)
        {
            lock (gate)
            {
                cleanupFailure = cleanupFailure ?? error;
            }

            Fail(error);
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
                if (closing || failure != null)
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
                    deadlines.Count, sentFrames, receivedFrames, lateResponses, timeouts, cancellations, queueAgeWarnings,
                    transport is KcpTransport kcp ? kcp.GetMetrics() : default, unreliableDropped,
                    memory.PeakReservedBytes, sentApplicationBytes, receivedApplicationBytes,
                    sentBusinessBodyBytes, receivedBusinessBodyBytes, pingRoundTripMilliseconds);
            }
        }
    }
}
