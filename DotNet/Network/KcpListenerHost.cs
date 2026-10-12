using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Network.Transport.Kcp;

namespace GameFrame.Server.Network
{
    internal readonly struct KcpConnectionInfo
    {
        internal ulong Id { get; }
        internal ulong Generation { get; }
        internal ulong GrantId { get; }

        internal KcpConnectionInfo(ulong id, ulong generation, ulong grantId)
        {
            Id = id;
            Generation = generation;
            GrantId = grantId;
        }
    }

    // The listener owns the socket and every child native handle. Application handlers never call native.
    internal sealed class KcpListenerHost : IAsyncDisposable
    {
        // Local listener policy. Ports share one source budget; a full table rejects new sources.
        internal sealed class SourceBudget
        {
            private readonly Dictionary<IPAddress, (int Packets, int Bytes)> sources =
                new Dictionary<IPAddress, (int Packets, int Bytes)>(1024);
            private long window = -1;

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
                if ((!exists && sources.Count >= 1024) || usage.Packets >= 2048 || bytes > 2 * 1024 * 1024 - usage.Bytes)
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
            internal readonly Channel<OwnedFrame> Outgoing = Channel.CreateBounded<OwnedFrame>(128);
            internal readonly Channel<OwnedFrame> OutgoingUnreliable = Channel.CreateBounded<OwnedFrame>(64);
            internal readonly Queue<OwnedFrame> Controls = new Queue<OwnedFrame>();
            internal required FrameMemoryBudget Memory;
            internal required FrameMemoryBudget SendMemory;
            internal required FrameMemoryBudget ReceiveMemory;
            internal required FrameMemoryBudget ControlMemory;
            internal Task? Dispatch;
            internal volatile Exception? Failure;
            internal bool Bound, RemoteClosed;
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
        private readonly Func<KcpConnectionInfo, OwnedFrame, FrameMemoryBudget, CancellationToken, Task<OwnedFrame?>> handler;
        private readonly FrameMemoryBudget memory = new FrameMemoryBudget(64 * 1024 * 1024);
        private readonly ConcurrentQueue<Action<IntPtr>> commands = new ConcurrentQueue<Action<IntPtr>>();
        private readonly Dictionary<ulong, Child> children = new Dictionary<ulong, Child>();
        private readonly List<Child> retired = new List<Child>();
        private readonly Queue<Child> releaseReady = new Queue<Child>();
        private readonly SourceBudget sourceBudget = new SourceBudget();
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
        internal IPEndPoint? LocalEndpoint { get; private set; }
        internal long OwnedBytes => memory.OwnedBytes;
        internal Task Started => started.Task;
        internal Task Stopped => stopped.Task;

        internal KcpListenerHost(IPEndPoint endpoint, NetProtocolCatalog catalog,
            Func<KcpConnectionInfo, OwnedFrame, FrameMemoryBudget, CancellationToken, Task<OwnedFrame?>> handler, ILogger? logger = null)
        {
            this.endpoint = endpoint;
            this.catalog = catalog;
            this.handler = handler;
            this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            thread = new Thread(Run) { IsBackground = true, Name = "GameFrame KCP Listener" };
            thread.Start();
        }

        private static TaskCompletionSource<T> Source<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task<ulong> RegisterGrantAsync(uint conv, byte[] identity, byte[] psk, byte[] ticket, int ttlMs = 30000)
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

        internal Task RevokeGrantAsync(ulong id) => CommandAsync(listener => KcpNative.Check(KcpNative.RevokeGrant(listener, id), "revoke grant"));

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

                if (commandCount >= 256)
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
            try
            {
                KcpNative.ValidateAbi();
                var config = new KcpNative.ListenerConfig
                {
                    Size = 32, Version = KcpNative.Version, NativeLimit = 256 * 1024 * 1024, ConnectionLimit = 4 * 1024 * 1024,
                    MaxConnections = 64, MaxHandshakes = 16, MaxGrants = 256
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
                LocalEndpoint = (IPEndPoint)socket.LocalEndPoint!;
                started.TrySetResult(true);
                EndPoint source = new IPEndPoint(endpoint.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
                while (!Volatile.Read(ref stopping))
                {
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
                            child.Failure = error;
                            logger.LogWarning(error, "KCP connection {ConnectionId}/{Generation} failed.", child.Event.Id, child.Event.Generation);
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
                        Retire(completed, completed.Failure == null);
                        KcpNative.Check(KcpNative.ReleaseConnection(listener, completed.Event.Id, completed.Event.Generation), "release drained child");
                    }
                    PollEvents(listener);
                    FlushUdp(socket, listener, output, ref outputLength, ref metadata, now, ref outputBudget);
                    ReapRetired();
                    wake.WaitOne(10);
                }
            }
            catch (Exception error)
            {
                failure = error;
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
                Cleanup(() => Task.WhenAll(retired.Select(child => child.Dispatch ?? Task.CompletedTask)).GetAwaiter().GetResult());
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
                if (failure == null)
                {
                    failure = error;
                }
                else
                {
                    logger.LogError(error, "KCP listener cleanup failed.");
                }
            }
        }

