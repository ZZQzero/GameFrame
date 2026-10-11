using System;

namespace GameFrame.Network
{
    public enum NetworkClientState
    {
        Idle,
        Connecting,
        Handshaking,
        Connected,
        Faulted,
        Closing,
        Disposed
    }

    public sealed class NetworkRuntimeOptions
    {
        public int MaxClients { get; set; } = 4;
        public long MaxOwnedBytes { get; set; } = 64 * 1024 * 1024;
        public long MaxIdlePoolBytes { get; set; } = 8 * 1024 * 1024;
        public TimeSpan PumpBudget { get; set; } = TimeSpan.FromMilliseconds(2);
        public int MaxMessagesPerPump { get; set; } = 128;

        internal NetworkRuntimeOptions Snapshot()
        {
            if (MaxClients < 1 || MaxOwnedBytes < 1 || MaxIdlePoolBytes < 0 || MaxIdlePoolBytes > MaxOwnedBytes ||
                PumpBudget <= TimeSpan.Zero || MaxMessagesPerPump < 1)
            {
                throw new ArgumentException("Invalid network host limits.");
            }

            return (NetworkRuntimeOptions)MemberwiseClone();
        }
    }

    public sealed class NetworkClientOptions
    {
        public int ReliableSendBytes { get; set; } = 64 * 1024;
        public int ReliableSendCount { get; set; } = 128;
        public int ReliableReceiveBytes { get; set; } = 2 * 1024 * 1024;
        public int ReliableReceiveCount { get; set; } = 1024;
        public int UnreliableQueueBytes { get; set; } = 64 * 1024;
        public int UnreliableQueueCount { get; set; } = 64;
        public int ControlQueueBytes { get; set; } = 16 * 1024;
        public int ControlQueueCount { get; set; } = 64;
        public int PendingCount { get; set; } = 512;
        public int PendingMetadataBytes { get; set; } = 256 * 1024;
        public int NativeBytes { get; set; } = 4 * 1024 * 1024;
        public int MaxOwnedBytes { get; set; } = 8 * 1024 * 1024;
        public TimeSpan MaxSendQueueAge { get; set; } = TimeSpan.FromMilliseconds(250);
        public TimeSpan UnreliableMaxAge { get; set; } = TimeSpan.FromMilliseconds(250);
        public int BusinessBytesPerSecond { get; set; } = 128 * 1024;
        public int BusinessBurstBytes { get; set; } = 32 * 1024;
        public int UnreliableBytesPerSecond { get; set; } = 32 * 1024;
        public int UnreliableBurstBytes { get; set; } = 8 * 1024;
        public Uri[] TrustedWebSocketEndpoints { get; set; } = Array.Empty<Uri>();
        public Uri[] TrustedUdpEndpoints { get; set; } = Array.Empty<Uri>();

        internal NetworkClientOptions Snapshot()
        {
            if (ReliableSendBytes < 1 || ReliableSendCount < 1 || ReliableReceiveBytes < 1 || ReliableReceiveCount < 1 ||
                UnreliableQueueBytes < 1 || UnreliableQueueCount < 1 || ControlQueueBytes < 1 || ControlQueueCount < 1 ||
                PendingCount < 1 || PendingMetadataBytes < 1 || NativeBytes < 1 || MaxOwnedBytes < 1 ||
                MaxSendQueueAge <= TimeSpan.Zero || UnreliableMaxAge <= TimeSpan.Zero ||
                BusinessBytesPerSecond < 1 || BusinessBurstBytes < 1 ||
                UnreliableBytesPerSecond < 1 || UnreliableBurstBytes < 1)
            {
                throw new ArgumentException("Invalid network client limits or token bucket burst.");
            }

            if (ReliableSendBytes > MaxOwnedBytes || ReliableReceiveBytes > MaxOwnedBytes ||
                UnreliableQueueBytes > MaxOwnedBytes || ControlQueueBytes > MaxOwnedBytes ||
                PendingMetadataBytes > MaxOwnedBytes || NativeBytes > MaxOwnedBytes)
            {
                throw new ArgumentException("Individual memory limits exceed the connection's owned memory limit.");
            }

            if (TrustedWebSocketEndpoints == null || TrustedUdpEndpoints == null)
            {
                throw new ArgumentException("Trusted endpoint collections must not be null.");
            }

            var snapshot = (NetworkClientOptions)MemberwiseClone();
            snapshot.TrustedWebSocketEndpoints = (Uri[])TrustedWebSocketEndpoints.Clone();
            snapshot.TrustedUdpEndpoints = (Uri[])TrustedUdpEndpoints.Clone();
            return snapshot;
        }

        internal void ValidateKcpNegotiation(int reliableLimit, int unreliableLimit)
        {
            if (BusinessBurstBytes < reliableLimit || (unreliableLimit != 0 && UnreliableBurstBytes < unreliableLimit))
            {
                throw new ArgumentException("The token bucket burst must accommodate the negotiated frame limits.");
            }
        }
    }
}
