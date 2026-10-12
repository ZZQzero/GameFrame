namespace GameFrame.Server.Network
{
    public sealed class KcpServerOptions
    {
        public int MaxConnections { get; init; } = 64;
        public int MaxHandshakes { get; init; } = 16;
        public int MaxGrants { get; init; } = 256;
        public int NativeBytes { get; init; } = 256 * 1024 * 1024;
        public int ConnectionNativeBytes { get; init; } = 4 * 1024 * 1024;
        public int MaxOwnedBytes { get; init; } = 64 * 1024 * 1024;
        public int ConnectionOwnedBytes { get; init; } = 8 * 1024 * 1024;
        public int ReceiveQueueCount { get; init; } = 128;
        public int ReceiveQueueBytes { get; init; } = 2 * 1024 * 1024;
        public int SendQueueCount { get; init; } = 128;
        public int SendQueueBytes { get; init; } = 64 * 1024;
        public int UnreliableQueueCount { get; init; } = 64;
        public int UnreliableQueueBytes { get; init; } = 64 * 1024;
        public int ControlQueueCount { get; init; } = 64;
        public int ControlQueueBytes { get; init; } = 16 * 1024;
        public int MaxPacketsPerSecond { get; init; } = 128;
        public int MaxBytesPerSecond { get; init; } = 128 * 1024;
        public int SourceCount { get; init; } = 1024;
        public int SourcePacketsPerSecond { get; init; } = 2048;
        public int SourceBytesPerSecond { get; init; } = 2 * 1024 * 1024;
        public int CommandQueueCount { get; init; } = 256;

        internal KcpServerOptions Snapshot()
        {
            if (MaxConnections < 1 || MaxHandshakes < 1 || MaxGrants < 1 || NativeBytes < 65536 ||
                ConnectionNativeBytes < 65536 || ConnectionNativeBytes > 4 * 1024 * 1024 || NativeBytes < ConnectionNativeBytes ||
                MaxOwnedBytes < ConnectionOwnedBytes || ConnectionOwnedBytes < ReceiveQueueBytes ||
                ReceiveQueueCount < 1 || ReceiveQueueBytes < 32768 + 512 || SendQueueCount < 1 || SendQueueBytes < 32768 + 512 ||
                UnreliableQueueCount < 1 || UnreliableQueueBytes < 1150 + 512 || ControlQueueCount < 1 || ControlQueueBytes < 96 + 512 ||
                MaxPacketsPerSecond < 1 || MaxBytesPerSecond < 1 || SourceCount < 1 || SourcePacketsPerSecond < 1 ||
                SourceBytesPerSecond < 1 || CommandQueueCount < 1)
            {
                throw new ArgumentException("Invalid KCP listener capacities.");
            }

            return (KcpServerOptions)MemberwiseClone();
        }
    }
}
