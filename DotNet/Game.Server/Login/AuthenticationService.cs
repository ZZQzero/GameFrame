using System.Net;
using System.Security.Cryptography;

namespace Game.Server.Login
{
    internal sealed class AuthenticationService : IDisposable
    {
        private readonly IAccountRepository repository;
        private readonly AccountSessions sessions;
        private readonly AuthenticationRateLimiter limiter;
        private readonly SemaphoreSlim operations = new SemaphoreSlim(4, 4);

        internal AuthenticationService(IAccountRepository repository, AccountSessions sessions, LoginOptions options)
        {
            this.repository = repository;
            this.sessions = sessions;
            limiter = new AuthenticationRateLimiter(options);
        }

        internal async Task<AuthenticationStatus> RegisterAsync(string account, string password, CancellationToken cancellation)
        {
            if (!limiter.AdmitAccount(account))
            {
                return AuthenticationStatus.Overloaded;
            }

            if (!PasswordHasher.Valid(account, password))
            {
                return AuthenticationStatus.InvalidInput;
            }

            if (!await operations.WaitAsync(0, cancellation))
            {
                return AuthenticationStatus.Overloaded;
            }

            byte[] salt = Array.Empty<byte>();
            byte[] hash = Array.Empty<byte>();
            try
            {
                salt = RandomNumberGenerator.GetBytes(16);
                hash = await Task.Run(() => PasswordHasher.Hash(password, salt, PasswordHasher.Iterations), cancellation);
                return await repository.CreateAsync(account, salt, hash, PasswordHasher.Iterations, cancellation) ?
                    AuthenticationStatus.Success : AuthenticationStatus.AccountAlreadyExists;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(hash);
                operations.Release();
            }
        }

        internal async Task<AuthenticationResult> LoginAsync(
            string account, string password, Action<AccountSessionEndReason> disconnect, CancellationToken cancellation)
        {
            if (!limiter.AdmitAccount(account))
            {
                return AuthenticationResult.Failure(AuthenticationStatus.Overloaded);
            }

            if (!PasswordHasher.Valid(account, password))
            {
                return AuthenticationResult.Failure();
            }

            if (!await operations.WaitAsync(0, cancellation))
            {
                return AuthenticationResult.Failure(AuthenticationStatus.Overloaded);
            }

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
                                return AuthenticationResult.Failure();
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

        internal bool AdmitConnection(IPAddress address) => limiter.AdmitIp(address);

        internal AuthenticationResult Resume(byte[] accountSessionId, byte[] resumeToken, Action<AccountSessionEndReason> disconnect,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return sessions.Resume(accountSessionId, resumeToken, disconnect);
        }

        internal AuthenticationStatus Logout(long binding) =>
            sessions.Logout(binding) ? AuthenticationStatus.Success : AuthenticationStatus.SessionRevoked;

        public void Dispose() => operations.Dispose();
    }
}
