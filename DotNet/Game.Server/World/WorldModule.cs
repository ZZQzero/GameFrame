using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using Game.Server.Sessions;
using GameFrame.Server.Network;

namespace Game.Server.World
{
    internal static class WorldModule
    {
        internal static void Register(ServerApplication application)
        {
            application.Rpc(GameNetRoutes.RequestWorldGrant, RequestGrantAsync);
            application.Reject(GameNetRoutes.EnterWorld);
            application.Reject(GameNetRoutes.LeaveWorld);
            application.Reject(GameNetRoutes.RestoreWorld);
            application.Reject(GameNetRoutes.MoveInput);
            application.Reject(GameNetRoutes.MoveInputReliable);
        }

        private static Task<WorldGrantResponse> RequestGrantAsync(ServerConnectionContext connection, RequestWorldGrantRequest request, CancellationToken token)
        {
            ((LobbySession)connection.Session).RequireIdentity();
            return Task.FromResult(new WorldGrantResponse { ResultCode = ResultCode.WorldUnavailable });
        }
    }
}
