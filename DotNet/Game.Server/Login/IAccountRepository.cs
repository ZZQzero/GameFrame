using System.Security.Cryptography;

namespace Game.Server.Login
{
    internal sealed record StoredAccount(long Id, byte[] Salt, byte[] PasswordHash, int Iterations) : IDisposable
    {
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Salt);
            CryptographicOperations.ZeroMemory(PasswordHash);
        }
    }

    internal interface IAccountRepository
    {
        Task<bool> CreateAsync(string account, byte[] salt, byte[] hash, int iterations, CancellationToken cancellation);
        Task<StoredAccount?> FindAsync(string account, CancellationToken cancellation);
        Task<ulong> AdvanceEpochAsync(long accountId, CancellationToken cancellation);
    }
}
