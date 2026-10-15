using GameFrame.Network;

namespace GameFrame.Tests
{
    internal sealed class NetworkIdleSample
    {
        private readonly NetworkMetricsSnapshot[] baseline;
        public int ExpectedConnections => baseline.Length;
        public bool HeartbeatRequired { get; }
        public int Samples { get; private set; }
        public int ConnectedClientsSampledMinimum { get; private set; }
        public int ServerConnectionsSampledMinimum { get; private set; }
        public long ClientOwnedBytesStart { get; }
        public long ClientOwnedBytesEnd { get; private set; }
        public long ClientOwnedBytesSampledMinimum { get; private set; }
        public long ClientOwnedBytesSampledMaximum { get; private set; }
        public long ServerOwnedBytesStart { get; }
        public long ServerOwnedBytesEnd { get; private set; }
        public long ServerOwnedBytesSampledMinimum { get; private set; }
        public long ServerOwnedBytesSampledMaximum { get; private set; }
        public long ClientOwnedBytesChange => ClientOwnedBytesEnd - ClientOwnedBytesStart;
        public long ServerOwnedBytesChange => ServerOwnedBytesEnd - ServerOwnedBytesStart;
        public int BusinessQueueSampledPeak { get; private set; }
        public int PendingSampledPeak { get; private set; }
        public int DeadlineSampledPeak { get; private set; }
        public long SentBusinessBodyBytes { get; private set; }
        public long ReceivedBusinessBodyBytes { get; private set; }
        public long ReceivedBusinessFrames { get; private set; }
        public long SentApplicationBytes { get; private set; }
        public long ReceivedApplicationBytes { get; private set; }
        public int ClientsWithBidirectionalControlTraffic { get; private set; }
        public bool Healthy => Samples != 0 && ConnectedClientsSampledMinimum == ExpectedConnections &&
            ServerConnectionsSampledMinimum == ExpectedConnections && BusinessQueueSampledPeak == 0 &&
            PendingSampledPeak == 0 && DeadlineSampledPeak == 0 && SentBusinessBodyBytes == 0 &&
            ReceivedBusinessBodyBytes == 0 && ReceivedBusinessFrames == 0 &&
            (!HeartbeatRequired || ClientsWithBidirectionalControlTraffic == ExpectedConnections);

        internal NetworkIdleSample(NetworkMetricsSnapshot[] baseline, bool heartbeatRequired, long clientOwnedBytes, long serverOwnedBytes)
        {
            this.baseline = baseline;
            HeartbeatRequired = heartbeatRequired;
            ConnectedClientsSampledMinimum = baseline.Length;
            ServerConnectionsSampledMinimum = baseline.Length;
            ClientOwnedBytesStart = clientOwnedBytes;
            ClientOwnedBytesEnd = clientOwnedBytes;
            ClientOwnedBytesSampledMinimum = clientOwnedBytes;
            ClientOwnedBytesSampledMaximum = clientOwnedBytes;
            ServerOwnedBytesStart = serverOwnedBytes;
            ServerOwnedBytesEnd = serverOwnedBytes;
            ServerOwnedBytesSampledMinimum = serverOwnedBytes;
            ServerOwnedBytesSampledMaximum = serverOwnedBytes;
        }

        internal void Record(NetworkMetricsSnapshot[] snapshots, int connectedClients, int serverConnections,
            long clientOwnedBytes, long serverOwnedBytes)
        {
            Samples++;
            ConnectedClientsSampledMinimum = Math.Min(ConnectedClientsSampledMinimum, connectedClients);
            ServerConnectionsSampledMinimum = Math.Min(ServerConnectionsSampledMinimum, serverConnections);
            ClientOwnedBytesEnd = clientOwnedBytes;
            ServerOwnedBytesEnd = serverOwnedBytes;
            ClientOwnedBytesSampledMinimum = Math.Min(ClientOwnedBytesSampledMinimum, clientOwnedBytes);
            ClientOwnedBytesSampledMaximum = Math.Max(ClientOwnedBytesSampledMaximum, clientOwnedBytes);
            ServerOwnedBytesSampledMinimum = Math.Min(ServerOwnedBytesSampledMinimum, serverOwnedBytes);
            ServerOwnedBytesSampledMaximum = Math.Max(ServerOwnedBytesSampledMaximum, serverOwnedBytes);
            int queued = 0;
            int pending = 0;
            int deadlines = 0;
            long sentBusiness = 0;
            long receivedBusiness = 0;
            long receivedFrames = 0;
            long sentControl = 0;
            long receivedControl = 0;
            int controlClients = 0;
            for (int index = 0; index < snapshots.Length; index++)
            {
                NetworkMetricsSnapshot current = snapshots[index];
                NetworkMetricsSnapshot start = baseline[index];
                queued += current.ReliableSendCount + current.ReliableReceiveCount;
                pending += current.PendingCount;
                deadlines += current.DeadlineCount;
                long sentBody = current.SentBusinessBodyBytes - start.SentBusinessBodyBytes;
                long receivedBody = current.ReceivedBusinessBodyBytes - start.ReceivedBusinessBodyBytes;
                sentBusiness += sentBody;
                receivedBusiness += receivedBody;
                long businessFrames = current.ReceivedFrames - start.ReceivedFrames;
                receivedFrames += businessFrames;
                long sent = current.SentApplicationBytes - start.SentApplicationBytes;
                long received = current.ReceivedApplicationBytes - start.ReceivedApplicationBytes;
                sentControl += sent;
                receivedControl += received;
                // No business requests are issued in this phase. A Ping and Pong each occupy 40 bytes.
                if (sentBody == 0 && businessFrames == 0 && sent >= 80 && received >= 80)
                {
                    controlClients++;
                }
            }

            BusinessQueueSampledPeak = Math.Max(BusinessQueueSampledPeak, queued);
            PendingSampledPeak = Math.Max(PendingSampledPeak, pending);
            DeadlineSampledPeak = Math.Max(DeadlineSampledPeak, deadlines);
            SentBusinessBodyBytes = sentBusiness;
            ReceivedBusinessBodyBytes = receivedBusiness;
            ReceivedBusinessFrames = receivedFrames;
            SentApplicationBytes = sentControl;
            ReceivedApplicationBytes = receivedControl;
            ClientsWithBidirectionalControlTraffic = controlClients;
        }
    }
}
