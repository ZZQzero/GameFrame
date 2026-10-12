using System.Security.Cryptography;
using System.Text;

namespace Game.Server.Login
{
    public sealed class LocalAccountSettings : IDisposable
    {
        public string Account { get; }
        internal byte[] PasswordDigest { get; }
        public int MaxLoginAttemptsPerSecond { get; init; } = 5;
        public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan AccountLifetime { get; init; } = TimeSpan.FromHours(24);

        // This provider is deliberately local-only. Production has no default/test identity.
        public LocalAccountSettings(string account, string password)
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
            if (MaxLoginAttemptsPerSecond < 1 || LoginTimeout <= TimeSpan.Zero || LoginTimeout > TimeSpan.FromSeconds(10) ||
                AccountLifetime <= TimeSpan.Zero || AccountLifetime > TimeSpan.FromHours(24))
            {
                throw new ArgumentException("Invalid local account policy.");
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(PasswordDigest);
        }
    }
}
