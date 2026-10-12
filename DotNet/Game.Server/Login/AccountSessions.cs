using System.Security.Cryptography;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Server.Network;

namespace Game.Server.Login
{
    internal sealed class AccountSessions : IDisposable
    {
        // The connection owns this lease even after the session indexes remove its binding.
        internal sealed class Binding
        {
            private int closeReason;
            internal long Id { get; }
            private readonly long deadline;
            internal ushort? CloseReason
            {
                get
                {
                    int reason = Volatile.Read(ref closeReason);
                    return reason != 0 ? (ushort)reason : Now >= deadline ? (ushort)4 : null;
                }
            }

            internal Binding(long id, long deadline)
            {
                Id = id;
                this.deadline = deadline;
            }

            internal void Revoke(ushort reason) => Interlocked.CompareExchange(ref closeReason, reason, 0);
        }

        private sealed class Session
        {
            internal ulong AccountId;
            internal ulong Epoch;
            internal required Binding Binding;
            internal byte[] Id = Array.Empty<byte>();
            internal byte[] ResumeDigest = Array.Empty<byte>();
            internal long Deadline;
            internal long ExpiresUtc;
            internal Action<ushort>? Disconnect;
            internal long AttemptWindow;
            internal int Attempts;
        }

        private sealed class AccountState
        {
            internal Session? Session;
            internal ulong LatestEpoch;
            internal ulong Revision;
            internal int PendingLogins;
        }

        private readonly object gate = new object();
        private readonly LoginOptions options;
        private readonly Dictionary<ulong, AccountState> accounts = new Dictionary<ulong, AccountState>();
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private readonly Dictionary<long, Session> bindings = new Dictionary<long, Session>();
        private long nextBinding;
        private bool disposed;
        internal static long Now => NetworkClock.GetMilliseconds();

        internal int TrackedAccountCount
        {
            get
            {
                lock (gate)
                {
                    return accounts.Count;
                }
            }
        }

        internal AccountSessions(LoginOptions options)
        {
            options.Validate();
            this.options = options;
        }

        internal async Task<(LoginResponse Response, Binding? Binding)> LoginAsync(ulong accountId,
            Func<CancellationToken, Task<ulong>> advanceEpoch, Action<ushort> disconnect, CancellationToken cancellation)
        {
            if (accountId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(accountId));
            }

            AccountState state;
            ulong revision;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellation.ThrowIfCancellationRequested();
                foreach (var pair in accounts.Where(pair => pair.Value.Session != null &&
                    Now >= pair.Value.Session.Deadline).ToArray())
                {
                    Remove(pair.Value.Session!, 4);
                }

                if (!accounts.TryGetValue(accountId, out state!))
                {
                    // A pending first login reserves the same slot its committed session will own.
                    if (accounts.Count >= options.MaxSessions)
                    {
                        throw new ServerRequestException(1);
                    }

                    state = new AccountState();
                    accounts.Add(accountId, state);
                }

                state.PendingLogins++;
                revision = state.Revision;
            }

            try
            {
                ulong epoch = await advanceEpoch(cancellation).ConfigureAwait(false);
                Action<ushort>? revoke;
                (LoginResponse Response, Binding? Binding) result;
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    cancellation.ThrowIfCancellationRequested();
                    if (epoch == 0)
                    {
                        throw new InvalidOperationException("Authentication generation must be nonzero.");
                    }

                    if (state.Session != null && Now >= state.Session.Deadline)
                    {
                        Remove(state.Session, 4);
                    }

                    if (state.Revision != revision || epoch <= state.LatestEpoch)
                    {
                        return (Failure(), null);
                    }

                    Session? previous = state.Session;
                    revoke = previous?.Disconnect;
                    if (previous != null)
                    {
                        // A newer login may replace this login too; only revocation invalidates
                        // all in-flight operations, while epoch orders competing login commits.
                        Remove(previous, 2, invalidatePending: false);
                    }

                    long deadline = checked(Now + (long)options.AccountLifetime.TotalMilliseconds);
                    var session = new Session
                    {
                        AccountId = accountId,
                        Epoch = epoch,
                        Binding = new Binding(NextBinding(), deadline),
                        Id = RandomNumberGenerator.GetBytes(16),
                        Deadline = deadline,
                        ExpiresUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)options.AccountLifetime.TotalMilliseconds,
                        Disconnect = disconnect
                    };
                    state.Session = session;
                    state.LatestEpoch = epoch;
                    sessions.Add(Convert.ToHexString(session.Id), session);
                    bindings.Add(session.Binding.Id, session);
                    result = (RotateToken(session), session.Binding);
                }

                try
                {
                    revoke?.Invoke(2);
                    return result;
                }
                catch
                {
                    ClearResponse(result.Response);
                    throw;
                }
            }
            finally
            {
                lock (gate)
                {
                    state.PendingLogins--;
                    ReleaseState(accountId, state);
                }
            }
        }

        internal (LoginResponse Response, Binding? Binding) Resume(ResumeLoginRequest request, Action<ushort> disconnect)
        {
            if (request.AccountSessionId.Length != 16 || request.ResumeToken.Length != 32)
            {
                return (Failure(), null);
            }

            byte[] digest = SHA256.HashData(request.ResumeToken);
            Action<ushort>? revoke;
            (LoginResponse Response, Binding? Binding) result;
            try
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (!sessions.TryGetValue(Convert.ToHexString(request.AccountSessionId), out Session? session) ||
                        Now >= session.Deadline || !AllowAttempt(session) ||
                        !CryptographicOperations.FixedTimeEquals(digest, session.ResumeDigest))
                    {
                        return (Failure(), null);
                    }

                    var binding = new Binding(NextBinding(), session.Deadline);
                    revoke = session.Disconnect;
                    session.Binding.Revoke(2);
                    bindings.Remove(session.Binding.Id);
                    session.Binding = binding;
                    session.Disconnect = disconnect;
                    bindings.Add(binding.Id, session);
                    result = (RotateToken(session), binding);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }

            try
            {
                revoke?.Invoke(2);
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
                return bindings.TryGetValue(binding, out Session? session) && session.Binding.CloseReason == null;
            }
        }

        internal bool Logout(long binding)
        {
            lock (gate)
            {
                if (!bindings.TryGetValue(binding, out Session? session) || session.Binding.CloseReason != null)
                {
                    return false;
                }

                Remove(session, 7);
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

        private void Remove(Session session, ushort reason, bool invalidatePending = true)
        {
            AccountState state = accounts[session.AccountId];
            if (invalidatePending)
            {
                state.Revision = checked(state.Revision + 1);
            }

            session.Binding.Revoke(reason);
            state.Session = null;
            sessions.Remove(Convert.ToHexString(session.Id));
            bindings.Remove(session.Binding.Id);
            session.Disconnect = null;
            CryptographicOperations.ZeroMemory(session.Id);
            CryptographicOperations.ZeroMemory(session.ResumeDigest);
            ReleaseState(session.AccountId, state);
        }

        private void ReleaseState(ulong accountId, AccountState state)
        {
            if (state.Session == null && state.PendingLogins == 0)
            {
                accounts.Remove(accountId);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                foreach (AccountState state in accounts.Values.ToArray())
                {
                    if (state.Session != null)
                    {
                        Remove(state.Session, 7);
                    }
                }
            }
        }
    }
}
