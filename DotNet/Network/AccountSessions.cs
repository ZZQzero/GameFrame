using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Game.Hotfix.Network.Protocol;

namespace GameFrame.Server.Network
{

    internal sealed class AccountSessions : IDisposable
    {
        private readonly object gate = new object();
        private readonly ServerSettings settings;
        private byte[] sessionId = Array.Empty<byte>();
        private byte[] resumeDigest = Array.Empty<byte>();
        private ulong epoch;
        private long deadline;
        private long expiresUtc;
        private bool active;
        private Action? disconnectLobby;
        private long lobbyBinding;
        private long loginWindow;
        private int loginAttempts;

        public AccountSessions(ServerSettings settings)
        {
            this.settings = settings;
        }

        internal static long Now => (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;

        internal (LoginResponse Response, long Binding) Login(LoginRequest request, Action disconnect)
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(request.Password);
            byte[] digest;
            try
            {
                digest = SHA256.HashData(passwordBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            Action? revoke = null;
            (LoginResponse, long) result;
            try
            {
                lock (gate)
                {
                    if (!AllowAttempt() || request.Account != settings.Account ||
                        !CryptographicOperations.FixedTimeEquals(digest, settings.PasswordDigest))
                    {
                        return (Failure(), 0);
                    }

                    if (epoch == ulong.MaxValue || lobbyBinding == long.MaxValue)
                    {
                        throw new InvalidOperationException("Account generation exhausted.");
                    }

                    revoke = disconnectLobby;
                    ClearCredentials();
                    sessionId = RandomNumberGenerator.GetBytes(16);
                    epoch++;
                    active = true;
                    deadline = checked(Now + (long)settings.AccountLifetime.TotalMilliseconds);
                    expiresUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)settings.AccountLifetime.TotalMilliseconds;
                    disconnectLobby = disconnect;
                    lobbyBinding++;
                    result = (RotateToken(), lobbyBinding);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }

            revoke?.Invoke();
            return result;
        }

        internal (LoginResponse Response, long Binding) Resume(ResumeLoginRequest request, Action disconnect)
        {
            if (request.AccountSessionId.Length != 16 || request.ResumeToken.Length != 32)
            {
                return (Failure(), 0);
            }

            byte[] digest = SHA256.HashData(request.ResumeToken);
            Action? revoke;
            (LoginResponse, long) result;
            try
            {
                lock (gate)
                {
                    if (!AllowAttempt() || !active || Now >= deadline ||
                        !CryptographicOperations.FixedTimeEquals(request.AccountSessionId, sessionId) ||
                        !CryptographicOperations.FixedTimeEquals(digest, resumeDigest))
                    {
                        return (Failure(), 0);
                    }

                    if (lobbyBinding == long.MaxValue)
                    {
                        throw new InvalidOperationException("Lobby binding generation exhausted.");
                    }

                    revoke = disconnectLobby;
                    disconnectLobby = disconnect;
                    lobbyBinding++;
                    // Consume and rotate under the account gate; no IO is performed in this critical section.
                    result = (RotateToken(), lobbyBinding);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }

            revoke?.Invoke();
            return result;
        }

        internal bool IsCurrent(long binding)
        {
            lock (gate)
            {
                return active && binding != 0 && binding == lobbyBinding && Now < deadline;
            }
        }

        internal bool Logout(long binding)
        {
            lock (gate)
            {
                if (!active || binding != lobbyBinding || Now >= deadline)
                {
                    return false;
                }

                active = false;
                disconnectLobby = null;
                ClearCredentials();
                return true;
            }
        }

        internal void Detach(long binding)
        {
            lock (gate)
            {
                if (binding == lobbyBinding)
                {
                    disconnectLobby = null;
                }
            }
        }

        private bool AllowAttempt()
        {
            long window = Now / 1000;
            if (loginWindow != window)
            {
                loginWindow = window;
                loginAttempts = 0;
            }

            return ++loginAttempts <= settings.MaxLoginAttemptsPerSecond;
        }

        private LoginResponse RotateToken()
        {
            byte[] token = RandomNumberGenerator.GetBytes(32);
            CryptographicOperations.ZeroMemory(resumeDigest);
            resumeDigest = SHA256.HashData(token);
            uint ttl = checked((uint)Math.Max(0, deadline - Now));
            return new LoginResponse
            {
                ResultCode = ResultCode.Success,
                AccountId = 1,
                AccountSessionId = (byte[])sessionId.Clone(),
                AuthEpoch = epoch,
                ResumeToken = token,
                ResumeExpiresAtUtcMs = expiresUtc,
                AccountExpiresAtUtcMs = expiresUtc,
                ResumeRemainingTtlMs = ttl,
                AccountRemainingTtlMs = ttl
            };
        }

        private static LoginResponse Failure() => new LoginResponse { ResultCode = ResultCode.InvalidCredentials };

        private void ClearCredentials()
        {
            CryptographicOperations.ZeroMemory(sessionId);
            CryptographicOperations.ZeroMemory(resumeDigest);
            sessionId = Array.Empty<byte>();
            resumeDigest = Array.Empty<byte>();
        }

        public void Dispose()
        {
            lock (gate)
            {
                active = false;
                disconnectLobby = null;
                ClearCredentials();
            }
        }
    }
}
