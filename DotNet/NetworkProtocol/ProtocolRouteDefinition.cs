using GameFrame.Network;

namespace GameFrame.ProtocolTools
{
    public sealed record ProtocolRouteDefinition(string Name, string MemberName, string Source, uint Opcode,
        string Kind, NetDelivery Delivery, Type Request, Type? Response);
}
