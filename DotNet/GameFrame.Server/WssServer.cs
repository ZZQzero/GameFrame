using System.Net;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFrame.Network
{
    public sealed class WssServer : IAsyncDisposable, IHostedLifecycleService
    {
        private sealed class IpQuota
        {
            internal int Total;
            internal int Restricted;
            internal long LastSeen;
        }

        private readonly object gate = new object();
        private readonly Dictionary<IPAddress, IpQuota> ips = new Dictionary<IPAddress, IpQuota>();
        private readonly HashSet<ServerConnection> connections = new HashSet<ServerConnection>();
        private readonly WssServerOptions settings;
        private readonly ServerApplication application;
        private readonly ILogger logger;
        private readonly FrameMemoryBudget memory;
        private int restricted;
        private ulong sequence;
        private bool stopping;
        private Task? shutdown;
        public long OwnedBytes => memory.OwnedBytes;
        public int ConnectionCount
        {
            get
            {
                lock (gate)
                {
                    return connections.Count;
                }
            }
        }

        public WssServer(ServerApplication application, WssServerOptions? options = null, ILogger? logger = null)
        {
            this.application = application ?? throw new ArgumentNullException(nameof(application));
            settings = (options ?? new WssServerOptions()).Snapshot();
            application.Freeze();
            this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            memory = new FrameMemoryBudget(settings.MaxGlobalOwnedBytes);
        }

        public async Task HandleAsync(HttpContext context)
        {
            if (!context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest ||
                context.Request.QueryString.HasValue || context.Request.Headers.ContainsKey("Authorization"))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            string origin = context.Request.Headers.Origin.ToString();
            if (origin.Length != 0 && !settings.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            IPAddress ip = context.Connection.RemoteIpAddress ?? IPAddress.None;
            ServerConnection connection;
            lock (gate)
            {
                if (stopping || connections.Count >= settings.MaxConnections || restricted >= settings.MaxRestrictedConnections)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }

                if (!ips.TryGetValue(ip, out IpQuota? quota))
                {
                    if (ips.Count >= 128)
                    {
                        long now = NetworkClock.GetMilliseconds();
                        foreach (IPAddress expired in ips.Where(pair => pair.Value.Total == 0 &&
                            now - pair.Value.LastSeen > 10000).Select(pair => pair.Key).ToArray())
                        {
                            ips.Remove(expired);
                        }

                        if (ips.Count >= 128)
                        {
                            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                            return;
                        }
                    }

                    quota = new IpQuota();
                    ips.Add(ip, quota);
                }

                if (quota.Total >= settings.MaxConnectionsPerIp || quota.Restricted >= settings.MaxRestrictedConnectionsPerIp)
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }

                // Construction precedes quota transfer and does not invoke application callbacks.
                connection = new ServerConnection(settings, application,
                    new FrameMemoryBudget(settings.MaxConnectionOwnedBytes, memory), () => Promoted(ip),
                    context.RequestAborted, logger, checked(++sequence), new IPEndPoint(ip, context.Connection.RemotePort));
                connections.Add(connection);
                quota.Total++;
                quota.Restricted++;
                restricted++;
                quota.LastSeen = NetworkClock.GetMilliseconds();
            }

            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await connection.RunAsync(socket);
            }
            finally
            {
                connection.Context.EndTraffic();
                try
                {
                    lock (gate)
                    {
                        connections.Remove(connection);
                        IpQuota quota = ips[ip];
                        quota.Total--;
                        quota.LastSeen = NetworkClock.GetMilliseconds();
                        if (!connection.WasPromoted)
                        {
                            quota.Restricted--;
                            restricted--;
                        }
                    }
                }
                finally
                {
                    connection.Finish();
                }
            }
        }

        private void Promoted(IPAddress ip)
        {
            lock (gate)
            {
                ips[ip].Restricted--;
                restricted--;
            }
        }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        // Host invokes every StoppingAsync before any hosted service StopAsync, including Kestrel.
        public Task StoppingAsync(CancellationToken cancellationToken) => DisposeAsync().AsTask();
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => DisposeAsync().AsTask();

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? source = null;
            ServerConnection[]? snapshot = null;
            lock (gate)
            {
                if (shutdown == null)
                {
                    stopping = true;
                    snapshot = connections.ToArray();
                    source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    shutdown = source.Task;
                }
            }

            if (source != null)
            {
                _ = ShutdownAsync(snapshot!, source);
            }

            return new ValueTask(shutdown);
        }

        private async Task ShutdownAsync(ServerConnection[] snapshot, TaskCompletionSource result)
        {
            try
            {
                foreach (ServerConnection connection in snapshot)
                {
                    connection.Stop();
                }

                await Task.WhenAll(snapshot.Select(connection => connection.Finished)).ConfigureAwait(false);
                Exception? cleanupFailure = snapshot.Select(connection => connection.CleanupFailure).FirstOrDefault(error => error != null);
                if (cleanupFailure != null)
                {
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                }

                result.TrySetResult();
            }
            catch (Exception error)
            {
                result.TrySetException(error);
            }
        }
    }
}
