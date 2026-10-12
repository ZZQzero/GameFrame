using System.Security.Cryptography;
using Game.Server.Login;

namespace GameFrame.Tests
{
    // Deterministic storage double for transport/heartbeat tests; never used by the server.
    internal sealed class TestAccountRepository : IAccountRepository, IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, (long Id, byte[] Salt, byte[] Hash, int Iterations, ulong Epoch)> accounts = new();

        public Task<bool> CreateAsync(string account, byte[] salt, byte[] hash, int iterations, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (accounts.ContainsKey(account))
                {
                    return Task.FromResult(false);
                }

                accounts.Add(account, (accounts.Count + 1, (byte[])salt.Clone(), (byte[])hash.Clone(), iterations, 0));
                return Task.FromResult(true);
            }
        }

        public Task<StoredAccount?> FindAsync(string account, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (gate)
            {
                return Task.FromResult(accounts.TryGetValue(account, out var value) ?
                    new StoredAccount(value.Id, (byte[])value.Salt.Clone(), (byte[])value.Hash.Clone(), value.Iterations) : null);
            }
        }

        public Task<ulong> AdvanceEpochAsync(long accountId, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (gate)
            {
                string key = accounts.First(pair => pair.Value.Id == accountId).Key;
                var account = accounts[key];
                account.Epoch++;
                accounts[key] = account;
                return Task.FromResult(account.Epoch);
            }
        }

        public void Dispose()
        {
            foreach (var account in accounts.Values)
            {
                CryptographicOperations.ZeroMemory(account.Salt);
                CryptographicOperations.ZeroMemory(account.Hash);
            }
        }
    }
}
