using System.Diagnostics.Metrics;
using GameFrame.Network;

namespace GameFrame.Tests
{
    internal sealed class NetworkLoadBytes
    {
        private readonly LoadLatency histogram = new LoadLatency();
        public long Count => histogram.Count;
        public double? AverageBytes => histogram.AverageMs * 1000;
        public double MaximumBytes => histogram.MaximumMs * 1000;
        public double? P95UpperBytes => histogram.P95UpperMs * 1000;
        internal void Record(long bytes) => histogram.Record(bytes / 1000.0);
    }

    internal sealed class NetworkKcpSample
    {
        private readonly KcpMetricsSnapshot[] baseline;
        public LoadLatency ServerOwnerWork { get; } = new LoadLatency();
        public LoadLatency ServerOwnerInterval { get; } = new LoadLatency();
        public NetworkLoadBytes ServerOwnerAllocated { get; } = new NetworkLoadBytes();
        public LoadLatency ServerSendQueue { get; } = new LoadLatency();
        public LoadLatency ServerDispatchQueue { get; } = new LoadLatency();
        public int Samples { get; private set; }
        public int InvalidSnapshots { get; private set; }
        public long ClientNativeReservedStart { get; }
        public long ClientNativeReservedEnd { get; private set; }
        public ulong ClientNativeOwnedStart { get; }
        public ulong ClientNativeOwnedEnd { get; private set; }
        public ulong ClientNativeOwnedSampledMinimum { get; private set; }
        public ulong ClientNativeOwnedSampledPeak { get; private set; }
        public ulong ClientWaitsndSampledPeak { get; private set; }
        public ulong ClientNativeQueuesSampledPeak { get; private set; }
        public ulong ClientTimeoutRetransmits { get; private set; }
        public long ClientSocketSentBytes { get; private set; }
        public ulong ClientSocketReceivedBytes { get; private set; }
        public long? ServerNativeOwnedBytes => null;
        public bool Healthy => Samples > 0 && InvalidSnapshots == 0 && ServerOwnerWork.Count > 0 &&
            ServerOwnerInterval.Count > 0 && ServerOwnerAllocated.Count > 0;

        internal NetworkKcpSample(NetworkMetricsSnapshot[] snapshots)
        {
            baseline = snapshots.Select(value => value.Kcp).ToArray();
            ClientNativeReservedStart = baseline.Sum(value => value.NativeReservedBytes);
            ClientNativeOwnedStart = baseline.Aggregate(0UL, (sum, value) => sum + value.NativeOwnedBytes);
            ClientNativeOwnedSampledMinimum = ClientNativeOwnedStart;
            ClientNativeOwnedSampledPeak = ClientNativeOwnedStart;
            Record(snapshots);
        }

        internal void Record(NetworkMetricsSnapshot[] snapshots)
        {
            Samples++;
            long reserved = 0;
            ulong owned = 0, waiting = 0, queues = 0, retransmits = 0, received = 0;
            long sent = 0;
            for (int index = 0; index < snapshots.Length; index++)
            {
                KcpMetricsSnapshot value = snapshots[index].Kcp;
                KcpMetricsSnapshot start = baseline[index];
                if (!value.Available || value.FatalCode != 0)
                {
                    InvalidSnapshots++;
                }

                reserved += value.NativeReservedBytes;
                owned += value.NativeOwnedBytes;
                waiting += value.Waitsnd;
                queues += (ulong)value.InputQueueCount + value.OutputQueueCount + value.PlaintextQueueCount;
                retransmits += value.TimeoutRetransmits - start.TimeoutRetransmits;
                sent += value.SocketSentBytes - start.SocketSentBytes;
                received += value.ReceivedUdpBytes - start.ReceivedUdpBytes;
            }

            ClientNativeReservedEnd = reserved;
            ClientNativeOwnedEnd = owned;
            ClientNativeOwnedSampledMinimum = Math.Min(ClientNativeOwnedSampledMinimum, owned);
            ClientNativeOwnedSampledPeak = Math.Max(ClientNativeOwnedSampledPeak, owned);
            ClientWaitsndSampledPeak = Math.Max(ClientWaitsndSampledPeak, waiting);
            ClientNativeQueuesSampledPeak = Math.Max(ClientNativeQueuesSampledPeak, queues);
            ClientTimeoutRetransmits = retransmits;
            ClientSocketSentBytes = sent;
            ClientSocketReceivedBytes = received;
        }
    }

    internal sealed class KcpLoadObserver : IDisposable
    {
        private readonly object gate = new object();
        private readonly MeterListener listener = new MeterListener();
        private readonly NetworkKcpSample sample;
        private bool stopped;

        internal KcpLoadObserver(NetworkKcpSample sample)
        {
            this.sample = sample;
            listener.InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Name == "GameFrame.Server.Network.Kcp")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument.Name, value));
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument.Name, value));
            listener.Start();
        }

        private void Record(string name, double value)
        {
            lock (gate)
            {
                if (stopped)
                {
                    return;
                }

                switch (name)
                {
                    case "gameframe.kcp.owner.work":
                        sample.ServerOwnerWork.Record(value);
                        break;
                    case "gameframe.kcp.owner.interval":
                        sample.ServerOwnerInterval.Record(value);
                        break;
                    case "gameframe.kcp.owner.allocated":
                        sample.ServerOwnerAllocated.Record((long)value);
                        break;
                    case "gameframe.kcp.send.queue":
                        sample.ServerSendQueue.Record(value);
                        break;
                    case "gameframe.kcp.dispatch.queue":
                        sample.ServerDispatchQueue.Record(value);
                        break;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                stopped = true;
            }

            listener.Dispose();
        }
    }

    internal sealed record NetworkKcpCompletion(bool ListenerStopped, bool WorkerStopped,
        ulong ClientNativeOwnedBytes, long ClientNativeReservedBytes, ulong ClientNativeQueues)
    {
        public bool Released => ListenerStopped && WorkerStopped && ClientNativeOwnedBytes == 0 &&
            ClientNativeReservedBytes == 0 && ClientNativeQueues == 0;
        public bool ServerNativeOwnedBytesMeasured => false;
    }
}
