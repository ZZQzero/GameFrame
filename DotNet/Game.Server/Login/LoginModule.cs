using Game.Hotfix.Network.Generated;
using Game.Server.Sessions;
using GameFrame.Server.Network;

namespace Game.Server.Login
{
    internal sealed class LoginModule
    {
        private readonly AccountSessions accounts;
        private readonly AuthenticationService service;
        private readonly LoginOptions options;
        private readonly LoginHandlers handlers;

        internal LoginModule(AccountSessions accounts, AuthenticationService service, LoginOptions options)
        {
            options.Validate();
            this.accounts = accounts;
            this.service = service;
            this.options = options;
            handlers = new LoginHandlers(service);
        }

        internal ServerSession CreateSession(ServerConnectionContext connection, Action<long>? bindingEnded = null) =>
            new LobbySession(connection, accounts, options, service.AdmitConnection, bindingEnded);

        internal void Register(ServerApplication application)
        {
            application.Rpc(GameNetRoutes.Register, handlers.RegisterAsync);
            application.Rpc(GameNetRoutes.Login, handlers.LoginAsync, LoginHandlers.ClearResponse);
            application.Rpc(GameNetRoutes.ResumeLogin, handlers.ResumeAsync, LoginHandlers.ClearResponse);
            application.Rpc(GameNetRoutes.Logout, handlers.LogoutAsync);
        }
    }
}
