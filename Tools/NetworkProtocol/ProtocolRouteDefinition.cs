using GameFrame.Network;

namespace GameFrame.ProtocolTools
{
    public sealed record ProtocolRouteDefinition(string Name, uint Opcode, string Kind, NetDelivery Delivery,
        Type Request, Type? Response, int MaxBody, int ResponseMaxBody);
}
