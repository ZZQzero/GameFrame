using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using Game.Server.Login;
using GameFrame.Server.Network;

namespace Game.Server
{
    internal sealed class LobbyApplication
    {
        private readonly AccountSessions accounts;
        internal ServerApplication Application { get; }

        internal LobbyApplication(AccountSessions accounts, AccountService service, LoginOptions options)
        {
            this.accounts = accounts;
            var login = new LoginHandlers(accounts, service, options);
            Application = new ServerApplication(GameNetProtocol.Catalog, login.CreateSession);
            login.Register(Application);
            Application.Rpc(GameNetRoutes.Echo, Echo);
            Application.Rpc(GameNetRoutes.RequestWorldGrant, (_, _, _) => Task.FromResult(new WorldGrantResponse { ResultCode = ResultCode.WorldUnavailable }));
            Application.Reject(GameNetRoutes.EnterWorld);
            Application.Reject(GameNetRoutes.LeaveWorld);
            Application.Reject(GameNetRoutes.RestoreWorld);
            Application.Reject(GameNetRoutes.MoveInput);
            Application.Reject(GameNetRoutes.MoveInputReliable);
        }

        private async Task<EchoResponse> Echo(ServerConnectionContext connection, EchoRequest request, CancellationToken token)
        {
            if (request.DelayMs > 5000)
            {
                throw new ServerRequestException(2);
            }

            await Task.Delay((int)request.DelayMs, token);
            if (!accounts.IsCurrent(((LoginSession)connection.Session).Binding))
            {
                throw new ServerRequestException(2, 5);
            }

            return new EchoResponse { Text = request.Text };
        }
    }
}
