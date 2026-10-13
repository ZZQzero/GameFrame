using System.Security.Cryptography;
using Game.Hotfix.Network.Protocol;
using GameFrame.Server.Network;

namespace Game.Server.Login
{
    internal sealed class AccountService : IDisposable
    {
        private readonly IAccountRepository repository;
        private readonly AccountSessions sessions;
        private readonly SemaphoreSlim operations = new SemaphoreSlim(4, 4);

        internal AccountService(IAccountRepository repository, AccountSessions sessions)
        {
            this.repository = repository;
            this.sessions = sessions;
        }

        internal async Task<ResultCode> RegisterAsync(string account, string password, CancellationToken cancellation)
        {
            if (!PasswordHasher.Valid(account, password))
            {
                return ResultCode.InvalidInput;
            }

            await AdmitAsync(cancellation);
            byte[] salt = Array.Empty<byte>();
            byte[] hash = Array.Empty<byte>();
            try
            {
                salt = RandomNumberGenerator.GetBytes(16);
                hash = await Task.Run(() => PasswordHasher.Hash(password, salt, PasswordHasher.Iterations), cancellation);
                return await repository.CreateAsync(account, salt, hash, PasswordHasher.Iterations, cancellation) ?
                    ResultCode.Success : ResultCode.AccountAlreadyExists;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(hash);
                operations.Release();
            }
        }

        internal async Task<(LoginResponse Response, AccountSessions.Binding? Binding)> LoginAsync(
            string account, string password, Action<ushort> disconnect, CancellationToken cancellation)
        {
            if (!PasswordHasher.Valid(account, password))
            {
                return (new LoginResponse { ResultCode = ResultCode.InvalidCredentials }, null);
            }

            await AdmitAsync(cancellation);
            try
            {
                StoredAccount? stored = await repository.FindAsync(account, cancellation);
                bool created = false;
                if (stored == null)
                {
                    byte[] salt = RandomNumberGenerator.GetBytes(16);
                    byte[] hash = Array.Empty<byte>();
                    try
                    {
                        hash = await Task.Run(() => PasswordHasher.Hash(password, salt, PasswordHasher.Iterations), cancellation);
                        created = await repository.CreateAsync(account, salt, hash, PasswordHasher.Iterations, cancellation);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(salt);
                        CryptographicOperations.ZeroMemory(hash);
                    }

                    // The unique account name decides concurrent creation. A losing request must
                    // authenticate against the winner's credentials, never overwrite its password.
                    stored = await repository.FindAsync(account, cancellation) ??
                        throw new InvalidOperationException("Account missing after creation was resolved.");
                }

                using (stored)
                {
                    if (stored.Id <= 0 || stored.Salt.Length != 16 || stored.PasswordHash.Length != 32 ||
                        stored.Iterations != PasswordHasher.Iterations)
                    {
                        throw new InvalidOperationException("Invalid stored account credentials.");
                    }

                    if (!created)
                    {
                        byte[] hash = await Task.Run(() => PasswordHasher.Hash(password, stored.Salt, stored.Iterations), cancellation);
                        try
                        {
                            if (!CryptographicOperations.FixedTimeEquals(hash, stored.PasswordHash))
                            {
                                return (new LoginResponse { ResultCode = ResultCode.InvalidCredentials }, null);
                            }
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(hash);
                        }
                    }

                    return await sessions.LoginAsync(checked((ulong)stored.Id),
                        token => repository.AdvanceEpochAsync(stored.Id, token), disconnect, cancellation);
                }
            }
            finally
            {
                operations.Release();
            }
        }

        private async Task AdmitAsync(CancellationToken cancellation)
        {
            if (!await operations.WaitAsync(0, cancellation))
            {
                throw new ServerRequestException(1);
            }
        }

        public void Dispose() => operations.Dispose();
    }
}
