using System.Security.Cryptography;
using Game.Protocol.Common;
using Game.Protocol.Session;
using Game.Server.Sessions;
using GameFrame.Network;

namespace Game.Server.Login
{
    internal sealed class LoginHandlers
    {
        private readonly AuthenticationService service;

        internal LoginHandlers(AuthenticationService service)
        {
            this.service = service;
        }

        internal async Task<OperationResponse> RegisterAsync(ServerConnectionContext connection, RegisterRequest request, CancellationToken token)
        {
            try
            {
                AuthenticationStatus result = await service.RegisterAsync(request.Account, request.Password, token);
                return new OperationResponse { ResultCode = ToResultCode(result) };
            }
            finally
            {
                request.Password = string.Empty;
                ((LobbySession)connection.Session).FinishAuthentication();
            }
        }

        internal async Task<LoginResponse> LoginAsync(ServerConnectionContext connection, LoginRequest request, CancellationToken token)
        {
            var session = (LobbySession)connection.Session;
            try
            {
                AuthenticationResult result = await service.LoginAsync(request.Account, request.Password, session.RequestClose, token);
                return BindAndRespond(session, result);
            }
            catch (ServerRequestException error)
            {
                session.BeginClosing();
                connection.CloseAfterResponse(error.CloseReason == 0 ? (ushort)1 : error.CloseReason);
                throw;
            }
            finally
            {
                request.Password = string.Empty;
                session.FinishAuthentication();
            }
        }

        internal Task<LoginResponse> ResumeAsync(ServerConnectionContext connection, ResumeLoginRequest request, CancellationToken token)
        {
            var session = (LobbySession)connection.Session;
            try
            {
                AuthenticationResult result = service.Resume(request.AccountSessionId, request.ResumeToken, session.RequestClose, token);
                return Task.FromResult(BindAndRespond(session, result));
            }
            finally
            {
                session.FinishAuthentication();
                CryptographicOperations.ZeroMemory(request.ResumeToken);
                CryptographicOperations.ZeroMemory(request.AccountSessionId);
            }
        }

        internal Task<OperationResponse> LogoutAsync(ServerConnectionContext connection, LogoutRequest request, CancellationToken token)
        {
            var session = (LobbySession)connection.Session;
            session.BeginClosing();
            AuthenticationStatus result = service.Logout(session.Binding);
            connection.CloseAfterResponse(1);
            return Task.FromResult(new OperationResponse { ResultCode = ToResultCode(result) });
        }

        private static LoginResponse BindAndRespond(LobbySession session, AuthenticationResult result)
        {
            try
            {
                var response = new LoginResponse
                {
                    ResultCode = ToResultCode(result.Status),
                    AccountId = result.Binding?.Identity.AccountId ?? 0,
                    AuthEpoch = result.Binding?.Identity.AuthEpoch ?? 0,
                    AccountSessionId = result.AccountSessionId,
                    ResumeToken = result.ResumeToken,
                    ResumeExpiresAtUtcMs = result.ExpiresAtUtcMs,
                    AccountExpiresAtUtcMs = result.ExpiresAtUtcMs,
                    ResumeRemainingTtlMs = result.RemainingTtlMs,
                    AccountRemainingTtlMs = result.RemainingTtlMs
                };
                session.Bind(result.Binding);
                return response;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static ResultCode ToResultCode(AuthenticationStatus status) => status switch
        {
            AuthenticationStatus.Success => ResultCode.Success,
            AuthenticationStatus.InvalidInput => ResultCode.InvalidInput,
            AuthenticationStatus.InvalidCredentials => ResultCode.InvalidCredentials,
            AuthenticationStatus.AccountAlreadyExists => ResultCode.AccountAlreadyExists,
            AuthenticationStatus.SessionRevoked => ResultCode.SessionRevoked,
            AuthenticationStatus.Overloaded => throw new ServerRequestException(1),
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        internal static void ClearResponse(LoginResponse response)
        {
            CryptographicOperations.ZeroMemory(response.ResumeToken);
            CryptographicOperations.ZeroMemory(response.AccountSessionId);
        }
    }
}
