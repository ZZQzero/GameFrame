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
        private readonly LocalAccountSettings settings;
        private readonly object gate = new object();
        private readonly Dictionary<IPAddress, (long Window, int Attempts)> attempts = new Dictionary<IPAddress, (long, int)>();

        internal LoginHandlers(AccountSessions accounts, LocalAccountSettings settings)
        {
            settings.Validate();
            this.accounts = accounts;
            this.settings = settings;
        }

        internal ServerSession CreateSession(ServerConnectionContext connection) => new LoginSession(connection, accounts, settings, AdmitLogin);

        internal void Register(ServerApplication application)
        {
            application.Rpc(GameNetRoutes.Login, Login, ClearLoginResponse);
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
                return count <= settings.MaxLoginAttemptsPerSecond;
            }
        }

        private Task<LoginResponse> Login(ServerConnectionContext connection, LoginRequest request, CancellationToken token)
        {
            try
            {
                var result = accounts.Login(request, connection.Disconnect);
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
