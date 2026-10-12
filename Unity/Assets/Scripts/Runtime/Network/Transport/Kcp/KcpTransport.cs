#nullable disable
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GameFrame.Network.Protocol;

namespace GameFrame.Network.Transport.Kcp
{
    internal sealed class KcpTransport : IFrameTransport
    {
        private sealed class SendOperation
        {
            internal OwnedFrame Frame;
            internal CancellationToken Token;
            internal Func<bool> CanSend;
            internal bool SingleAttempt;
            internal TaskCompletionSource<bool> Source = NewSource<bool>();
        }

        private sealed class ReceiveOperation
        {
            internal FrameMemoryBudget Budget, UnreliableBudget;
            internal int ReliableLimit, UnreliableLimit;
            internal FrameDirection Direction;
            internal bool Handshaking;
            internal CancellationToken Token;
            internal Action<FrameHeader, NetDelivery> Validate;
            internal TaskCompletionSource<OwnedFrame> Source = NewSource<OwnedFrame>();
        }

        private readonly object gate = new object();
        private readonly KcpWorker worker;
        private readonly NetworkConnectOptions connect;
        private readonly NetworkClientOptions options;
        private readonly FrameMemoryBudget memory;
        private readonly Action checkClock;
        private readonly TaskCompletionSource<bool> connected = NewSource<bool>();
        private readonly TaskCompletionSource<bool> stopped = NewSource<bool>();
        private IPEndPoint endpoint;
        private CancellationToken connectToken;
        private IntPtr handle;
        private Socket socket;
        private OwnedFrame input, output, header;
        private int outputLength;
        private uint outputBorn;
        private long lastStep, deadline, tokenTime;
        private double businessTokens, unreliableTokens;
        private double admissionTokens;
        private long admissionTokenTime;
        private SendOperation send;
        private ReceiveOperation receive;
        private TaskCompletionSource<bool> closeOutput;
        private CancellationToken closeToken;
        private bool abort, added, established, nativeReserved;
        private Exception terminal;
        private KcpNative.Stats stats;
        private long wireSentBytes, wireSentPackets, managedUnreliableDropped;
        private KcpMetricsSnapshot metrics;
        private int socketReceiveBuffer, socketSendBuffer;
        internal KcpMetricsSnapshot GetMetrics()
        {
            lock (gate)
            {
                return metrics;
            }
        }

        internal double EstimateReliableWaitMilliseconds(long queuedBytes, int length, long now)
        {
            lock (gate)
            {
                double available = Math.Min(options.BusinessBurstBytes, admissionTokens +
                    Math.Max(0, now - admissionTokenTime) * (double)options.BusinessBytesPerSecond / 1000);
                double tokenDeficit = Math.Max(0, queuedBytes + length - available);
                long segments = (length + 1125L) / 1126;
                // Native-accepted bytes have already spent tokens. Estimate only segment pressure
                // beyond the admission window; do not charge all in-flight bytes a second time.
                double windowDeficit = Math.Max(0, stats.Waitsnd + segments - 512) * 1126;
                return Math.Max(tokenDeficit, windowDeficit) * 1000 / options.BusinessBytesPerSecond;
            }
        }
        public bool OwnsClock => true;
        public Task StoppedTask => stopped.Task;

        internal KcpTransport(KcpWorker worker, NetworkConnectOptions connect, NetworkClientOptions options,
            FrameMemoryBudget memory, Action checkClock)
        {
            this.worker = worker;
            this.connect = connect;
            this.options = options;
            this.memory = memory;
            this.checkClock = checkClock;
        }

        private static TaskCompletionSource<T> NewSource<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConnectAsync(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IPAddress address;
            if (!IPAddress.TryParse(uri.Host, out address))
            {
                Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(uri.DnsSafeHost);
                var canceled = NewSource<bool>();
                using (token.Register(() => canceled.TrySetCanceled(token)))
                {
                    await await Task.WhenAny(lookup, canceled.Task).ConfigureAwait(false);
                }

                address = lookup.Result.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork ||
                    (ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv6LinkLocal));
            }

