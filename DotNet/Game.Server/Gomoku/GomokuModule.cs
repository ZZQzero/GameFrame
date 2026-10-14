using Game.Protocol;
using Game.Server.Sessions;
using GameFrame.Network;

namespace Game.Server.Gomoku
{
    internal static class GomokuModule
    {
        internal static void Register(ServerApplication application, GomokuRooms? rooms)
        {
            Register(application, rooms, GameNetRoutes.CreateGomokuRoom, GomokuOperation.Create);
            Register(application, rooms, GameNetRoutes.JoinGomokuRoom, GomokuOperation.Join);
            Register(application, rooms, GameNetRoutes.SetGomokuReady, GomokuOperation.Ready);
            Register(application, rooms, GameNetRoutes.StartGomokuGame, GomokuOperation.Start);
            Register(application, rooms, GameNetRoutes.PlaceGomokuMove, GomokuOperation.Move);
            Register(application, rooms, GameNetRoutes.LeaveGomokuRoom, GomokuOperation.Leave);
            if (rooms == null)
            {
                application.Reject(GameNetRoutes.QueryMyGomokuRoom);
            }
            else
            {
                application.Rpc(GameNetRoutes.QueryMyGomokuRoom, (connection, query, cancellation) =>
                    Task.FromResult(rooms.Query(((LobbySession)connection.Session).RequireBinding(), connection, query.CommandId)));
            }
        }

        private static void Register(ServerApplication application, GomokuRooms? rooms,
            GameFrame.Network.NetRpc<GomokuCommand, GomokuResponse> route, GomokuOperation operation)
        {
            if (rooms == null)
            {
                application.Reject(route);
                return;
            }

            application.Rpc(route, (connection, command, cancellation) =>
                Task.FromResult(rooms.Execute(((LobbySession)connection.Session).RequireBinding(), connection, operation, command)));
        }
    }
}
