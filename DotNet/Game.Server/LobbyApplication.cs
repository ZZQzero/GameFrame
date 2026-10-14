using Game.Hotfix.Network.Generated;
using Game.Server.Diagnostics;
using Game.Server.Login;
using Game.Server.Gomoku;
using GameFrame.Server.Network;

namespace Game.Server
{
    internal sealed class LobbyApplication
    {
        internal ServerApplication Application { get; }

        internal LobbyApplication(LoginModule login, GomokuRooms? rooms = null)
        {
            Application = new ServerApplication(GameNetProtocol.Catalog, connection =>
                login.CreateSession(connection, rooms == null ? null : rooms.Detach));
            login.Register(Application);
            EchoModule.Register(Application);
            GomokuModule.Register(Application, rooms);
        }
    }
}