            if (address == null || address.IsIPv6LinkLocal)
            {
                throw new NetworkException("UDP endpoint has no supported IP address.");
            }

            lock (gate)
            {
                if (abort)
                {
                    throw terminal ?? new NetworkException("KCP transport closed before connect.");
                }

                endpoint = new IPEndPoint(address, uri.Port);
                connectToken = token;
                deadline = Math.Min(connect.GrantDeadlineMilliseconds, checked(NetworkClock.GetMilliseconds() + 10000));
                worker.Add(this);
                added = true;
            }

            await connected.Task.ConfigureAwait(false);
        }

        public Task SendAsync(OwnedFrame frame, CancellationToken token, Func<bool> canSend = null)
        {
            return QueueSend(frame, token, canSend, false);
        }

        internal Task<bool> TrySendAsync(OwnedFrame frame, CancellationToken token, Func<bool> canSend)
        {
            return QueueSend(frame, token, canSend, true);
        }

        private Task<bool> QueueSend(OwnedFrame frame, CancellationToken token, Func<bool> canSend, bool singleAttempt)
        {
            lock (gate)
            {
                RequireRunning();
                if (send != null || closeOutput != null)
                {
                    throw new InvalidOperationException("KCP requires one send owner.");
                }

                send = new SendOperation { Frame = frame, Token = token, CanSend = canSend, SingleAttempt = singleAttempt };
                worker.Wake();
                return send.Source.Task;
            }
        }

        public Task<OwnedFrame> ReceiveAsync(FrameMemoryBudget budget, int reliableLimit, int unreliableLimit,
            FrameDirection direction, bool handshaking, CancellationToken token, Action<FrameHeader, NetDelivery> validateHeader, FrameMemoryBudget unreliableBudget = null)
        {
            lock (gate)
            {
                RequireRunning();
                if (receive != null)
                {
                    throw new InvalidOperationException("KCP requires one receive owner.");
                }

                receive = new ReceiveOperation
                {
                    Budget = budget, UnreliableBudget = unreliableBudget ?? budget, ReliableLimit = reliableLimit, UnreliableLimit = unreliableLimit,
                    Direction = direction, Handshaking = handshaking, Token = token, Validate = validateHeader
                };
                worker.Wake();
                return receive.Source.Task;
            }
        }

        public Task CloseOutputAsync(CancellationToken token)
        {
            lock (gate)
            {
                RequireRunning();
                if (send != null)
                {
                    throw new InvalidOperationException("CloseOutput requires an idle send owner.");
                }

                closeOutput = closeOutput ?? NewSource<bool>();
                closeToken = token;
                worker.Wake();
                return closeOutput.Task;
            }
        }

        private void RequireRunning()
        {
            if (abort || stopped.Task.IsCompleted)
            {
                throw terminal ?? new NetworkException("KCP transport stopped.");
            }
        }

        public void Abort()
        {
            lock (gate)
            {
                abort = true;
                if (!added)
                {
                    stopped.TrySetResult(true);
                }
            }

            worker.Wake();
        }

        public void Dispose() => Abort();

