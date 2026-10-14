using System;
using GameFrame.Network.Transport.Kcp;

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

    public readonly struct KcpMetricsSnapshot
    {
        public bool Available { get; }
        public bool DtlsDiscardDetailsAvailable => false;
        public uint Capabilities { get; }
        public ulong NativeAllocations { get; }
        public ulong PolledUdpBytes { get; }
        public ulong ReceivedUdpPackets { get; }
        public uint InputQueueCount { get; }
        public uint OutputOldestMilliseconds { get; }
        public uint PlaintextQueueCount { get; }
        public int NativeState { get; }
        public int FatalCode { get; }
        public int NativeError { get; }
        public long NativeReservedBytes { get; }
        public ulong NativeOwnedBytes { get; }
        public ulong NativePeakBytes { get; }
        public ulong RawDropped { get; }
        public long TransportUnreliableDropped { get; }
        public ulong NativeUnreliableDropped { get; }
        public uint Waitsnd { get; }
        public uint SrttMilliseconds { get; }
        public uint RtoMilliseconds { get; }
        public uint SendWindow { get; }
        public uint ReceiveWindow { get; }
        public uint RemoteWindow { get; }
        public ulong PendingReliableBytes { get; }
        public ulong InflightReliableBytes { get; }
        public ulong TimeoutRetransmits { get; }
        public uint OutputQueueBytes { get; }
        public uint OutputQueueCount { get; }
        public uint PlaintextQueueBytes { get; }
        public long SocketSentBytes { get; }
        public long SocketSentPackets { get; }
        public ulong ReceivedUdpBytes { get; }
        public int SocketReceiveBufferBytes { get; }
        public int SocketSendBufferBytes { get; }

        internal KcpMetricsSnapshot(KcpNative.Stats stats, long reservedBytes, long socketSentBytes,
            long socketSentPackets, int socketReceiveBuffer, int socketSendBuffer, long transportUnreliableDropped = 0)
        {
            Available = true;
            Capabilities = stats.Capabilities;
            NativeAllocations = stats.Allocations;
            PolledUdpBytes = stats.PolledBytes;
            ReceivedUdpPackets = stats.ReceivedPackets;
            InputQueueCount = stats.InputCount;
            OutputOldestMilliseconds = stats.OutputOldestMs;
            PlaintextQueueCount = stats.PlaintextCount;
            NativeState = (int)stats.State;
            FatalCode = stats.FatalCode;
            NativeError = stats.NativeError;
            NativeReservedBytes = reservedBytes;
            NativeOwnedBytes = stats.OwnedBytes;
            NativePeakBytes = stats.PeakBytes;
            RawDropped = stats.RawDropped;
            NativeUnreliableDropped = stats.UnreliableDropped;
            TransportUnreliableDropped = transportUnreliableDropped;
            Waitsnd = stats.Waitsnd;
            SrttMilliseconds = stats.SrttMs;
            RtoMilliseconds = stats.RtoMs;
            SendWindow = stats.SendWindow;
            ReceiveWindow = stats.ReceiveWindow;
            RemoteWindow = stats.RemoteWindow;
            PendingReliableBytes = stats.PendingReliableBytes;
            InflightReliableBytes = stats.InflightReliableBytes;
            TimeoutRetransmits = stats.TimeoutRetransmits;
            OutputQueueBytes = stats.OutputBytes;
            OutputQueueCount = stats.OutputCount;
            PlaintextQueueBytes = stats.PlaintextBytes;
            SocketSentBytes = socketSentBytes;
            SocketSentPackets = socketSentPackets;
            ReceivedUdpBytes = stats.ReceivedBytes;
            SocketReceiveBufferBytes = socketReceiveBuffer;
            SocketSendBufferBytes = socketSendBuffer;
        }
    }

    /// <summary>当前连接的耗时汇总；不保留逐请求记录，也不推算分位数。</summary>
    public readonly struct NetworkDurationSnapshot
    {
        public long Count { get; }
        public double LastMilliseconds { get; }
        public double TotalMilliseconds { get; }
        public double MaximumMilliseconds { get; }
        public double AverageMilliseconds => Count == 0 ? 0 : TotalMilliseconds / Count;

        private NetworkDurationSnapshot(long count, double last, double total, double maximum)
        {
            Count = count;
            LastMilliseconds = last;
            TotalMilliseconds = total;
            MaximumMilliseconds = maximum;
        }

        internal NetworkDurationSnapshot Add(double milliseconds) =>
            new NetworkDurationSnapshot(Count + 1, milliseconds, TotalMilliseconds + milliseconds,
                Math.Max(MaximumMilliseconds, milliseconds));
    }

    /// <summary>仅统计成功解码的 RPC；任务 continuation 的执行成本不计入。</summary>
    public readonly struct RpcMetricsSnapshot
    {
        public long SuccessfulResponses => ResponseWait.Count;
        public NetworkDurationSnapshot Encoding { get; }
        public NetworkDurationSnapshot ResponseWait { get; }
        public NetworkDurationSnapshot MainThreadWait { get; }
        public NetworkDurationSnapshot Decoding { get; }
        public NetworkDurationSnapshot Completion { get; }

        internal RpcMetricsSnapshot(NetworkDurationSnapshot encoding, NetworkDurationSnapshot responseWait,
            NetworkDurationSnapshot mainThreadWait, NetworkDurationSnapshot decoding, NetworkDurationSnapshot completion)
        {
            Encoding = encoding;
            ResponseWait = responseWait;
            MainThreadWait = mainThreadWait;
            Decoding = decoding;
            Completion = completion;
        }
    }

    /// <summary>各子预算当前预约值，含在途帧及保守元数据额度；不是实际进程内存。</summary>
    public readonly struct NetworkMemorySnapshot
    {
        public long ReliableSendReservedBytes { get; }
        public long ReliableReceiveReservedBytes { get; }
        public long ControlReservedBytes { get; }
        public long UnreliableSendReservedBytes { get; }
        public long UnreliableReceiveReservedBytes { get; }
        public long PendingReservedBytes { get; }

        internal NetworkMemorySnapshot(long reliableSend, long reliableReceive, long control,
            long unreliableSend, long unreliableReceive, long pending)
        {
            ReliableSendReservedBytes = reliableSend;
            ReliableReceiveReservedBytes = reliableReceive;
            ControlReservedBytes = control;
            UnreliableSendReservedBytes = unreliableSend;
            UnreliableReceiveReservedBytes = unreliableReceive;
            PendingReservedBytes = pending;
        }
    }

    public readonly struct NetworkMetricsSnapshot
    {
        public KcpMetricsSnapshot Kcp { get; }
        public RpcMetricsSnapshot Rpc { get; }
        public NetworkMemorySnapshot Memory { get; }
        public long UnreliableDropped { get; }
        public long Generation { get; }
        public long OwnedBytes { get; }
        public long PeakReservedBytes { get; }
        public long SentApplicationBytes { get; }
        public long ReceivedApplicationBytes { get; }
        public long SentBusinessBodyBytes { get; }
        public long ReceivedBusinessBodyBytes { get; }
        public long PingRoundTripMilliseconds { get; }
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
            long timeouts, long cancellations, long queueAgeWarnings, KcpMetricsSnapshot kcp = default, long unreliableDropped = 0,
            long peakReservedBytes = 0, long sentApplicationBytes = 0, long receivedApplicationBytes = 0,
            long sentBusinessBodyBytes = 0, long receivedBusinessBodyBytes = 0, long pingRoundTripMilliseconds = 0,
            RpcMetricsSnapshot rpc = default, NetworkMemorySnapshot memory = default)
        {
            Rpc = rpc;
            Memory = memory;
            Generation = generation;
            OwnedBytes = ownedBytes;
            PeakReservedBytes = peakReservedBytes;
            SentApplicationBytes = sentApplicationBytes;
            ReceivedApplicationBytes = receivedApplicationBytes;
            SentBusinessBodyBytes = sentBusinessBodyBytes;
            ReceivedBusinessBodyBytes = receivedBusinessBodyBytes;
            PingRoundTripMilliseconds = pingRoundTripMilliseconds;
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
            Kcp = kcp;
            UnreliableDropped = unreliableDropped;
        }
    }
}
