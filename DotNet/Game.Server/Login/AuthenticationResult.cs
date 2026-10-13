using System.Security.Cryptography;

namespace Game.Server.Login
{
    internal enum AuthenticationStatus
    {
        Success,
        InvalidInput,
        InvalidCredentials,
        AccountAlreadyExists,
        SessionRevoked,
        Overloaded
    }

    internal enum AccountSessionEndReason
    {
        Replaced = 1,
        Expired,
        Revoked
    }

    // The caller owns the credential arrays. A handler transfers them to its response;
    // the framework clears that response after encoding, including encoding failures.
    internal sealed class AuthenticationResult : IDisposable
    {
        internal AuthenticationStatus Status { get; init; }
        internal AccountSessions.Binding? Binding { get; init; }
        internal byte[] AccountSessionId { get; init; } = Array.Empty<byte>();
        internal byte[] ResumeToken { get; init; } = Array.Empty<byte>();
        internal long ExpiresAtUtcMs { get; init; }
        internal uint RemainingTtlMs { get; init; }

        internal static AuthenticationResult Failure(AuthenticationStatus status = AuthenticationStatus.InvalidCredentials) =>
            new AuthenticationResult { Status = status };

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(ResumeToken);
            CryptographicOperations.ZeroMemory(AccountSessionId);
        }
    }
}
