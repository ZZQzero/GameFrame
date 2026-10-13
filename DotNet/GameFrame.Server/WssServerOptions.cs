using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;

namespace GameFrame.Server.Network
{
    public sealed class WssServerOptions
    {
        public int MaxConnections { get; init; } = 32;
        public int MaxConnectionsPerIp { get; init; } = 4;
        public int MaxRestrictedConnections { get; init; } = 16;
        public int MaxRestrictedConnectionsPerIp { get; init; } = 2;
        public int MaxPacketsPerSecond { get; init; } = 128;
        public int MaxBytesPerSecond { get; init; } = 128 * 1024;
        public int RestrictedPacketsPerSecond { get; init; } = 5;
        public int RestrictedBytesPerSecond { get; init; } = 16 * 1024;
        public int RestrictedBodyBytes { get; init; } = 2048;
        public int ReceiveQueueCount { get; init; } = 128;
        public int ReceiveQueueBytes { get; init; } = 2 * 1024 * 1024;
        public int BusinessQueueCount { get; init; } = 128;
        public int BusinessQueueBytes { get; init; } = 64 * 1024;
        public int ControlQueueCount { get; init; } = 64;
        public int ControlQueueBytes { get; init; } = 16 * 1024;
        public int MaxConnectionOwnedBytes { get; init; } = 8 * 1024 * 1024;
        public int MaxGlobalOwnedBytes { get; init; } = 64 * 1024 * 1024;
        public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
        private string[] allowedOrigins = Array.Empty<string>();
        public string[] AllowedOrigins
        {
            get => allowedOrigins;
            init => allowedOrigins = value;
        }

        internal WssServerOptions Snapshot()
        {
            // Receiving a maximum frame temporarily owns both the header lease and its replacement.
            if (MaxConnections < 1 || MaxConnectionsPerIp < 1 || MaxRestrictedConnections < 1 || MaxRestrictedConnectionsPerIp < 1 ||
                MaxPacketsPerSecond < 1 || MaxBytesPerSecond < 1 || RestrictedPacketsPerSecond < 1 || RestrictedBytesPerSecond < 1 ||
                RestrictedBodyBytes < 1 || RestrictedBodyBytes > 32736 || ReceiveQueueCount < 1 ||
                ReceiveQueueBytes < 32768 + FrameCodec.HeaderBytes + 2 * FrameMemoryBudget.FrameRecordReservation ||
                BusinessQueueCount < 1 || BusinessQueueBytes < 32768 + 512 || ControlQueueCount < 1 || ControlQueueBytes < 96 + 512 ||
                MaxConnectionOwnedBytes < ReceiveQueueBytes || MaxGlobalOwnedBytes < MaxConnectionOwnedBytes ||
                HelloTimeout <= TimeSpan.Zero || HelloTimeout > TimeSpan.FromSeconds(5) || AllowedOrigins == null)
            {
                throw new ArgumentException("Invalid WSS capacities or Hello timeout.");
            }

            var snapshot = (WssServerOptions)MemberwiseClone();
            snapshot.allowedOrigins = (string[])AllowedOrigins.Clone();
            return snapshot;
        }
    }
}