        private void PollEvents(IntPtr listener)
        {
            var connectionEvent = new KcpNative.ConnectionEvent { Size = 72, Version = KcpNative.Version };
            int result;
            while ((result = KcpNative.ListenerPollEvent(listener, ref connectionEvent)) == 1)
            {
                if (connectionEvent.Kind == 1)
                {
                    if (children.Count + retired.Count >= 64)
                    {
                        KcpNative.CloseConnection(listener, connectionEvent.Id, connectionEvent.Generation);
                        continue;
                    }
                    var childMemory = new FrameMemoryBudget(8 * 1024 * 1024, memory);
                    children.Add(connectionEvent.Id, new Child
                    {
                        Event = connectionEvent, Memory = childMemory,
                        SendMemory = new FrameMemoryBudget(64 * 1024, childMemory),
                        ReceiveMemory = new FrameMemoryBudget(2 * 1024 * 1024, childMemory),
                        ControlMemory = new FrameMemoryBudget(16 * 1024, childMemory),
                        UnreliableMemory = new FrameMemoryBudget(64 * 1024, childMemory),
                        LastReceived = NetworkClock.GetMilliseconds(), PingAt = NetworkClock.GetMilliseconds()
                    });
                }
                else
                {
                    if (connectionEvent.Reason == 0 && children.TryGetValue(connectionEvent.Id, out Child? ended) &&
                        ended.Bound && ended.Failure == null)
                    {
                        ended.TransportEnded = true;
                        continue;
                    }
                    if (children.Remove(connectionEvent.Id, out Child? child))
                    {
                        Retire(child);
                    }

                    KcpNative.Check(KcpNative.ReleaseConnection(listener, connectionEvent.Id, connectionEvent.Generation), "release child");
                }
            }

            KcpNative.Check(result, "listener events");
        }