        internal void Step(long now)
        {
            try
            {
                bool stop;
                lock (gate)
                {
                    stop = abort;
                }

                if (stop)
                {
                    StopOnOwner(new NetworkException("KCP transport aborted."));
                    return;
                }

                if (lastStep != 0 && (now < lastStep || now - lastStep >= int.MaxValue))
                {
                    throw new NetworkException("KCP clock gap exceeded the native clock range.");
                }

                lastStep = now;
                if (!established)
                {
                    connectToken.ThrowIfCancellationRequested();
                    if (now >= deadline)
                    {
                        throw new NetworkException("KCP handshake/grant deadline elapsed.");
                    }
                }

                if (handle == IntPtr.Zero)
                {
                    KcpNative.ValidateAbi();
                    memory.Reserve(options.NativeBytes);
                    nativeReserved = true; // reservation now belongs to the owner cleanup path
                    input = memory.Allocate(1201);
                    output = memory.Allocate(1200);
                    header = memory.Allocate(32);
                    handle = KcpNative.Create(connect, options.NativeBytes);
                    socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
                    socket.ReceiveBufferSize = 256 * 1024;
                    socket.SendBufferSize = 256 * 1024;
                    socket.Connect(endpoint);
                    socketReceiveBuffer = socket.ReceiveBufferSize;
                    socketSendBuffer = socket.SendBufferSize;
                    KcpNative.Check(KcpNative.Tick(handle, (uint)now), "initial tick");
                    tokenTime = now;
                    businessTokens = options.BusinessBurstBytes;
                    unreliableTokens = options.UnreliableBurstBytes;
                    lock (gate)
                    {
                        admissionTokens = businessTokens;
                        admissionTokenTime = now;
                    }
                }

                if (stats.State == KcpNative.PeerClosed)
                {
                    ProcessReceive(now);
                    return;
                }

                int outputBudget = 256;
                FlushOutput(now, ref outputBudget);
                for (int i = 0; i < 64; i++)
                {
                    int length;
                    try
                    {
                        length = socket.Receive(input.Bytes, 0, input.Capacity, SocketFlags.None);
                    }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
                    {
                        break;
                    }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize)
                    {
                        continue;
                    }

                    if (length == 0 || length > 1200)
                    {
                        continue;
                    }

                    int result = KcpNative.Feed(handle, input.Bytes, length);
                    if (result != KcpNative.Backpressure)
                    {
                        KcpNative.Check(result, "feed UDP");
                    }
                }

                KcpNative.Check(KcpNative.Tick(handle, (uint)now), "tick");
                stats = new KcpNative.Stats { Size = 168, Version = KcpNative.Version };
                KcpNative.Check(KcpNative.GetStats(handle, ref stats), "stats");
                if (!established && stats.State == KcpNative.Established)
                {
                    established = true;
                    connected.TrySetResult(true);
                }

                if (established)
                {
                    ProcessReceive(now);
                    if (stats.State == KcpNative.Established)
                    {
                        checkClock?.Invoke();
                        ProcessSend(now);
                    }
                }

                if (stats.State != KcpNative.PeerClosed)
                {
                    FlushOutput(now, ref outputBudget);
                }
                lock (gate)
                {
                    admissionTokens = businessTokens;
                    admissionTokenTime = tokenTime;
                    metrics = new KcpMetricsSnapshot(stats, nativeReserved ? options.NativeBytes : 0, wireSentBytes,
                        wireSentPackets, socketReceiveBuffer, socketSendBuffer, managedUnreliableDropped);
                    if (closeOutput != null && (closeToken.IsCancellationRequested ||
                        (stats.Waitsnd == 0 && stats.PlaintextCount == 0 && stats.OutputCount == 0 && outputLength == 0)))
                    {
                        if (closeToken.IsCancellationRequested)
                        {
                            closeOutput.TrySetCanceled(closeToken);
                        }
                        else
                        {
                            closeOutput.TrySetResult(true);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                StopOnOwner(error);
            }
        }

        private void ProcessSend(long now)
        {
            SendOperation operation;
            lock (gate)
            {
                operation = send;
            }

            if (operation == null)
            {
                return;
            }

            operation.Token.ThrowIfCancellationRequested();
            if (operation.CanSend != null && !operation.CanSend())
            {
                CompleteSend(operation);
                return;
            }

            bool control = operation.Frame.Header.Kind == FrameKind.Control;
            long age = now - operation.Frame.AdmittedAt;
            if (operation.Frame.AdmittedAt != 0 && age >= (control ? 15000 : options.MaxSendQueueAge.TotalMilliseconds))
            {
                if (IsUnreliableFrame(operation))
                {
                    CompleteSend(operation);
                    return;
                }

                throw new NetworkBackpressureException("KCP send queue age exceeded.");
            }

            bool unreliable = operation.Frame.Delivery == NetDelivery.Unreliable;
            long elapsed = now - tokenTime;
            tokenTime = now;
            businessTokens = Math.Min(options.BusinessBurstBytes, businessTokens + elapsed * (double)options.BusinessBytesPerSecond / 1000);
            unreliableTokens = Math.Min(options.UnreliableBurstBytes, unreliableTokens + elapsed * (double)options.UnreliableBytesPerSecond / 1000);
            if (outputLength != 0 || (!control && (businessTokens < operation.Frame.Length ||
                (unreliable && unreliableTokens < operation.Frame.Length))))
            {
                if (operation.SingleAttempt)
                {
                    CompleteSend(operation, false);
                }
                return;
            }

            int result = unreliable ? KcpNative.SendUnreliable(handle, operation.Frame.Bytes, operation.Frame.Length) :
                KcpNative.SendReliable(handle, operation.Frame.Bytes, operation.Frame.Length);
            if (result == KcpNative.Backpressure)
            {
                if (operation.SingleAttempt)
                {
                    CompleteSend(operation, false);
                }
                return;
            }

            KcpNative.Check(result, "send frame");
            if (!control)
            {
                businessTokens -= operation.Frame.Length;
                if (unreliable)
                {
                    unreliableTokens -= operation.Frame.Length;
                }
            }

            operation.Frame.AcceptedByTransport = true;
            CompleteSend(operation);
        }

        private static bool IsUnreliableFrame(SendOperation operation) => operation.Frame.Delivery == NetDelivery.Unreliable;

        private void CompleteSend(SendOperation operation, bool completed = true)
        {
            lock (gate)
            {
                send = null;
                operation.Source.TrySetResult(completed);
            }
        }

        private void ProcessReceive(long now)
        {
            ReceiveOperation operation;
            lock (gate)
            {
                operation = receive;
            }

            if (operation == null)
            {
                return;
            }

            operation.Token.ThrowIfCancellationRequested();
            int result = KcpNative.Peek(handle, out int channel, out int length, out uint born);
            KcpNative.Check(result, "peek frame");
            if (result == 0)
            {
                if (stats.State == KcpNative.PeerClosed)
                {
                    lock (gate)
                    {
                        receive = null;
                        operation.Source.TrySetResult(null);
                    }
                }

                return;
            }

            NetDelivery delivery = channel == 1 ? NetDelivery.Reliable : NetDelivery.Unreliable;
            long receivedAt = now - unchecked((uint)now - born);
            if (delivery == NetDelivery.Unreliable && now - receivedAt >= options.UnreliableMaxAge.TotalMilliseconds)
            {
                if (KcpNative.Receive(handle, input.Bytes, input.Capacity) != length)
                {
                    throw new NetworkProtocolException("Expired unreliable frame length mismatch.");
                }
                managedUnreliableDropped++;
                return;
            }
            int limit = delivery == NetDelivery.Reliable ? operation.ReliableLimit : operation.UnreliableLimit;
            KcpNative.Check(KcpNative.PeekHeader(handle, header.Bytes, header.Capacity), "peek header");
            FrameHeader decoded = FrameCodec.ReadHeader(header.Bytes, limit, delivery, operation.Direction, operation.Handshaking);
            operation.Validate?.Invoke(decoded, delivery);
            if (length != FrameCodec.HeaderBytes + decoded.BodyLength)
            {
                throw new NetworkProtocolException("KCP message length disagrees with the GFNP header.");
            }

            OwnedFrame frame;
            try
            {
                frame = (delivery == NetDelivery.Reliable ? operation.Budget : operation.UnreliableBudget).Allocate(length);
            }
            catch (NetworkBackpressureException) when (delivery == NetDelivery.Unreliable)
            {
                // The main thread can still own the oldest dispatched lease. Consume this lossy frame
                // using the UDP input scratch buffer rather than faulting or allocating past the quota.
                int discarded = KcpNative.Receive(handle, input.Bytes, input.Capacity);
                KcpNative.Check(discarded, "discard unreliable frame");
                if (discarded != length)
                {
                    throw new NetworkProtocolException("Discarded KCP frame length mismatch.");
                }

                managedUnreliableDropped++;
                return;
            }
            try
            {
                int actual = KcpNative.Receive(handle, frame.Bytes, frame.Capacity);
                KcpNative.Check(actual, "receive frame");
                if (actual != length)
                {
                    throw new NetworkProtocolException("KCP receive returned an inconsistent frame length.");
                }

                frame.Header = decoded;
                frame.Length = length;
                frame.Delivery = delivery;
                frame.AdmittedAt = receivedAt;
                lock (gate)
                {
                    receive = null;
                    operation.Source.TrySetResult(frame);
                }
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        private void FlushOutput(long now, ref int remaining)
        {
            while (remaining > 0)
            {
                if (outputLength == 0)
                {
                    outputLength = KcpNative.PollUdp(handle, output.Bytes, output.Capacity, out outputBorn);
                    KcpNative.Check(outputLength, "poll UDP");
                    if (outputLength == 0)
                    {
                        return;
                    }
                }

                if (unchecked((uint)now - outputBorn) > 250)
                {
                    throw new NetworkException("KCP socket output age exceeded.");
                }

                try
                {
                    int length = socket.Send(output.Bytes, 0, outputLength, SocketFlags.None);
                    if (length != outputLength)
                    {
                        throw new NetworkException("UDP send did not accept the complete datagram.");
                    }

                    wireSentBytes += length;
                    wireSentPackets++;
                    remaining--;
                    outputLength = 0;
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }
            }
        }

        internal void StopOnOwner(Exception error)
        {
            if (stopped.Task.IsCompleted)
            {
                return;
            }

            terminal = error;
            Exception cleanupFailure = null;
            bool nativeDestroyed = handle == IntPtr.Zero;
            try
            {
                Socket ownedSocket = socket;
                socket = null;
                try
                {
                    ownedSocket?.Dispose();
                }
                catch (Exception cleanupError)
                {
                    cleanupFailure = cleanupError;
                }

                if (handle != IntPtr.Zero)
                {
                    IntPtr ownedHandle = handle;
                    handle = IntPtr.Zero;
                    try
                    {
                        var finalStats = new KcpNative.Stats { Size = 168, Version = KcpNative.Version };
                        KcpNative.Check(KcpNative.GetStats(ownedHandle, ref finalStats), "final stats");
                        stats = finalStats;
                    }
                    catch (Exception cleanupError)
                    {
                        cleanupFailure = cleanupFailure ?? cleanupError;
                    }

                    try
                    {
                        KcpNative.Check(KcpNative.Destroy(ownedHandle), "destroy");
                        nativeDestroyed = true;
                    }
                    catch (Exception cleanupError)
                    {
                        cleanupFailure = cleanupFailure ?? cleanupError;
                    }
                }
            }
            finally
            {
                input?.Dispose();
                output?.Dispose();
                header?.Dispose();
                if (nativeReserved && nativeDestroyed)
                {
                    memory.Release(options.NativeBytes);
                    nativeReserved = false;
                }

                lock (gate)
                {
                    abort = true;
                    if (nativeDestroyed)
                    {
                        stats.OwnedBytes = 0;
                        stats.PendingReliableBytes = stats.InflightReliableBytes = 0;
                        stats.InputCount = stats.OutputCount = stats.OutputBytes = stats.PlaintextCount = stats.PlaintextBytes = 0;
                    }

                    metrics = new KcpMetricsSnapshot(stats, nativeReserved ? options.NativeBytes : 0, wireSentBytes,
                        wireSentPackets, socketReceiveBuffer, socketSendBuffer, managedUnreliableDropped);
                    connected.TrySetException(error);
                    if (send != null)
                    {
                        if (send.Token.IsCancellationRequested)
                        {
                            send.Source.TrySetCanceled(send.Token);
                        }
                        else
                        {
                            send.Source.TrySetException(error);
                        }
                    }
                    if (receive != null)
                    {
                        if (receive.Token.IsCancellationRequested)
                        {
                            receive.Source.TrySetCanceled(receive.Token);
                        }
                        else
                        {
                            receive.Source.TrySetException(error);
                        }
                    }
                    closeOutput?.TrySetException(error);
                    send = null;
                    receive = null;
                    if (cleanupFailure == null)
                    {
                        stopped.TrySetResult(true);
                    }
                    else
                    {
                        stopped.TrySetException(cleanupFailure);
                    }
                }
            }
        }
    }
}
