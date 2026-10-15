using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GameFrame.Tests
{
    internal sealed record KcpImpairmentPlan(int DelayMilliseconds, int JitterMilliseconds,
        int DropEvery, int ReorderEvery, int ReorderExtraMilliseconds)
    {
        internal static KcpImpairmentPlan For(string profile) => profile switch
        {
            "direct" => new(0, 0, 0, 0, 0),
            "loss" => new(0, 0, 20, 0, 0),
            "delay" => new(40, 0, 0, 0, 0),
            "jitter" => new(20, 40, 0, 0, 0),
            "reorder" => new(0, 0, 0, 5, 80),
            "mixed" => new(20, 20, 20, 5, 80),
            _ => throw new ArgumentException("Unknown network profile.", nameof(profile))
        };
    }

    // One deterministic stream per connection and direction; no per-packet history.
    internal sealed class KcpPacketSchedule
    {
        private readonly KcpImpairmentPlan plan;
        private readonly uint offset;
        private uint state;
        private long sequence;

        internal KcpPacketSchedule(KcpImpairmentPlan plan, uint seed)
        {
            this.plan = plan;
            offset = seed;
            state = seed;
        }

        internal (bool Drop, int Delay, bool Reorder, long Sequence) Next()
        {
            sequence++;
            long position = sequence + offset;
            bool drop = plan.DropEvery > 0 && position % plan.DropEvery == 0;
            bool reorder = !drop && plan.ReorderEvery > 0 && position % plan.ReorderEvery == 0;
            state = unchecked(state * 1664525 + 1013904223);
            int jitter = plan.JitterMilliseconds == 0 ? 0 : (int)(state % (uint)(plan.JitterMilliseconds + 1));
            return (drop, plan.DelayMilliseconds + jitter + (reorder ? plan.ReorderExtraMilliseconds : 0), reorder, sequence);
        }
    }

    internal sealed record KcpRelayReport
    {
        public KcpImpairmentPlan Plan { get; init; } = KcpImpairmentPlan.For("direct");
        public long ReceivedPackets { get; internal set; }
        public long BypassPackets { get; internal set; }
        public long ImpairedPackets { get; internal set; }
        public long ForwardedPackets { get; internal set; }
        public long DroppedPackets { get; internal set; }
        public long ClientToServerFaultDrops { get; internal set; }
        public long ServerToClientFaultDrops { get; internal set; }
        public long HeldPackets { get; internal set; }
        public long ReleasedHeldPackets { get; internal set; }
        public long DelayedPackets { get; internal set; }
        public long ReorderScheduledPackets { get; internal set; }
        public long OutOfOrderPackets { get; internal set; }
        public long QueueOverflowDrops { get; internal set; }
        public long DiscardedAtStop { get; internal set; }
        public int QueuePackets { get; internal set; }
        public int QueueBytes { get; internal set; }
        public int PeakQueuePackets { get; internal set; }
        public int PeakQueueBytes { get; internal set; }
        public int MaximumInjectedDelayMilliseconds { get; internal set; }
        public int? MinimumInjectedDelayMilliseconds { get; internal set; }
        public bool Stopped { get; internal set; }
        public bool SocketsDisposed { get; internal set; }
        public bool Released => Stopped && SocketsDisposed && QueuePackets == 0 && QueueBytes == 0;
        public bool AccountingValid => ReceivedPackets == ForwardedPackets + DroppedPackets + DiscardedAtStop + QueueOverflowDrops + QueuePackets;
        public bool FaultExercised => ClientToServerFaultDrops > 0 || ServerToClientFaultDrops > 0 || HeldPackets > 0;
        public bool ProfileExercised => ImpairedPackets > 0 &&
            (Plan.DropEvery == 0 || DroppedPackets > 0) &&
            (Plan.DelayMilliseconds == 0 || DelayedPackets > 0) &&
            (Plan.JitterMilliseconds == 0 || MinimumInjectedDelayMilliseconds < MaximumInjectedDelayMilliseconds) &&
            (Plan.ReorderEvery == 0 || OutOfOrderPackets > 0);
        public bool Healthy => Released && QueueOverflowDrops == 0 && AccountingValid &&
            (Plan == KcpImpairmentPlan.For("direct") ? FaultExercised : ProfileExercised);
    }

    // Test-only opaque UDP relay. Each client has a stable front/back socket pair,
    // preserving the listener's source endpoint binding and shared source-IP budget.
    internal sealed class KcpLoadRelay : IAsyncDisposable
    {
        internal const int PacketCapacity = 4096;
        internal const int ByteCapacity = 4 * 1024 * 1024;

        private sealed class Peer : IDisposable
        {
            internal readonly Socket Front = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            internal readonly Socket Back = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            internal readonly KcpPacketSchedule[] Schedules;
            internal readonly long[] LastForwarded = new long[2];
            internal readonly bool[] Blackhole = new bool[2];
            internal readonly bool[] Hold = new bool[2];
            internal readonly int[] DropNext = new int[2];
            internal EndPoint? Client;

            internal Peer(KcpImpairmentPlan plan, int seed, int index)
            {
                Schedules = new[]
                {
                    new KcpPacketSchedule(plan, unchecked((uint)seed + (uint)index * 2)),
                    new KcpPacketSchedule(plan, unchecked((uint)seed + (uint)index * 2 + 1))
                };
            }

            public void Dispose()
            {
                Front.Dispose();
                Back.Dispose();
            }
        }

        private sealed record Datagram(Peer Peer, int Direction, byte[] Bytes, long Sequence);
        private readonly List<Peer> peers = new List<Peer>();
        private readonly PriorityQueue<Datagram, (long Due, long Order)> queue = new();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly KcpRelayReport report;
        private readonly Task owner;
        private readonly object gate = new object();
        private readonly byte[] buffer = new byte[65535];
        private long order;
        private volatile bool impaired;

        internal KcpLoadRelay(IPEndPoint server, int connections, string profile, int seed)
        {
            KcpImpairmentPlan plan = KcpImpairmentPlan.For(profile);
            report = new KcpRelayReport { Plan = plan };
            try
            {
                for (int index = 0; index < connections; index++)
                {
                    var peer = new Peer(plan, seed, index);
                    peers.Add(peer);
                    peer.Front.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    peer.Back.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    peer.Back.Connect(server);
                    peer.Front.Blocking = false;
                    peer.Back.Blocking = false;
                }
            }
            catch
            {
                foreach (Peer peer in peers)
                {
                    peer.Dispose();
                }

                stop.Dispose();
                throw;
            }

            owner = Task.Run(RunAsync);
        }

        internal Uri Endpoint(int index) => new Uri($"udp://{peers[index].Front.LocalEndPoint}");
        internal void SetImpaired(bool value) => impaired = value;

        internal void SetBlackhole(int index, bool enabled, int direction = -1)
        {
            lock (gate)
            {
                for (int value = 0; value < 2; value++)
                {
                    if (direction == -1 || direction == value)
                    {
                        peers[index].Blackhole[value] = enabled;
                    }
                }
            }
        }

        internal void DropNext(int index, int direction, int count)
        {
            lock (gate)
            {
                peers[index].DropNext[direction] = count;
            }
        }

        internal void SetHold(int index, int direction, bool enabled)
        {
            lock (gate)
            {
                Peer peer = peers[index];
                peer.Hold[direction] = enabled;
                if (!enabled)
                {
                    var packets = queue.UnorderedItems.ToArray();
                    queue.Clear();
                    long now = Stopwatch.GetTimestamp();
                    foreach (var item in packets)
                    {
                        var priority = item.Priority;
                        if (item.Element.Peer == peer && item.Element.Direction == direction && priority.Due == long.MaxValue)
                        {
                            priority.Due = now;
                            report.ReleasedHeldPackets++;
                        }

                        queue.Enqueue(item.Element, priority);
                    }
                }
            }
        }

        internal KcpRelayReport Snapshot()
        {
            lock (gate)
            {
                return report with { };
            }
        }

        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    lock (gate)
                    {
                        foreach (Peer peer in peers)
                        {
                            Receive(peer, 0);
                            Receive(peer, 1);
                        }

                        long now = Stopwatch.GetTimestamp();
                        while (queue.TryPeek(out Datagram? packet, out var priority) && priority.Due <= now)
                        {
                            Send(packet);
                            queue.Dequeue();
                            report.QueuePackets--;
                            report.QueueBytes -= packet.Bytes.Length;
                        }
                    }

                    await Task.Delay(1, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Only this owner's stop signal ends its polling delay.
            }
            finally
            {
                lock (gate)
                {
                    report.DiscardedAtStop += queue.Count;
                    queue.Clear();
                    report.QueuePackets = 0;
                    report.QueueBytes = 0;
                    foreach (Peer peer in peers)
                    {
                        peer.Dispose();
                    }

                    report.SocketsDisposed = true;
                }
            }
        }

        private void Receive(Peer peer, int direction)
        {
            Socket socket = direction == 0 ? peer.Front : peer.Back;
            for (int count = 0; count < 32 && socket.Poll(0, SelectMode.SelectRead); count++)
            {
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                int size;
                try
                {
                    size = direction == 0 ? socket.ReceiveFrom(buffer, ref sender) : socket.Receive(buffer);
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }

                if (direction == 0)
                {
                    if (peer.Client != null && !peer.Client.Equals(sender))
                    {
                        throw new InvalidOperationException("Relay client endpoint changed.");
                    }

                    peer.Client = sender;
                }

                report.ReceivedPackets++;
                bool active = impaired;
                var decision = active ? peer.Schedules[direction].Next() : (Drop: false, Delay: 0, Reorder: false, Sequence: 0L);
                if (active)
                {
                    report.ImpairedPackets++;
                }
                else
                {
                    report.BypassPackets++;
                }

                bool forcedDrop = peer.Blackhole[direction] || peer.DropNext[direction] > 0;
                if (peer.DropNext[direction] > 0)
                {
                    peer.DropNext[direction]--;
                }

                if (forcedDrop)
                {
                    if (direction == 0)
                    {
                        report.ClientToServerFaultDrops++;
                    }
                    else
                    {
                        report.ServerToClientFaultDrops++;
                    }
                }

                if (decision.Drop || forcedDrop)
                {
                    report.DroppedPackets++;
                    continue;
                }

                if (queue.Count >= PacketCapacity || report.QueueBytes + size > ByteCapacity)
                {
                    report.QueueOverflowDrops++;
                    continue;
                }

                if (decision.Delay > 0)
                {
                    report.DelayedPackets++;
                }

                if (decision.Reorder)
                {
                    report.ReorderScheduledPackets++;
                }

                report.MaximumInjectedDelayMilliseconds = Math.Max(report.MaximumInjectedDelayMilliseconds, decision.Delay);
                if (active)
                {
                    report.MinimumInjectedDelayMilliseconds = Math.Min(report.MinimumInjectedDelayMilliseconds ?? decision.Delay, decision.Delay);
                }
                long due = Stopwatch.GetTimestamp() + (long)(decision.Delay / 1000.0 * Stopwatch.Frequency);
                if (peer.Hold[direction])
                {
                    due = long.MaxValue;
                    report.HeldPackets++;
                }

                queue.Enqueue(new Datagram(peer, direction, buffer.AsSpan(0, size).ToArray(), decision.Sequence), (due, order++));
                report.QueuePackets++;
                report.QueueBytes += size;
                report.PeakQueuePackets = Math.Max(report.PeakQueuePackets, report.QueuePackets);
                report.PeakQueueBytes = Math.Max(report.PeakQueueBytes, report.QueueBytes);
            }
        }

        private void Send(Datagram packet)
        {
            int sent = packet.Direction == 0 ? packet.Peer.Back.Send(packet.Bytes) :
                packet.Peer.Front.SendTo(packet.Bytes, packet.Peer.Client!);
            if (sent != packet.Bytes.Length)
            {
                throw new IOException("Relay UDP send was incomplete.");
            }

            report.ForwardedPackets++;
            if (packet.Sequence > 0)
            {
                ref long last = ref packet.Peer.LastForwarded[packet.Direction];
                if (packet.Sequence < last)
                {
                    report.OutOfOrderPackets++;
                }

                last = Math.Max(last, packet.Sequence);
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try
            {
                await owner;
                lock (gate)
                {
                    report.Stopped = true;
                }
            }
            finally
            {
                stop.Dispose();
            }
        }
    }
}
