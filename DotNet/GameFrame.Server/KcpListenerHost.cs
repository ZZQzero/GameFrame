using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace GameFrame.Network
{
    // The listener owns the socket and every child native handle. Application handlers never call native.
    public sealed class KcpListenerHost : IAsyncDisposable
    {
        private static readonly Meter Metrics = new Meter("GameFrame.Server.Network.Kcp");
        private static readonly Histogram<double> OwnerWork = Metrics.CreateHistogram<double>("gameframe.kcp.owner.work", "ms");
        private static readonly Histogram<double> OwnerInterval = Metrics.CreateHistogram<double>("gameframe.kcp.owner.interval", "ms");
        private static readonly Histogram<long> OwnerAllocated = Metrics.CreateHistogram<long>("gameframe.kcp.owner.allocated", "By");
        private static readonly Histogram<double> SendQueueAge = Metrics.CreateHistogram<double>("gameframe.kcp.send.queue", "ms");
        private static readonly Histogram<double> DispatchQueueAge = Metrics.CreateHistogram<double>("gameframe.kcp.dispatch.queue", "ms");

        // Listener source policy. Ports share one source budget; a full table rejects new sources.
        internal sealed class SourceBudget
        {
            private readonly Dictionary<IPAddress, (int Packets, int Bytes)> sources =
                new Dictionary<IPAddress, (int Packets, int Bytes)>(1024);
            private long window = -1;
            private readonly KcpServerOptions options;

            internal SourceBudget(KcpServerOptions? options = null)
            {
                this.options = options ?? new KcpServerOptions();
            }

            internal int Count => sources.Count;

            internal bool TryAccept(IPAddress address, int bytes, long now)
            {
                long currentWindow = now / 1000;
                if (currentWindow != window)
                {
                    sources.Clear();
                    window = currentWindow;
                }

                bool exists = sources.TryGetValue(address, out var usage);
                if ((!exists && sources.Count >= options.SourceCount) || usage.Packets >= options.SourcePacketsPerSecond || bytes > options.SourceBytesPerSecond - usage.Bytes)
                {
                    return false;
                }

                sources[address] = (usage.Packets + 1, usage.Bytes + bytes);
                return true;
            }
        }

        private sealed class Child
        {
            internal KcpNative.ConnectionEvent Event;
            internal readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
            internal readonly object InputGate = new object();
            internal readonly Queue<OwnedFrame> Incoming = new Queue<OwnedFrame>(128);
            internal readonly Queue<OwnedFrame> IncomingUnreliable = new Queue<OwnedFrame>(64);
            internal readonly SemaphoreSlim InputSignal = new SemaphoreSlim(0);
            internal bool InputCompleted;
            internal required FrameMemoryBudget UnreliableMemory;
            internal required Channel<OwnedFrame> Outgoing;
            internal required Channel<OwnedFrame> OutgoingUnreliable;
            internal readonly Queue<OwnedFrame> Controls = new Queue<OwnedFrame>();
            internal required FrameMemoryBudget Memory;
            internal required FrameMemoryBudget SendMemory;
            internal required FrameMemoryBudget ReceiveMemory;
            internal required FrameMemoryBudget ControlMemory;
            internal Task? Dispatch;
            internal Task? Finalizing;
            internal ServerConnectionContext? Context;
            internal volatile bool SessionReady;
            internal volatile bool ApplicationReady;
            internal volatile bool Retired;
            internal volatile bool StopRequested;
            internal Exception? CleanupFailure;
            internal ushort CloseReason;
            internal ulong LastCorrelation;
            private Exception? failure;
            internal bool TryRecordFailure(Exception error) => Interlocked.CompareExchange(ref failure, error, null) == null;
            internal Exception? Failure
            {
                get => Volatile.Read(ref failure);
                set
                {
                    if (value != null)
                    {
                        Interlocked.CompareExchange(ref failure, value, null);
                    }
                }
            }
            internal bool Bound, RemoteClosed;
            internal bool ServerHelloQueued;
            internal uint Features;
            internal bool ServerClosing, CloseSent;
            internal volatile bool DiscardInput;
            internal volatile bool TransportEnded;
            internal bool InputDrained;
            internal long CloseDeadline;
            internal int ReliableLimit, UnreliableLimit;
            internal long LastReceived, RateWindow, PingAt, PongDeadline;
            internal int Packets, Bytes;
            internal ulong Nonce, NonceSequence;
        }

        private readonly IPEndPoint endpoint;
        private readonly NetProtocolCatalog catalog;
        private readonly ServerApplication application;
        private readonly KcpServerOptions options;
        private readonly FrameMemoryBudget memory;
        private readonly ConcurrentQueue<Action<IntPtr>> commands = new ConcurrentQueue<Action<IntPtr>>();
        private readonly Dictionary<ulong, Child> children = new Dictionary<ulong, Child>();
        private readonly List<Child> retired = new List<Child>();
        private readonly Queue<Child> releaseReady = new Queue<Child>();
        private readonly SourceBudget sourceBudget;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly TaskCompletionSource<bool> started = Source<bool>();
        private readonly TaskCompletionSource<bool> stopped = Source<bool>();
        private readonly object gate = new object();
        private readonly Thread thread;
        private readonly ILogger logger;
        private bool stopping;
        private int commandCount;
        private long grantSequence;
        private Exception? failure;
        private IPEndPoint? localEndpoint;
        public IPEndPoint? LocalEndpoint
        {
            get
            {
                IPEndPoint? value = Volatile.Read(ref localEndpoint);
                return value == null ? null : ServerConnectionContext.CopyEndpoint(value);
            }
        }
        public long OwnedBytes => memory.OwnedBytes;
        public Task Started => started.Task;
        public Task Stopped => stopped.Task;

        public KcpListenerHost(IPEndPoint endpoint, ServerApplication application, KcpServerOptions? options = null, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            this.endpoint = ServerConnectionContext.CopyEndpoint(endpoint);
            this.application = application ?? throw new ArgumentNullException(nameof(application));
            this.options = (options ?? new KcpServerOptions()).Snapshot();
            application.Freeze();
            catalog = application.Catalog;
            memory = new FrameMemoryBudget(this.options.MaxOwnedBytes);
            sourceBudget = new SourceBudget(this.options);
            this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            thread = new Thread(Run) { IsBackground = true, Name = "GameFrame KCP Listener" };
            thread.Start();
        }

        private static TaskCompletionSource<T> Source<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ulong> RegisterGrantAsync(uint conv, byte[] identity, byte[] psk, byte[] ticket, int ttlMs = 30000)
        {
            if (conv == 0 || identity == null || psk == null || ticket == null || identity.Length != 16 || psk.Length != 32 || ticket.Length < 1 || ticket.Length > 1024 || ttlMs < 1 || ttlMs > 30000)
            {
                throw new ArgumentException("Invalid KCP grant.");
            }

            long deadline = checked(NetworkClock.GetMilliseconds() + ttlMs);
            await started.Task.ConfigureAwait(false);
            ulong id = checked((ulong)Interlocked.Increment(ref grantSequence));
            var grant = new KcpNative.Grant
            {
                Size = 104, Version = KcpNative.Version, Id = id, Conv = conv, RemainingTtlMs = (uint)ttlMs,
                Identity = (byte[])identity.Clone(), Psk = (byte[])psk.Clone(), TicketDigest = SHA256.HashData(ticket)
            };
            try
            {
                await CommandAsync(listener =>
                {
                    long now = NetworkClock.GetMilliseconds();
                    if (now >= deadline)
                    {
                        throw new NetworkException("Grant expired before registration.");
                    }
                    grant.RemainingTtlMs = checked((uint)(deadline - now));
                    KcpNative.Check(KcpNative.RegisterGrant(listener, ref grant, (uint)now), "register grant");
                }).ConfigureAwait(false);
                return id;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(grant.Psk);
                CryptographicOperations.ZeroMemory(grant.Identity);
                CryptographicOperations.ZeroMemory(grant.TicketDigest);
            }
        }

        public Task RevokeGrantAsync(ulong id) => CommandAsync(listener => KcpNative.Check(KcpNative.RevokeGrant(listener, id), "revoke grant"));

        private Task CommandAsync(Action<IntPtr> action)
        {
            var source = Source<bool>();
            lock (gate)
            {
                if (stopping || failure != null)
                {
                    throw failure == null ? new NetworkException("KCP listener is unavailable.") :
                        new NetworkException("KCP listener is unavailable.", failure);
                }

                if (commandCount >= options.CommandQueueCount)
                {
                    throw new NetworkBackpressureException("Listener command queue is full.");
                }

                commandCount++;
                commands.Enqueue(listener =>
                {
                    try
                    {
                        if (listener == IntPtr.Zero)
                        {
                            throw new NetworkException("KCP listener stopped before executing the command.");
                        }

                        action(listener);
                        source.TrySetResult(true);
                    }
                    catch (Exception error)
                    {
                        source.TrySetException(error);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref commandCount);
                    }
                });
                wake.Set();
            }

            return source.Task;
        }

        private void Run()
        {
            IntPtr listener = IntPtr.Zero;
            Socket? socket = null;
            OwnedFrame? input = null, output = null, header = null;
            int outputLength = 0;
            var metadata = new KcpNative.UdpMetadata { Size = 64, Version = KcpNative.Version };
            long previous = 0;
            long previousMeasurement = 0;
            try
            {
                KcpNative.ValidateAbi();
                var config = new KcpNative.ListenerConfig
                {
                    Size = 32, Version = KcpNative.Version, NativeLimit = (uint)options.NativeBytes, ConnectionLimit = (uint)options.ConnectionNativeBytes,
                    MaxConnections = (uint)options.MaxConnections, MaxHandshakes = (uint)options.MaxHandshakes, MaxGrants = (uint)options.MaxGrants
                };
                KcpNative.Check(KcpNative.ListenerCreate(ref config, out listener), "listener create");
                input = memory.Allocate(1201);
                output = memory.Allocate(1200);
                header = memory.Allocate(32);
                socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false, ReceiveBufferSize = 256 * 1024, SendBufferSize = 256 * 1024
                };
                socket.Bind(endpoint);
                Volatile.Write(ref localEndpoint, (IPEndPoint)socket.LocalEndPoint!);
                started.TrySetResult(true);
                EndPoint source = new IPEndPoint(endpoint.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
                while (!Volatile.Read(ref stopping))
                {
                    bool measureWork = OwnerWork.Enabled;
                    bool measureInterval = OwnerInterval.Enabled;
                    bool measureAllocation = OwnerAllocated.Enabled;
                    long intervalStart = measureInterval ? Stopwatch.GetTimestamp() : 0;
                    if (measureInterval && previousMeasurement != 0)
                    {
                        OwnerInterval.Record(Stopwatch.GetElapsedTime(previousMeasurement, intervalStart).TotalMilliseconds);
                    }

                    previousMeasurement = intervalStart;
                    long allocatedStart = measureAllocation ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    long measurementStart = measureWork ? Stopwatch.GetTimestamp() : 0;

                    long now = NetworkClock.GetMilliseconds();
                    if (previous != 0 && (now < previous || now - previous >= int.MaxValue))
                    {
                        throw new NetworkException("Listener clock gap exceeded native range.");
                    }

                    previous = now;
                    for (int i = 0; i < 64 && commands.TryDequeue(out Action<IntPtr>? command); i++)
                    {
                        command(listener);
                    }

                    // Grant commands may advance the listener clock; never feed/tick with an older round timestamp.
                    now = NetworkClock.GetMilliseconds();
                    int outputBudget = 256;
                    FlushUdp(socket, listener, output, ref outputLength, ref metadata, now, ref outputBudget);
                    long readStart = NetworkClock.GetMilliseconds();
                    // Explicit host-wide admission budget bounds work before peer lookup/cookie processing.
                    for (int i = 0; i < 256 && NetworkClock.GetMilliseconds() - readStart < 2; i++)
                    {
                        int length;
                        try
                        {
                            length = socket.ReceiveFrom(input.Bytes, 0, input.Capacity, SocketFlags.None, ref source);
                        }
                        catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
                        {
                            break;
                        }
                        catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize)
                        {
                            continue;
                        }

                        if (length < 1 || length > 1200)
                        {
                            continue;
                        }

                        var remote = (IPEndPoint)source;
                        if (remote.Address.IsIPv6LinkLocal || !sourceBudget.TryAccept(remote.Address, length, now))
                        {
                            continue;
                        }

                        KcpNative.Peer peer = ToPeer(remote);
                        // Feed may report an internal failure of a transient handshake. Child faults
                        // belong to native events; shared listener failures are checked by tick/poll.
                        KcpNative.ListenerFeed(listener, ref peer, input.Bytes, length, (uint)now);
                    }

                    KcpNative.Check(KcpNative.ListenerTick(listener, (uint)now), "listener tick");
                    PollEvents(listener);
                    foreach (Child child in children.Values)
                    {
                        try
                        {
                            DriveChild(listener, child, header, input, now, outputLength == 0);
                            if (child.InputDrained)
                            {
                                releaseReady.Enqueue(child);
                            }
                        }
                        catch (Exception error)
                        {
                            RecordChildFailure(child, error, "Transport");
                            if (child.TransportEnded)
                            {
                                releaseReady.Enqueue(child);
                            }
                            else
                            {
                                KcpNative.Check(KcpNative.CloseConnection(listener, child.Event.Id, child.Event.Generation), "close child");
                            }
                        }
                    }

                    while (releaseReady.TryDequeue(out Child? completed))
                    {
                        children.Remove(completed.Event.Id);
                        Retire(completed, completed.Failure == null && !completed.DiscardInput);
                        KcpNative.Check(KcpNative.ReleaseConnection(listener, completed.Event.Id, completed.Event.Generation), "release drained child");
                    }
                    PollEvents(listener);
                    FlushUdp(socket, listener, output, ref outputLength, ref metadata, now, ref outputBudget);
                    ReapRetired();
                    // Capture both values before their observers run; observers can allocate on this thread.
                    double workMilliseconds = measureWork ? Stopwatch.GetElapsedTime(measurementStart).TotalMilliseconds : 0;
                    long allocatedBytes = measureAllocation ? GC.GetAllocatedBytesForCurrentThread() - allocatedStart : 0;
                    if (measureWork)
                    {
                        OwnerWork.Record(workMilliseconds);
                    }
                    if (measureAllocation)
                    {
                        OwnerAllocated.Record(allocatedBytes);
                    }
                    wake.WaitOne(10);
                }
            }
            catch (Exception error)
            {
                failure = error;
                NetworkLog.Failure(logger, LogLevel.Critical, NetworkLog.ListenerFailure, error,
                    NetworkTransportKind.Kcp, 0, 0, "Owner");
                started.TrySetException(error);
            }
            finally
            {
                lock (gate)
                {
                    stopping = true;
                }

                Cleanup(() => socket?.Dispose());
                foreach (Child child in children.Values)
                {
                    child.Failure ??= failure;
                    Cleanup(() => Retire(child));
                }

                children.Clear();
                if (listener != IntPtr.Zero)
                {
                    IntPtr ownedListener = listener;
                    listener = IntPtr.Zero;
                    Cleanup(() => KcpNative.Check(KcpNative.ListenerDestroy(ownedListener), "listener destroy"));
                }

                while (commands.TryDequeue(out Action<IntPtr>? command))
                {
                    command(IntPtr.Zero);
                }

                foreach (Child child in retired)
                {
                    Cleanup(() => child.Lifetime.Cancel());
                }
                // Async application handlers are canceled and remain owners until they finish.
                Cleanup(() => Task.WhenAll(retired.Select(child => child.Finalizing ?? Task.CompletedTask)).GetAwaiter().GetResult());
                Cleanup(ReapRetired);
                input?.Dispose();
                output?.Dispose();
                header?.Dispose();
                lock (gate)
                {
                    if (failure == null)
                    {
                        stopped.TrySetResult(true);
                    }
                    else
                    {
                        stopped.TrySetException(failure);
                    }

                    wake.Dispose();
                }
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
                failure ??= error;
                NetworkLog.Failure(logger, LogLevel.Error, NetworkLog.ListenerCleanupFailure, error,
                    NetworkTransportKind.Kcp, 0, 0, "Cleanup");
            }
        }

        private void RecordChildFailure(Child child, Exception error, string stage, uint opcode = 0, ulong correlationId = 0)
        {
            if (child.TryRecordFailure(error))
            {
                LogLevel level = error is NetworkException or SocketException or OperationCanceledException
                    ? LogLevel.Warning : LogLevel.Error;
                NetworkLog.Failure(logger, level, NetworkLog.ConnectionFailure, error, NetworkTransportKind.Kcp,
                    child.Event.Id, child.Event.Generation, stage, opcode, correlationId, child.CloseReason);
            }
        }

        private void RecordChildCleanupFailure(Child child, Exception error, string stage)
        {
            child.Failure = error;
            child.CleanupFailure ??= error;
            NetworkLog.Failure(logger, LogLevel.Error, NetworkLog.ConnectionCleanupFailure, error,
                NetworkTransportKind.Kcp, child.Event.Id, child.Event.Generation, stage, closeReason: child.CloseReason);
        }

        private void PollEvents(IntPtr listener)
        {
            var connectionEvent = new KcpNative.ConnectionEvent { Size = 72, Version = KcpNative.Version };
            int result;
            while ((result = KcpNative.ListenerPollEvent(listener, ref connectionEvent)) == 1)
            {
                if (connectionEvent.Kind == 1)
                {
                    if (children.Count + retired.Count >= options.MaxConnections)
                    {
                        KcpNative.CloseConnection(listener, connectionEvent.Id, connectionEvent.Generation);
                        continue;
                    }
                    var childMemory = new FrameMemoryBudget(options.ConnectionOwnedBytes, memory);
                    children.Add(connectionEvent.Id, new Child
                    {
                        Event = connectionEvent, Memory = childMemory,
                        Outgoing = Channel.CreateBounded<OwnedFrame>(options.SendQueueCount),
                        OutgoingUnreliable = Channel.CreateBounded<OwnedFrame>(options.UnreliableQueueCount),
                        SendMemory = new FrameMemoryBudget(options.SendQueueBytes, childMemory),
                        ReceiveMemory = new FrameMemoryBudget(options.ReceiveQueueBytes, childMemory),
                        ControlMemory = new FrameMemoryBudget(options.ControlQueueBytes, childMemory),
                        UnreliableMemory = new FrameMemoryBudget(options.UnreliableQueueBytes, childMemory),
                        LastReceived = NetworkClock.GetMilliseconds(), PingAt = NetworkClock.GetMilliseconds()
                    });
                }
                else
                {
                    if (connectionEvent.Reason == 0 && children.TryGetValue(connectionEvent.Id, out Child? ended) &&
                        ended.Bound && ended.Failure == null && !ended.StopRequested)
                    {
                        ended.TransportEnded = true;
                        continue;
                    }
                    if (children.Remove(connectionEvent.Id, out Child? child))
                    {
                        if (child.CloseReason == 0)
                        {
                            child.CloseReason = child.Context?.RequestedCloseReason ?? 0;
                            if (child.CloseReason == 0 && connectionEvent.Reason != 0)
                            {
                                child.CloseReason = 1;
                            }
                        }
                        if (connectionEvent.Reason != 0)
                        {
                            RecordChildFailure(child, new NetworkException($"Native KCP connection closed ({connectionEvent.Reason})."), "NativeClose");
                        }

                        Retire(child);
                    }

                    KcpNative.Check(KcpNative.ReleaseConnection(listener, connectionEvent.Id, connectionEvent.Generation), "release child");
                }
            }

            KcpNative.Check(result, "listener events");
        }

        private void DriveChild(IntPtr listener, Child child, OwnedFrame header, OwnedFrame scratch, long now, bool writable)
        {
            if (child.StopRequested)
            {
                KcpNative.Check(KcpNative.CloseConnection(listener, child.Event.Id, child.Event.Generation), "application close");
                return;
            }

            ushort requestedReason = child.Context?.RequestedCloseReason ?? 0;
            if (child.ApplicationReady && !child.RemoteClosed && requestedReason == 0 &&
                child.Context!.Session.CheckDeadline(now) is ushort deadlineReason)
            {
                child.Context.RequestClose(deadlineReason);
                requestedReason = child.Context.RequestedCloseReason;
            }

            if (child.Failure != null && requestedReason == 0)
            {
                throw new NetworkException("Application handler failed.", child.Failure);
            }

            if (requestedReason != 0 && !child.RemoteClosed && !child.TransportEnded)
            {
                child.CloseReason = requestedReason;
                child.ServerClosing = true;
                child.RemoteClosed = true;
                child.CloseDeadline = now + 2000;
                lock (child.InputGate)
                {
                    child.DiscardInput = true;
                    while (child.Incoming.TryDequeue(out OwnedFrame? pending))
                    {
                        pending.Dispose();
                    }

                    while (child.IncomingUnreliable.TryDequeue(out OwnedFrame? pending))
                    {
                        pending.Dispose();
                    }

                    while (child.Outgoing.Reader.TryRead(out OwnedFrame? pending))
                    {
                        pending.Dispose();
                    }

                    while (child.OutgoingUnreliable.Reader.TryRead(out OwnedFrame? pending))
                    {
                        pending.Dispose();
                    }
                }

                CompleteInput(child);
            }

            // Publish the session before acknowledging Hello, so a conforming peer cannot
            // submit business frames before its admission policy exists.
            if (child.SessionReady && !child.ServerHelloQueued && !child.RemoteClosed && !child.TransportEnded)
            {
                QueueServerHello(child);
                child.ServerHelloQueued = true;
            }

            for (int i = 0; i < 16 && !child.RemoteClosed; i++)
            {
                int result = KcpNative.Peek(child.Event.Connection, out int channel, out int length, out uint born);
                KcpNative.Check(result, "child peek");
                if (result == 0)
                {
                    if (child.TransportEnded)
                    {
                        child.InputDrained = true;
                        CompleteInput(child);
                    }
                    break;
                }

                NetDelivery delivery = channel == 1 ? NetDelivery.Reliable : NetDelivery.Unreliable;
                long receivedAt = now - unchecked((uint)now - born);
                if (delivery == NetDelivery.Unreliable && (child.TransportEnded || now - receivedAt >= 250))
                {
                    if (KcpNative.Receive(child.Event.Connection, scratch.Bytes, scratch.Capacity) != length)
                    {
                        throw new NetworkProtocolException("Child unreliable discard returned inconsistent length.");
                    }
                    continue;
                }
                KcpNative.Check(KcpNative.PeekHeader(child.Event.Connection, header.Bytes, header.Capacity), "child header");
                FrameHeader decoded = FrameCodec.ReadHeader(header.Bytes, child.Bound ?
                    (channel == 1 ? child.ReliableLimit : child.UnreliableLimit) : 32768, delivery,
                    FrameDirection.ClientToServer, !child.Bound);
                ValidateHeader(decoded, delivery, child.Bound);
                if (decoded.Kind != FrameKind.Control && !child.ServerHelloQueued)
                {
                    throw new NetworkProtocolException("Business frame arrived before ServerHello.");
                }

                if (length != 32 + decoded.BodyLength)
                {
                    throw new NetworkProtocolException("Child frame length mismatch.");
                }

                if (child.RateWindow != now / 1000)
                {
                    child.RateWindow = now / 1000;
                    child.Packets = child.Bytes = 0;
                }

                if (++child.Packets > options.MaxPacketsPerSecond || (child.Bytes += length) > options.MaxBytesPerSecond)
                {
                    throw new NetworkProtocolException("Authenticated input rate exceeded local policy.");
                }

                if (delivery == NetDelivery.Unreliable)
                {
                    lock (child.InputGate)
                    {
                        while (child.IncomingUnreliable.Count != 0 && (child.IncomingUnreliable.Count >= options.UnreliableQueueCount ||
                            length + FrameMemoryBudget.FrameRecordReservation > options.UnreliableQueueBytes - child.UnreliableMemory.OwnedBytes))
                        {
                            child.IncomingUnreliable.Dequeue().Dispose();
                            child.InputSignal.Wait(0);
                        }
                    }
                }

                OwnedFrame frame;
                try
                {
                    frame = (delivery == NetDelivery.Reliable ? child.ReceiveMemory : child.UnreliableMemory).Allocate(length);
                }
                catch (NetworkBackpressureException) when (delivery == NetDelivery.Unreliable)
                {
                    // A running handler may still own a lease after queue eviction. Drop using existing scratch.
                    if (KcpNative.Receive(child.Event.Connection, scratch.Bytes, scratch.Capacity) != length)
                    {
                        throw new NetworkProtocolException("Child unreliable discard returned inconsistent length.");
                    }

                    continue;
                }

                bool transferred = false;
                try
                {
                    int actual = KcpNative.Receive(child.Event.Connection, frame.Bytes, frame.Capacity);
                    if (actual != length)
                    {
                        throw new NetworkProtocolException("Child receive failed or returned inconsistent length.");
                    }

                    frame.Header = decoded;
                    frame.Length = length;
                    frame.Delivery = delivery;
                    frame.AdmittedAt = receivedAt;
                    child.LastReceived = now;
                    if (decoded.Kind == FrameKind.Control)
                    {
                        HandleControl(listener, child, frame, now);
                    }
                    else
                    {
                        if (decoded.Kind == FrameKind.Request)
                        {
                            if (decoded.CorrelationId <= child.LastCorrelation)
                            {
                                throw new NetworkProtocolException("Request correlation IDs must strictly increase.");
                            }

                            child.LastCorrelation = decoded.CorrelationId;
                        }

                        ServerAdmission admission = child.Context!.Session.Admit(ServerApplication.Header(frame));
                        if (!admission.Allowed)
                        {
                            if (decoded.Kind == FrameKind.Request)
                            {
                                OwnedFrame rejection = child.SendMemory.Allocate(32);
                                try
                                {
                                    new BoundedFrameWriter(rejection).Complete(FrameKind.Response, decoded.Opcode, decoded.CorrelationId,
                                        admission.StatusCode == 0 ? 2 : admission.StatusCode);
                                    rejection.AdmittedAt = now;
                                    if (!child.Outgoing.Writer.TryWrite(rejection))
                                    {
                                        throw new NetworkBackpressureException("Rejected response queue is full.");
                                    }
                                }
                                catch
                                {
                                    rejection.Dispose();
                                    throw;
                                }
                            }

                            child.CloseReason = admission.CloseReason == 0 ? (ushort)5 : admission.CloseReason;
                            child.RemoteClosed = true;
                            child.ServerClosing = true;
                            child.CloseDeadline = now + 2000;
                            CompleteInput(child);
                            continue;
                        }

                        lock (child.InputGate)
                        {
                            Queue<OwnedFrame> queue = delivery == NetDelivery.Reliable ? child.Incoming : child.IncomingUnreliable;
                            if (queue.Count >= (delivery == NetDelivery.Reliable ? options.ReceiveQueueCount : options.UnreliableQueueCount))
                            {
                                throw new NetworkBackpressureException("Child receive queue full.");
                            }

                            queue.Enqueue(frame);
                            child.InputSignal.Release();
                            transferred = true;
                        }
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

            if (child.TransportEnded)
            {
                if (child.RemoteClosed)
                {
                    child.InputDrained = true;
                    CompleteInput(child);
                }
                return;
            }

            if (child.ServerHelloQueued && !child.RemoteClosed && now - child.PingAt >= 5000 && child.Nonce == 0)
            {
                child.PingAt = now;
                child.Nonce = checked(child.NonceSequence + 1);
                child.NonceSequence = child.Nonce;
                child.PongDeadline = 0;
                QueueNonce(child, ControlOpcode.Ping, child.Nonce);
            }

            if (!child.RemoteClosed && child.PongDeadline != 0 && now >= child.PongDeadline)
            {
                throw new NetworkException("Child heartbeat deadline elapsed.");
            }

            for (int i = 0; writable && i < 64; i++)
            {
                if (child.Controls.TryPeek(out OwnedFrame? control))
                {
                    if (!TrySendQueued(child, control, now))
                    {
                        break;
                    }
                    child.Controls.Dequeue();
                }
                else if (child.ServerHelloQueued && child.Outgoing.Reader.TryPeek(out OwnedFrame? reliable) && TrySendQueued(child, reliable, now))
                {
                    child.Outgoing.Reader.TryRead(out _);
                }
                else if (child.ServerHelloQueued && child.OutgoingUnreliable.Reader.TryPeek(out OwnedFrame? unreliable) && TrySendQueued(child, unreliable, now))
                {
                    child.OutgoingUnreliable.Reader.TryRead(out _);
                }
                else
                {
                    break;
                }
            }
            if (child.RemoteClosed)
            {
                if (child.ServerClosing && !child.CloseSent && (child.DiscardInput || child.Dispatch == null || child.Dispatch.IsCompleted) &&
                    !child.Outgoing.Reader.TryPeek(out _) && !child.OutgoingUnreliable.Reader.TryPeek(out _))
                {
                    OwnedFrame close = child.ControlMemory.Allocate(36);
                    try
                    {
                        ControlCodec.WriteClose(close.Bytes.AsSpan(32), child.CloseReason);
                        close.Header = new FrameHeader(FrameKind.Control, (uint)ControlOpcode.Close, 0, 4);
                        close.Length = 36;
                        FrameCodec.WriteHeader(close.Bytes, close.Header);
                        QueueControl(child, close);
                        child.CloseSent = true;
                    }
                    catch
                    {
                        close.Dispose();
                        throw;
                    }
                }

                var stats = new KcpNative.Stats { Size = 168, Version = KcpNative.Version };
                KcpNative.Check(KcpNative.GetStats(child.Event.Connection, ref stats), "closing child stats");
                if (now >= child.CloseDeadline || ((child.DiscardInput || child.Dispatch == null || child.Dispatch.IsCompleted) &&
                    child.Controls.Count == 0 && !child.Outgoing.Reader.TryPeek(out _) &&
                    !child.OutgoingUnreliable.Reader.TryPeek(out _) &&
                    stats.Waitsnd == 0 && stats.PlaintextCount == 0 && stats.OutputCount == 0))
                {
                    KcpNative.Check(KcpNative.CloseConnection(listener, child.Event.Id, child.Event.Generation), "orderly child close");
                }
            }
        }

        private static bool TrySendQueued(Child child, OwnedFrame frame, long now)
        {
            if (now - frame.AdmittedAt >= (frame.Header.Kind == FrameKind.Control ? 15000 : 250))
            {
                if (frame.Delivery == NetDelivery.Unreliable)
                {
                    frame.Dispose();
                    return true;
                }
                throw new NetworkBackpressureException("Child output application queue age exceeded.");
            }

            int result = frame.Delivery == NetDelivery.Reliable ? KcpNative.SendReliable(child.Event.Connection, frame.Bytes, frame.Length) :
                KcpNative.SendUnreliable(child.Event.Connection, frame.Bytes, frame.Length);
            if (result == KcpNative.Backpressure)
            {
                return false;
            }

            KcpNative.Check(result, "child send");
            if (frame.Header.Kind != FrameKind.Control && SendQueueAge.Enabled)
            {
                SendQueueAge.Record(Math.Max(0, now - frame.AdmittedAt));
            }
            if (frame.Header.Kind == FrameKind.Control && frame.Header.Opcode == (uint)ControlOpcode.Ping)
            {
                child.PingAt = NetworkClock.GetMilliseconds();
                child.PongDeadline = child.PingAt + 15000;
            }

            frame.Dispose();
            return true;
        }

        private void ValidateHeader(FrameHeader header, NetDelivery delivery, bool bound)
        {
            if (header.Kind == FrameKind.Control)
            {
                ControlCodec.ValidatePhase((ControlOpcode)header.Opcode, bound, FrameDirection.ClientToServer);
                ControlCodec.ValidateBodyLength((ControlOpcode)header.Opcode, header.BodyLength, NetworkTransportKind.Kcp);
                return;
            }

            NetRoute route = catalog.Find(header.Opcode);
            if (route.Delivery != delivery || header.BodyLength > route.MaxBodyBytes ||
                (header.Kind == FrameKind.Request ? route.Kind != NetRouteKind.Rpc : route.Kind != NetRouteKind.Notify))
            {
                throw new NetworkProtocolException("Child route/body/channel mismatch.");
            }
        }

        private void HandleControl(IntPtr listener, Child child, OwnedFrame frame, long now)
        {
            ControlOpcode opcode = (ControlOpcode)frame.Header.Opcode;
            if (opcode == ControlOpcode.ClientHello)
            {
                ClientHelloBody hello = ControlCodec.ReadClientHello(frame.Body, NetworkTransportKind.Kcp);
                if (!hello.Hash.SequenceEqual(catalog.Hash) || catalog.MaxReliableBodyBytes + 32 > hello.ReliableLimit ||
                    (hello.UnreliableLimit != 0 && catalog.MaxUnreliableBodyBytes + 32 > hello.UnreliableLimit))
                {
                    throw new NetworkProtocolException("ClientHello catalog/limits mismatch.");
                }

                byte[] ticket = hello.Ticket.ToArray();
                try
                {
                    KcpNative.Check(KcpNative.ConfirmBinding(listener, child.Event.Id, child.Event.Generation,
                        ticket, ticket.Length, (uint)now), "bind Hello");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ticket);
                }

                child.Bound = true;
                child.ReliableLimit = hello.ReliableLimit;
                child.UnreliableLimit = hello.UnreliableLimit;
                child.Features = hello.Features;

                child.Context = new ServerConnectionContext(child.Event.Id, child.Event.Generation, child.Event.GrantId,
                    NetworkTransportKind.Kcp, FromPeer(child.Event.Peer), catalog, child.SendMemory, child.Lifetime.Token,
                    frame => EnqueuePush(child, frame), () => RequestStop(child));
                child.Context.ReportSecondaryFailure = (error, opcode, correlation) => NetworkLog.Failure(logger, LogLevel.Error,
                    NetworkLog.ConnectionCleanupFailure, error, NetworkTransportKind.Kcp, child.Event.Id,
                    child.Event.Generation, "ReleaseResponse", opcode, correlation);
                child.Dispatch = Task.Run(() => DispatchAsync(child));
            }
            else if (opcode == ControlOpcode.Ping)
            {
                ulong nonce = ControlCodec.ReadNonce(frame.Body);
                if (!child.TransportEnded)
                {
                    QueueNonce(child, ControlOpcode.Pong, nonce);
                }
            }
            else if (opcode == ControlOpcode.Pong)
            {
                if (child.PongDeadline == 0 || now >= child.PongDeadline || ControlCodec.ReadNonce(frame.Body) != child.Nonce)
                {
                    throw new NetworkProtocolException("Unmatched child Pong.");
                }

                child.Nonce = 0;
                child.PongDeadline = 0;
            }
            else if (opcode == ControlOpcode.Close)
            {
                ushort peerReason = ControlCodec.ReadClose(frame.Body);
                if (child.CloseReason == 0)
                {
                    child.CloseReason = peerReason;
                }

                child.RemoteClosed = true;
                child.CloseDeadline = now + 2000;
                CompleteInput(child);
            }
        }

        private static void CompleteInput(Child child)
        {
            lock (child.InputGate)
            {
                if (!child.InputCompleted)
                {
                    child.InputCompleted = true;
                    child.InputSignal.Release();
                }
            }
        }

        private void QueueServerHello(Child child)
        {
            OwnedFrame reply = child.ControlMemory.Allocate(96);
            try
            {
                byte[] session = RandomNumberGenerator.GetBytes(16);
                ControlCodec.WriteServerHello(reply.Bytes.AsSpan(32), catalog.Hash, session, child.ReliableLimit,
                    child.UnreliableLimit, child.Features, NetworkTransportKind.Kcp);
                reply.Header = new FrameHeader(FrameKind.Control, (uint)ControlOpcode.ServerHello, 0, 64);
                reply.Length = 96;
                FrameCodec.WriteHeader(reply.Bytes, reply.Header);
                QueueControl(child, reply);
            }
            catch
            {
                reply.Dispose();
                throw;
            }
        }

        private void QueueNonce(Child child, ControlOpcode opcode, ulong nonce)
        {
            OwnedFrame reply = child.ControlMemory.Allocate(40);
            try
            {
                ControlCodec.WriteNonce(reply.Bytes.AsSpan(32), nonce);
                reply.Header = new FrameHeader(FrameKind.Control, (uint)opcode, 0, 8);
                reply.Length = 40;
                FrameCodec.WriteHeader(reply.Bytes, reply.Header);
                QueueControl(child, reply);
            }
            catch
            {
                reply.Dispose();
                throw;
            }
        }

        private void QueueControl(Child child, OwnedFrame frame)
        {
            if (child.Controls.Count >= options.ControlQueueCount)
            {
                throw new NetworkBackpressureException("Child control queue full.");
            }

            frame.AdmittedAt = NetworkClock.GetMilliseconds();
            child.Controls.Enqueue(frame);
        }

        private async Task DispatchAsync(Child child)
        {
            string stage = "Initialize";
            uint opcode = 0;
            ulong correlationId = 0;
            try
            {
                application.Initialize(child.Context!);
                child.SessionReady = true;
                stage = "Connected";
                await child.Context!.Session.ConnectedAsync(child.Context).ConfigureAwait(false);
                child.ApplicationReady = true;
                int reliableCount = 0;
                while (true)
                {
                    stage = "WaitForInput";
                    opcode = 0;
                    correlationId = 0;
                    await child.InputSignal.WaitAsync(child.Lifetime.Token).ConfigureAwait(false);
                    OwnedFrame? frame;
                    lock (child.InputGate)
                    {
                        if (child.Context!.RequestedCloseReason != 0)
                        {
                            return;
                        }

                        Queue<OwnedFrame> queue = child.IncomingUnreliable.Count != 0 && (child.Incoming.Count == 0 || reliableCount >= 16)
                            ? child.IncomingUnreliable : child.Incoming;
                        frame = queue.TryDequeue(out OwnedFrame? next) ? next : null;
                        if (frame == null && child.InputCompleted)
                        {
                            return;
                        }

                        if (frame != null)
                        {
                            reliableCount = frame.Delivery == NetDelivery.Reliable ? Math.Min(16, reliableCount + 1) :
                                reliableCount < 16 ? 17 : reliableCount == 19 ? 0 : reliableCount + 1;
                        }
                    }

                    if (frame == null)
                    {
                        continue;
                    }

                    using (frame)
                    {
                        if (frame.Delivery == NetDelivery.Unreliable && NetworkClock.GetMilliseconds() - frame.AdmittedAt > 250)
                        {
                            continue;
                        }

                        if (DispatchQueueAge.Enabled)
                        {
                            DispatchQueueAge.Record(Math.Max(0, NetworkClock.GetMilliseconds() - frame.AdmittedAt));
                        }

                        stage = "Dispatch";
                        opcode = frame.Header.Opcode;
                        correlationId = frame.Header.CorrelationId;
                        OwnedFrame? reply = await application.DispatchAsync(child.Context!, frame, child.SendMemory).ConfigureAwait(false);
                        if (reply != null)
                        {
                            bool transferred = false;
                            try
                            {
                                lock (child.InputGate)
                                {
                                    if (!child.TransportEnded && !child.Retired && !child.DiscardInput && child.Context!.RequestedCloseReason == 0)
                                    {
                                        child.Lifetime.Token.ThrowIfCancellationRequested();
                                        reply.AdmittedAt = NetworkClock.GetMilliseconds();
                                        if (!child.Outgoing.Writer.TryWrite(reply))
                                        {
                                            throw new NetworkBackpressureException("Child business send queue full.");
                                        }

                                        transferred = true;
                                    }
                                }
                            }
                            finally
                            {
                                if (!transferred)
                                {
                                    reply.Dispose();
                                }
                            }
                        }

                        ushort reason = child.Context!.TakeCloseReason();
                        if (reason != 0 && !child.TransportEnded && child.Context!.RequestedCloseReason == 0)
                        {
                            await CommandAsync(_ =>
                            {
                                if (!child.Retired)
                                {
                                    child.CloseReason = reason;
                                    child.RemoteClosed = true;
                                    child.ServerClosing = true;
                                    child.CloseDeadline = NetworkClock.GetMilliseconds() + 2000;
                                    CompleteInput(child);
                                }
                            }).ConfigureAwait(false);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (child.Lifetime.IsCancellationRequested)
            {
            }
            catch (ServerConnectionContext.ClosingException) when (child.Context?.RequestedCloseReason is > 0)
            {
                // The native owner completes the requested Close even if an old handler
                // attempts to send after its binding was revoked.
            }
            catch (Exception error)
            {
                RecordChildFailure(child, error, stage, opcode, correlationId);
            }
        }

        private void Retire(Child child, bool graceful = false)
        {
            if (child.CloseReason == 0)
            {
                child.CloseReason = child.Context?.RequestedCloseReason ?? 0;
            }

            // Retain ownership before cancellation invokes application registrations.
            retired.Add(child);
            if (!graceful)
            {
                child.Context?.EndTraffic();
            }

            lock (child.InputGate)
            {
                child.Retired = true;
                child.TransportEnded = true;
                child.InputCompleted = true;
            }

            try
            {
                if (!graceful)
                {
                    child.Lifetime.Cancel();
                }
            }
            catch (Exception error)
            {
                RecordChildCleanupFailure(child, error, "Cancel");
            }
            finally
            {
                child.InputSignal.Release();
                child.Outgoing.Writer.TryComplete();
                child.OutgoingUnreliable.Writer.TryComplete();
                while (child.Controls.TryDequeue(out OwnedFrame? frame))
                {
                    frame.Dispose();
                }

                child.Finalizing = Task.Run(() => FinalizeChildAsync(child));
            }
        }

        private async Task FinalizeChildAsync(Child child)
        {
            if (child.Dispatch != null)
            {
                await child.Dispatch.ConfigureAwait(false);
            }

            child.Context?.EndTraffic();
            try
            {
                child.Lifetime.Cancel();
            }
            catch (Exception error)
            {
                RecordChildCleanupFailure(child, error, "FinalCancel");
            }

            try
            {
                if (child.Context?.Session != null)
                {
                    await child.Context.Session.ClosedAsync(child.Context, new ServerCloseInfo(child.CloseReason, child.Failure)).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                RecordChildCleanupFailure(child, error, "Closed");
            }
        }

        private void ReapRetired()
        {
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                Child child = retired[i];
                if (child.Finalizing != null && !child.Finalizing.IsCompleted)
                {
                    continue;
                }

                while (child.Incoming.TryDequeue(out OwnedFrame? frame))
                {
                    frame.Dispose();
                }

                while (child.Outgoing.Reader.TryRead(out OwnedFrame? frame))
                {
                    frame.Dispose();
                }

                while (child.OutgoingUnreliable.Reader.TryRead(out OwnedFrame? frame))
                {
                    frame.Dispose();
                }

                while (child.IncomingUnreliable.TryDequeue(out OwnedFrame? frame))
                {
                    frame.Dispose();
                }

                child.InputSignal.Dispose();
                child.Lifetime.Dispose();
                child.Context?.Complete(new ServerCloseInfo(child.CloseReason, child.Failure));
                if (stopping && child.CleanupFailure != null)
                {
                    failure ??= child.CleanupFailure;
                }

                retired.RemoveAt(i);
            }
        }

        private void RequestStop(Child child)
        {
            lock (gate)
            {
                if (child.Retired || stopping)
                {
                    return;
                }

                child.StopRequested = true;
                wake.Set();
            }
        }

        private bool EnqueuePush(Child child, OwnedFrame frame)
        {
            lock (child.InputGate)
            {
                if (child.Retired || child.TransportEnded || child.InputCompleted || child.StopRequested)
                {
                    return false;
                }

                if (frame.Delivery == NetDelivery.Unreliable && child.UnreliableLimit == 0)
                {
                    throw new NotSupportedException("The peer did not negotiate unreliable messages.");
                }

                child.Lifetime.Token.ThrowIfCancellationRequested();
                frame.AdmittedAt = NetworkClock.GetMilliseconds();
                return (frame.Delivery == NetDelivery.Reliable ? child.Outgoing : child.OutgoingUnreliable).Writer.TryWrite(frame);
            }
        }

        private void FlushUdp(Socket socket, IntPtr listener, OwnedFrame output, ref int length, ref KcpNative.UdpMetadata metadata, long now, ref int remaining)
        {
            while (remaining > 0)
            {
                if (length == 0)
                {
                    length = KcpNative.ListenerPollUdp(listener, output.Bytes, output.Capacity, ref metadata);
                    KcpNative.Check(length, "listener UDP output");
                    if (length == 0)
                    {
                        return;
                    }
                }

                int nativeState = 0;
                bool stale = metadata.Id != 0 &&
                    (KcpNative.ConnectionState(listener, metadata.Id, metadata.Generation, out nativeState) != 0 || nativeState >= KcpNative.PeerClosed);
                if (stale || unchecked((uint)now - metadata.EnqueuedMs) > 250)
                {
                    if (!stale && metadata.Id != 0)
                    {
                        if (children.TryGetValue(metadata.Id, out Child? expired))
                        {
                            RecordChildFailure(expired, new NetworkBackpressureException("Listener UDP output age exceeded."), "UdpOutputDeadline");
                        }
                        KcpNative.Check(KcpNative.CloseConnection(listener, metadata.Id, metadata.Generation), "expire UDP output");
                    }

                    length = 0;
                    remaining--;
                    continue;
                }

                try
                {
                    int sent = socket.SendTo(output.Bytes, 0, length, SocketFlags.None, FromPeer(metadata.Peer));
                    if (sent != length)
                    {
                        throw new NetworkException("Listener UDP send did not accept the complete datagram.");
                    }

                    length = 0;
                    remaining--;
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }
            }
        }

        private static KcpNative.Peer ToPeer(IPEndPoint peer)
        {
            byte[] address = new byte[16];
            peer.Address.GetAddressBytes().CopyTo(address, 0);
            return new KcpNative.Peer
            {
                AddressFamily = peer.AddressFamily == AddressFamily.InterNetwork ? 4u : 6u,
                Address = address, Port = (ushort)peer.Port
            };
        }

        private static IPEndPoint FromPeer(KcpNative.Peer peer) => new IPEndPoint(
            new IPAddress(peer.Address.AsSpan(0, peer.AddressFamily == 4 ? 4 : 16)), peer.Port);

        public async ValueTask DisposeAsync()
        {
            lock (gate)
            {
                stopping = true;
                if (!stopped.Task.IsCompleted)
                {
                    wake.Set();
                }
            }

            await stopped.Task.ConfigureAwait(false);
        }
    }
}
