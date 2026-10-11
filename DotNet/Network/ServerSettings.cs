using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace GameFrame.Server.Network
{

    public sealed class ServerSettings : IDisposable
    {
        public string Account { get; }
        internal byte[] PasswordDigest { get; }
        public int MaxConnections { get; init; } = 32;
        public int MaxConnectionsPerIp { get; init; } = 4;
        public int MaxUnauthenticated { get; init; } = 16;
        public int MaxUnauthenticatedPerIp { get; init; } = 2;
        public int MaxLoginAttemptsPerSecond { get; init; } = 5;
        public int MaxAuthenticatedPacketsPerSecond { get; init; } = 128;
        public int MaxAuthenticatedBytesPerSecond { get; init; } = 128 * 1024;
        public int ReceiveQueueCount { get; init; } = 128;
        public int ReceiveQueueBytes { get; init; } = 2 * 1024 * 1024;
        public int BusinessQueueCount { get; init; } = 128;
        public int BusinessQueueBytes { get; init; } = 64 * 1024;
        public int ControlQueueCount { get; init; } = 64;
        public int ControlQueueBytes { get; init; } = 16 * 1024;
        public int MaxConnectionOwnedBytes { get; init; } = 8 * 1024 * 1024;
        public int MaxGlobalOwnedBytes { get; init; } = 64 * 1024 * 1024;
        public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan AccountLifetime { get; init; } = TimeSpan.FromHours(24);
        public string[] AllowedOrigins { get; init; } = Array.Empty<string>();

        // This provider is deliberately local-only. Production has no default/test identity.
        public ServerSettings(string account, string password)
        {
            if (string.IsNullOrWhiteSpace(account) || account.Length > 64 ||
                string.IsNullOrEmpty(password) || password.Length > 128)
            {
                throw new ArgumentException("Local account/password must be explicitly configured within protocol limits.");
            }

            Account = account;
            byte[] bytes = Encoding.UTF8.GetBytes(password);
            try
            {
                PasswordDigest = SHA256.HashData(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        internal void Validate()
        {
            if (MaxConnections < 1 || MaxConnectionsPerIp < 1 || MaxUnauthenticated < 1 ||
                MaxUnauthenticatedPerIp < 1 || MaxLoginAttemptsPerSecond < 1 ||
                MaxAuthenticatedPacketsPerSecond < 1 || MaxAuthenticatedBytesPerSecond < 1 ||
                ReceiveQueueCount < 1 || ReceiveQueueBytes < 32768 || BusinessQueueCount < 1 ||
                BusinessQueueBytes < 32768 || ControlQueueCount < 1 || ControlQueueBytes < 96 ||
                MaxConnectionOwnedBytes < ReceiveQueueBytes || MaxGlobalOwnedBytes < MaxConnectionOwnedBytes ||
                HelloTimeout <= TimeSpan.Zero || HelloTimeout > TimeSpan.FromSeconds(5) ||
                LoginTimeout <= TimeSpan.Zero || LoginTimeout > TimeSpan.FromSeconds(10) ||
                AccountLifetime <= TimeSpan.Zero || AccountLifetime > TimeSpan.FromHours(24) || AllowedOrigins == null)
            {
                throw new ArgumentException("Invalid local shard capacity or deadlines.");
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(PasswordDigest);
        }
    }
}
