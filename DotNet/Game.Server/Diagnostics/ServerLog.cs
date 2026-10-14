namespace Game.Server.Diagnostics
{
    internal static partial class ServerLog
    {
        [LoggerMessage(2000, LogLevel.Information, "Server starting with profile {Profile}.")]
        internal static partial void Starting(ILogger logger, string profile);

        [LoggerMessage(2001, LogLevel.Information, "Server stopped. Discarded log events: {DroppedCount}.")]
        internal static partial void Stopped(ILogger logger, long droppedCount);

        // Exception messages may contain credentials or third-party configuration values.
        [LoggerMessage(2002, LogLevel.Critical, "Server failed during {Stage}: {ErrorType}. {StackTrace}")]
        internal static partial void Failed(ILogger logger, string stage, string errorType, string? stackTrace);

        [LoggerMessage(2003, LogLevel.Information, "KCP/DTLS listening on {Endpoint}; the local grant expires in 30 seconds.")]
        internal static partial void KcpReady(ILogger logger, string endpoint);

        [LoggerMessage(2004, LogLevel.Information, "WSS listening on localhost:{Port}.")]
        internal static partial void WssReady(ILogger logger, int port);
    }
}
