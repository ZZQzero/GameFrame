using System.Security.Cryptography;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Server.Network;

namespace Game.Server.Login
{
    internal sealed class AccountSessions : IDisposable
    {
        private sealed class Session
        {
            internal ulong AccountId;
            internal ulong Epoch;
            internal long Binding;
            internal byte[] Id = Array.Empty<byte>();
            internal byte[] ResumeDigest = Array.Empty<byte>();
            internal long Deadline;
            internal long ExpiresUtc;
            internal Action? Disconnect;
            internal long AttemptWindow;
            internal int Attempts;
        }

        private readonly object gate = new object();
        private readonly LoginOptions options;
        private readonly Dictionary<ulong, Session> accounts = new Dictionary<ulong, Session>();
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private readonly Dictionary<long, Session> bindings = new Dictionary<long, Session>();
        private long nextBinding;
        private bool disposed;
        internal static long Now => NetworkClock.GetMilliseconds();

        internal AccountSessions(LoginOptions options)
        {
            options.Validate();
            this.options = options;
        }

        internal (LoginResponse Response, long Binding) Login(AuthenticatedAccount account, Action disconnect, CancellationToken cancellation)
        {
            Action? revoke;
            (LoginResponse Response, long Binding) result;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellation.ThrowIfCancellationRequested();
                if (account.Id == 0 || account.Epoch == 0)
                {
                    throw new ArgumentException("Authenticated account identity must be nonzero.");
                }

                if (accounts.TryGetValue(account.Id, out Session? previous) && previous.Epoch >= account.Epoch)
                {
                    return (Failure(), 0);
                }

                foreach (Session expired in accounts.Values.Where(value => Now >= value.Deadline).ToArray())
                {
                    Remove(expired);
                }

                accounts.TryGetValue(account.Id, out previous);
                if (previous == null && accounts.Count >= options.MaxSessions)
                {
                    throw new ServerRequestException(3);
                }

                revoke = previous?.Disconnect;
                if (previous != null)
                {
                    Remove(previous);
                }

                var session = new Session
                {
                    AccountId = account.Id,
                    Epoch = account.Epoch,
                    Binding = NextBinding(),
                    Id = RandomNumberGenerator.GetBytes(16),
                    Deadline = checked(Now + (long)options.AccountLifetime.TotalMilliseconds),
                    ExpiresUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)options.AccountLifetime.TotalMilliseconds,
                    Disconnect = disconnect
                };
                accounts.Add(account.Id, session);
                sessions.Add(Convert.ToHexString(session.Id), session);
                bindings.Add(session.Binding, session);
                result = (RotateToken(session), session.Binding);
            }

            try
            {
                revoke?.Invoke();
                return result;
            }
            catch
            {
                ClearResponse(result.Response);
                throw;
            }
        }

        internal (LoginResponse Response, long Binding) Resume(ResumeLoginRequest request, Action disconnect)
        {
            if (request.AccountSessionId.Length != 16 || request.ResumeToken.Length != 32)
            {
                return (Failure(), 0);
            }

            byte[] digest = SHA256.HashData(request.ResumeToken);
            Action? revoke;
            (LoginResponse Response, long Binding) result;
            try
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (!sessions.TryGetValue(Convert.ToHexString(request.AccountSessionId), out Session? session) ||
                        Now >= session.Deadline || !AllowAttempt(session) ||
                        !CryptographicOperations.FixedTimeEquals(digest, session.ResumeDigest))
                    {
                        return (Failure(), 0);
                    }

                    long binding = NextBinding();
                    revoke = session.Disconnect;
                    bindings.Remove(session.Binding);
                    session.Binding = binding;
                    session.Disconnect = disconnect;
                    bindings.Add(binding, session);
                    result = (RotateToken(session), binding);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }

            try
            {
                revoke?.Invoke();
                return result;
            }
            catch
            {
                ClearResponse(result.Response);
                throw;
            }
        }

        internal bool IsCurrent(long binding)
        {
            lock (gate)
            {
                return bindings.TryGetValue(binding, out Session? session) && Now < session.Deadline;
            }
        }

        internal bool Logout(long binding)
        {
            lock (gate)
            {
                if (!bindings.TryGetValue(binding, out Session? session) || Now >= session.Deadline)
                {
                    return false;
                }

                Remove(session);
                return true;
            }
        }

        internal void Detach(long binding)
        {
            lock (gate)
            {
                if (bindings.TryGetValue(binding, out Session? session))
                {
                    session.Disconnect = null;
                }
            }
        }

        private long NextBinding()
        {
            if (nextBinding == long.MaxValue)
            {
                throw new InvalidOperationException("Lobby binding generation exhausted.");
            }

            return ++nextBinding;
        }

        private bool AllowAttempt(Session session)
        {
            long window = Now / 1000;
            if (session.AttemptWindow != window)
            {
                session.AttemptWindow = window;
                session.Attempts = 0;
            }

            return ++session.Attempts <= options.MaxLoginAttemptsPerSecond;
        }

        private static LoginResponse RotateToken(Session session)
        {
            byte[] token = RandomNumberGenerator.GetBytes(32);
            CryptographicOperations.ZeroMemory(session.ResumeDigest);
            session.ResumeDigest = SHA256.HashData(token);
            uint ttl = checked((uint)Math.Max(0, session.Deadline - Now));
            return new LoginResponse
            {
                ResultCode = ResultCode.Success,
                AccountId = session.AccountId,
                AccountSessionId = (byte[])session.Id.Clone(),
                AuthEpoch = session.Epoch,
                ResumeToken = token,
                ResumeExpiresAtUtcMs = session.ExpiresUtc,
                AccountExpiresAtUtcMs = session.ExpiresUtc,
                ResumeRemainingTtlMs = ttl,
                AccountRemainingTtlMs = ttl
            };
        }

        private static LoginResponse Failure() => new LoginResponse { ResultCode = ResultCode.InvalidCredentials };

        internal static void ClearResponse(LoginResponse response)
        {
            CryptographicOperations.ZeroMemory(response.ResumeToken);
            CryptographicOperations.ZeroMemory(response.AccountSessionId);
        }

        private void Remove(Session session)
        {
            accounts.Remove(session.AccountId);
            sessions.Remove(Convert.ToHexString(session.Id));
            bindings.Remove(session.Binding);
            session.Disconnect = null;
            CryptographicOperations.ZeroMemory(session.Id);
            CryptographicOperations.ZeroMemory(session.ResumeDigest);
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                foreach (Session session in accounts.Values.ToArray())
                {
                    Remove(session);
                }
            }
        }
    }
}
