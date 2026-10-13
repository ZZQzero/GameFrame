using Game.Hotfix.Network.Generated;
using Game.Server.Diagnostics;
using Game.Server.Login;
using Game.Server.World;
using GameFrame.Server.Network;

namespace Game.Server
{
    internal sealed class LobbyApplication
    {
        internal ServerApplication Application { get; }

        internal LobbyApplication(LoginModule login)
        {
            Application = new ServerApplication(GameNetProtocol.Catalog, login.CreateSession);
            login.Register(Application);
            EchoModule.Register(Application);
            WorldModule.Register(Application);
        }
    }
}
