using System.Net;
using System.Security.Cryptography;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Server.Network;

namespace Game.Server.Login
{
    internal sealed class LoginHandlers
    {
        private readonly AccountSessions accounts;
        private readonly AccountService service;
        private readonly LoginOptions options;
        private readonly object gate = new object();
        private readonly Dictionary<IPAddress, (long Window, int Attempts)> attempts = new Dictionary<IPAddress, (long, int)>();
        private readonly Dictionary<string, (long Window, int Attempts)> accountAttempts = new Dictionary<string, (long, int)>(StringComparer.Ordinal);

        internal LoginHandlers(AccountSessions accounts, AccountService service, LoginOptions options)
        {
            options.Validate();
            this.accounts = accounts;
            this.service = service;
            this.options = options;
        }

        internal ServerSession CreateSession(ServerConnectionContext connection) => new LoginSession(connection, accounts, options, AdmitLogin);

        internal void Register(ServerApplication application)
        {
            application.Rpc(GameNetRoutes.Register, RegisterAccountAsync);
            application.Rpc(GameNetRoutes.Login, LoginAsync, ClearLoginResponse);
            application.Rpc(GameNetRoutes.ResumeLogin, Resume, ClearLoginResponse);
            application.Rpc(GameNetRoutes.Logout, Logout);
        }

        private bool AdmitLogin(IPAddress ip)
        {
            lock (gate)
            {
                long window = NetworkClock.GetMilliseconds() / 1000;
                if (!attempts.TryGetValue(ip, out var value))
                {
                    if (attempts.Count >= 128)
                    {
                        foreach (IPAddress expired in attempts.Where(pair => window - pair.Value.Window > 10).Select(pair => pair.Key).ToArray())
                        {
                            attempts.Remove(expired);
                        }

                        if (attempts.Count >= 128)
                        {
                            return false;
                        }
                    }
                }

                int count = value.Window == window ? value.Attempts + 1 : 1;
                attempts[ip] = (window, count);
                return count <= options.MaxLoginAttemptsPerSecond;
            }
        }

        private bool AdmitAccount(string account)
        {
            lock (gate)
            {
                long window = NetworkClock.GetMilliseconds() / 1000;
                if (!accountAttempts.TryGetValue(account, out var value))
                {
                    if (accountAttempts.Count >= 128)
                    {
                        foreach (string expired in accountAttempts.Where(pair => window - pair.Value.Window > 10).Select(pair => pair.Key).ToArray())
                        {
                            accountAttempts.Remove(expired);
                        }

                        if (accountAttempts.Count >= 128)
                        {
                            return false;
                        }
                    }
                }

                int count = value.Window == window ? value.Attempts + 1 : 1;
                accountAttempts[account] = (window, count);
                return count <= options.MaxLoginAttemptsPerSecond;
            }
        }

        private async Task<OperationResponse> RegisterAccountAsync(ServerConnectionContext connection, RegisterRequest request, CancellationToken token)
        {
            try
            {
                if (!AdmitAccount(request.Account))
                {
                    throw new ServerRequestException(3);
                }

                ResultCode result = await service.RegisterAsync(request.Account, request.Password, token);
                return new OperationResponse { ResultCode = result };
            }
            finally
            {
                request.Password = string.Empty;
                ((LoginSession)connection.Session).FinishRegistration();
            }
        }

        private async Task<LoginResponse> LoginAsync(ServerConnectionContext connection, LoginRequest request, CancellationToken token)
        {
            try
            {
                if (!AdmitAccount(request.Account))
                {
                    throw new ServerRequestException(3, 5);
                }

                AuthenticatedAccount? account = await service.LoginAsync(request.Account, request.Password, token);
                token.ThrowIfCancellationRequested();
                var result = account.HasValue ? accounts.Login(account.Value, connection.Disconnect, token) :
                    (Response: new LoginResponse { ResultCode = ResultCode.InvalidCredentials }, Binding: 0L);
                try
                {
                    ((LoginSession)connection.Session).Bind(result.Binding);
                    return result.Response;
                }
                catch
                {
                    ClearLoginResponse(result.Response);
                    throw;
                }
            }
            finally
            {
                request.Password = string.Empty;
            }
        }

        private Task<LoginResponse> Resume(ServerConnectionContext connection, ResumeLoginRequest request, CancellationToken token)
        {
            try
            {
                var result = accounts.Resume(request, connection.Disconnect);
                try
                {
                    ((LoginSession)connection.Session).Bind(result.Binding);
                    return Task.FromResult(result.Response);
                }
                catch
                {
                    ClearLoginResponse(result.Response);
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(request.ResumeToken);
                CryptographicOperations.ZeroMemory(request.AccountSessionId);
            }
        }

        private static void ClearLoginResponse(LoginResponse response)
        {
            CryptographicOperations.ZeroMemory(response.ResumeToken);
            CryptographicOperations.ZeroMemory(response.AccountSessionId);
        }

        private Task<OperationResponse> Logout(ServerConnectionContext connection, LogoutRequest request, CancellationToken token)
        {
            var session = (LoginSession)connection.Session;
            session.BeginLogout();
            bool revoked = accounts.Logout(session.Binding);
            connection.CloseAfterResponse(1);
            return Task.FromResult(new OperationResponse { ResultCode = revoked ? ResultCode.Success : ResultCode.SessionRevoked });
        }
    }
}
