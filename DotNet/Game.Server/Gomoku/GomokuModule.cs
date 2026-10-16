using Game.Protocol.Gomoku;
using Game.Server.Sessions;
using GameFrame.Network;

namespace Game.Server.Gomoku
{
    internal static class GomokuModule
    {
        internal static void Register(ServerApplication application, GomokuRooms? rooms)
        {
            Register(application, rooms, GomokuRoutes.CreateGomokuRoom, GomokuOperation.Create,
                request => new GomokuRoomCommand(request.CommandId));
            Register(application, rooms, GomokuRoutes.JoinGomokuRoom, GomokuOperation.Join,
                request => new GomokuRoomCommand(request.CommandId, RoomCode: request.RoomCode));
            Register(application, rooms, GomokuRoutes.SetGomokuReady, GomokuOperation.Ready,
                request => new GomokuRoomCommand(request.CommandId, request.RoomId, request.ExpectedVersion, Ready: request.Ready));
            Register(application, rooms, GomokuRoutes.StartGomokuGame, GomokuOperation.Start,
                request => new GomokuRoomCommand(request.CommandId, request.RoomId, request.ExpectedVersion));
            Register(application, rooms, GomokuRoutes.PlaceGomokuMove, GomokuOperation.Move,
                request => new GomokuRoomCommand(request.CommandId, request.RoomId, request.ExpectedVersion,
                    Column: request.Column, Row: request.Row));
            Register(application, rooms, GomokuRoutes.LeaveGomokuRoom, GomokuOperation.Leave,
                request => new GomokuRoomCommand(request.CommandId, request.RoomId));
            if (rooms == null)
            {
                application.Reject(GomokuRoutes.QueryMyGomokuRoom);
            }
            else
            {
                application.Rpc(GomokuRoutes.QueryMyGomokuRoom, (connection, query, cancellation) =>
                    Task.FromResult(rooms.Query(((LobbySession)connection.Session).RequireBinding(), connection, query.CommandId)));
            }
        }

        private static void Register<TRequest>(ServerApplication application, GomokuRooms? rooms,
            NetRpc<TRequest, GomokuResponse> route, GomokuOperation operation, Func<TRequest, GomokuRoomCommand> map)
            where TRequest : class
        {
            if (rooms == null)
            {
                application.Reject(route);
                return;
            }

            application.Rpc(route, (connection, command, cancellation) =>
                Task.FromResult(rooms.Execute(((LobbySession)connection.Session).RequireBinding(), connection, operation, map(command))));
        }
    }
}