        private void DriveChild(IntPtr listener, Child child, OwnedFrame header, OwnedFrame scratch, long now, bool writable)
        {
            if (child.Failure != null)
            {
                throw new NetworkException("Application handler failed.", child.Failure);
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
                if (length != 32 + decoded.BodyLength)
                {
                    throw new NetworkProtocolException("Child frame length mismatch.");
                }

                if (child.RateWindow != now / 1000)
                {
                    child.RateWindow = now / 1000;
                    child.Packets = child.Bytes = 0;
                }

                if (++child.Packets > 128 || (child.Bytes += length) > 128 * 1024)
                {
                    throw new NetworkProtocolException("Authenticated input rate exceeded local policy.");
                }

                if (delivery == NetDelivery.Unreliable)
                {
                    lock (child.InputGate)
                    {
                        while (child.IncomingUnreliable.Count != 0 && (child.IncomingUnreliable.Count >= 64 ||
                            length + FrameMemoryBudget.FrameRecordReservation > 64 * 1024 - child.UnreliableMemory.OwnedBytes))
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
                        lock (child.InputGate)
                        {
                            Queue<OwnedFrame> queue = delivery == NetDelivery.Reliable ? child.Incoming : child.IncomingUnreliable;
                            if (queue.Count >= (delivery == NetDelivery.Reliable ? 128 : 64))
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

            if (child.Bound && !child.RemoteClosed && now - child.PingAt >= 5000 && child.Nonce == 0)
            {
                child.PingAt = now;
                child.Nonce = checked(child.NonceSequence + 1);
                child.NonceSequence = child.Nonce;
                child.PongDeadline = 0;
                QueueNonce(child, ControlOpcode.Ping, child.Nonce);
            }

            if (child.PongDeadline != 0 && now >= child.PongDeadline)
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
                else if (child.Outgoing.Reader.TryPeek(out OwnedFrame? reliable) && TrySendQueued(child, reliable, now))
                {
                    child.Outgoing.Reader.TryRead(out _);
                }
                else if (child.OutgoingUnreliable.Reader.TryPeek(out OwnedFrame? unreliable) && TrySendQueued(child, unreliable, now))
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
                var stats = new KcpNative.Stats { Size = 168, Version = KcpNative.Version };
                KcpNative.Check(KcpNative.GetStats(child.Event.Connection, ref stats), "closing child stats");
                if (now >= child.CloseDeadline || ((child.Dispatch == null || child.Dispatch.IsCompleted) &&
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
                OwnedFrame reply = child.ControlMemory.Allocate(96);
                try
                {
                    byte[] session = RandomNumberGenerator.GetBytes(16);
                    ControlCodec.WriteServerHello(reply.Bytes.AsSpan(32), catalog.Hash, session, hello.ReliableLimit,
                        hello.UnreliableLimit, hello.Features, NetworkTransportKind.Kcp);
                    reply.Header = new FrameHeader(FrameKind.Control, 2, 0, 64);
                    reply.Length = 96;
                    FrameCodec.WriteHeader(reply.Bytes, reply.Header);
                    QueueControl(child, reply);
                }
                catch
                {
                    reply.Dispose();
                    throw;
                }

                child.Dispatch = DispatchAsync(child);
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
                ControlCodec.ReadClose(frame.Body);
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

        private static void QueueNonce(Child child, ControlOpcode opcode, ulong nonce)
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

        private static void QueueControl(Child child, OwnedFrame frame)
        {
            if (child.Controls.Count >= 64)
            {
                throw new NetworkBackpressureException("Child control queue full.");
            }

            frame.AdmittedAt = NetworkClock.GetMilliseconds();
            child.Controls.Enqueue(frame);
        }

        private async Task DispatchAsync(Child child)
        {
            try
            {
                int reliableCount = 0;
                while (true)
                {
                    await child.InputSignal.WaitAsync(child.Lifetime.Token).ConfigureAwait(false);
                    OwnedFrame? frame;
                    lock (child.InputGate)
                    {
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

                        OwnedFrame? reply = await handler(new KcpConnectionInfo(child.Event.Id, child.Event.Generation, child.Event.GrantId),
                            frame, child.SendMemory, child.Lifetime.Token).ConfigureAwait(false);
                        if (reply == null)
                        {
                            continue;
                        }

                        bool transferred = false;
                        try
                        {
                            if (child.TransportEnded)
                            {
                                continue;
                            }
                            child.Lifetime.Token.ThrowIfCancellationRequested();
                            FrameCodec.ReadFrame(reply.Bytes.AsSpan(0, reply.Length), reply.Delivery == NetDelivery.Reliable ?
                                child.ReliableLimit : child.UnreliableLimit, reply.Delivery, FrameDirection.ServerToClient, false);
                            NetRoute route = catalog.Find(reply.Header.Opcode);
                            if (route.Delivery != reply.Delivery || (reply.Header.Kind == FrameKind.Response ?
                                route.Kind != NetRouteKind.Rpc || reply.Header.BodyLength > route.ResponseMaxBodyBytes :
                                route.Kind != NetRouteKind.Push || reply.Header.BodyLength > route.MaxBodyBytes))
                            {
                                throw new NetworkProtocolException("Application reply does not match its registered route.");
                            }

                            reply.AdmittedAt = NetworkClock.GetMilliseconds();
                            if (!(reply.Delivery == NetDelivery.Reliable ? child.Outgoing : child.OutgoingUnreliable).Writer.TryWrite(reply))
                            {
                                throw new NetworkBackpressureException("Child business send queue full.");
                            }

                            transferred = true;
                        }
                        finally
                        {
                            if (!transferred)
                            {
                                reply.Dispose();
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (child.Lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                child.Failure = error;
            }
        }

        private void Retire(Child child, bool graceful = false)
        {
            // Retain ownership before cancellation invokes application registrations.
            retired.Add(child);
            try
            {
                if (!graceful)
                {
                    child.Lifetime.Cancel();
                }
            }
            finally
            {
                child.InputCompleted = true;
                child.Outgoing.Writer.TryComplete();
                child.OutgoingUnreliable.Writer.TryComplete();
                while (child.Controls.TryDequeue(out OwnedFrame? frame))
                {
                    frame.Dispose();
                }
            }
        }

        private void ReapRetired()
        {
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                Child child = retired[i];
                if (child.Dispatch != null && !child.Dispatch.IsCompleted)
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
                retired.RemoveAt(i);
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
                            expired.Failure = new NetworkBackpressureException("Listener UDP output age exceeded.");
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
