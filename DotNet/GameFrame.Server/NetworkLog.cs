using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GameFrame.Network
{
    // Events belong to the network boundary. Never pass application exceptions to a provider:
    // Message, InnerException, Data and custom ToString implementations may contain credentials.
    internal static class NetworkLog
    {
        internal static readonly EventId ConnectionFailure = new EventId(1000, "NetworkConnectionFailure");
        internal static readonly EventId ConnectionCleanupFailure = new EventId(1001, "NetworkConnectionCleanupFailure");
        internal static readonly EventId ListenerFailure = new EventId(1002, "NetworkListenerFailure");
        internal static readonly EventId ListenerCleanupFailure = new EventId(1003, "NetworkListenerCleanupFailure");

        internal static void Failure(ILogger logger, LogLevel level, EventId eventId, Exception error,
            NetworkTransportKind transport, ulong connectionId, ulong generation, string stage,
            uint opcode = 0, ulong correlationId = 0, ushort closeReason = 0)
        {
            if (!logger.IsEnabled(level))
            {
                return;
            }

            // Build frames directly, without invoking an exception's overridable StackTrace property.
            string stack = new StackTrace(error, false).ToString();
            logger.Log(level, eventId,
                "Network failure: {Transport} {ConnectionId}/{Generation}, stage {Stage}, opcode {Opcode}, " +
                "correlation {CorrelationId}, close {CloseReason}, type {ExceptionType}, stack {ExceptionStack}",
                transport, connectionId, generation, stage, opcode, correlationId, closeReason,
                error.GetType().FullName, stack);
        }
    }
}
