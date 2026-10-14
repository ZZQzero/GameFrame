using Game.Protocol;
using Game.Server.Sessions;
using GameFrame.Network;

namespace Game.Server.Diagnostics
{
    internal static class EchoModule
    {
        internal static void Register(ServerApplication application)
        {
            application.Rpc(GameNetRoutes.Echo, EchoAsync);
        }

        private static async Task<EchoResponse> EchoAsync(ServerConnectionContext connection, EchoRequest request, CancellationToken token)
        {
            var session = (LobbySession)connection.Session;
            session.RequireIdentity();
            if (request.DelayMs > 5000)
            {
                throw new ServerRequestException(2);
            }

            await Task.Delay((int)request.DelayMs, token);
            session.RequireIdentity();
            return new EchoResponse { Text = request.Text };
        }
    }
}
