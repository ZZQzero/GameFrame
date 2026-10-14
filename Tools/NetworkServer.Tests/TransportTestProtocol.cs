using System.Security.Cryptography;
using System.Text;
using Game.Protocol;
using GameFrame.Network;

namespace GameFrame.Tests
{
    // Transport fixtures must not add unused gameplay contracts to the shipping protocol.
    internal static class TransportTestProtocol
    {
        internal static readonly NetPush<EchoRequest> ReliablePush =
            new NetPush<EchoRequest>(9000, NetDelivery.Reliable, 2048, GameNetRoutes.Echo.RequestCodec);
        internal static readonly NetPush<EchoRequest> UnreliablePush =
            new NetPush<EchoRequest>(9001, NetDelivery.Unreliable, 1118, GameNetRoutes.Echo.RequestCodec);
        internal static readonly NetSend<EchoRequest> ReliableSend =
            new NetSend<EchoRequest>(9002, 2048, GameNetRoutes.Echo.RequestCodec);
        internal static readonly NetUnreliable<EchoRequest> UnreliableSend =
            new NetUnreliable<EchoRequest>(9003, 1118, GameNetRoutes.Echo.RequestCodec);
        internal static readonly NetProtocolCatalog Catalog = CreateCatalog();

        private static NetProtocolCatalog CreateCatalog()
        {
            byte[] productionHash = new byte[32];
            GameNetProtocol.Catalog.CopyHashTo(productionHash);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToHexString(productionHash) + ":transport-tests-v1"));
            return new NetProtocolCatalog(hash, GameNetProtocol.Catalog.Routes.Concat(new NetRoute[]
            {
                ReliablePush, UnreliablePush, ReliableSend, UnreliableSend
            }).ToArray());
        }
    }
}
