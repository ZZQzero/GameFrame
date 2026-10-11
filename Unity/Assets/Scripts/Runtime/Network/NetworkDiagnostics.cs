using System;

namespace GameFrame.Network
{
    public readonly struct NetworkStateChange
    {
        public NetworkClientState State { get; }
        public long Generation { get; }
        public Exception Error { get; }

        internal NetworkStateChange(NetworkClientState state, long generation, Exception error)
        {
            State = state;
            Generation = generation;
            Error = error;
        }
    }

    public readonly struct NetworkMessageFault
    {
        public uint Opcode { get; }
        public long Generation { get; }
        public Exception Error { get; }

        internal NetworkMessageFault(uint opcode, long generation, Exception error)
        {
            Opcode = opcode;
            Generation = generation;
            Error = error;
        }
    }

    public readonly struct NetworkMetricsSnapshot
    {
        public long Generation { get; }
        public long OwnedBytes { get; }
        public int ReliableSendCount { get; }
        public int ReliableReceiveCount { get; }
        public int PendingCount { get; }
        public int DeadlineCount { get; }
        public long SentFrames { get; }
        public long ReceivedFrames { get; }
        public long LateResponses { get; }
        public long RequestTimeouts { get; }
        public long RequestCancellations { get; }
        public long SendQueueAgeWarnings { get; }

        internal NetworkMetricsSnapshot(long generation, long ownedBytes, int sendCount, int receiveCount,
            int pendingCount, int deadlineCount, long sentFrames, long receivedFrames, long lateResponses,
            long timeouts, long cancellations, long queueAgeWarnings)
        {
            Generation = generation;
            OwnedBytes = ownedBytes;
            ReliableSendCount = sendCount;
            ReliableReceiveCount = receiveCount;
            PendingCount = pendingCount;
            DeadlineCount = deadlineCount;
            SentFrames = sentFrames;
            ReceivedFrames = receivedFrames;
            LateResponses = lateResponses;
            RequestTimeouts = timeouts;
            RequestCancellations = cancellations;
            SendQueueAgeWarnings = queueAgeWarnings;
        }
    }
}
