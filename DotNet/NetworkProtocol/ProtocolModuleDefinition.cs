namespace GameFrame.ProtocolTools
{
    public sealed record ProtocolModuleDefinition(string Name, string Namespace, IReadOnlyList<Type> Types,
        IReadOnlyList<ProtocolRouteDefinition> Routes);
}
