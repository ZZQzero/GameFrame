using System.Net;
using GameFrame.Network.Transport;

namespace GameFrame.Server.Network
{

    public sealed class LocalWssService : IAsyncDisposable
    {
        private sealed class IpQuota
        {
            public int Total;
            public int Unauthenticated;
            public long Window;
            public int LoginAttempts;
            public long LastSeen;
        }

        private readonly object gate = new object();
        private readonly Dictionary<IPAddress, IpQuota> ips = new Dictionary<IPAddress, IpQuota>();
        private readonly HashSet<ServerConnection> connections = new HashSet<ServerConnection>();
        private readonly ServerSettings settings;
        private readonly AccountSessions accounts;
        private readonly ILogger logger;
        private readonly FrameMemoryBudget memory;
        private int unauthenticated;
        private bool stopping;
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

        public LocalWssService(ServerSettings settings, ILogger? logger = null)
        {
            settings.Validate();
            this.settings = settings;
            this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            accounts = new AccountSessions(settings);
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
                if (stopping || connections.Count >= settings.MaxConnections || unauthenticated >= settings.MaxUnauthenticated)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }

                if (!ips.TryGetValue(ip, out IpQuota? quota))
                {
                    if (ips.Count >= 128)
                    {
                        foreach (IPAddress expired in ips.Where(pair => pair.Value.Total == 0 &&
                            AccountSessions.Now - pair.Value.LastSeen > 10000).Select(pair => pair.Key).ToArray())
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

                if (quota.Total >= settings.MaxConnectionsPerIp || quota.Unauthenticated >= settings.MaxUnauthenticatedPerIp)
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }

                quota.Total++;
                quota.Unauthenticated++;
                quota.LastSeen = AccountSessions.Now;
                unauthenticated++;
                connection = new ServerConnection(settings, accounts, new FrameMemoryBudget(settings.MaxConnectionOwnedBytes, memory),
                    () => AdmitLogin(ip), () => Authenticated(ip), context.RequestAborted, logger);
                connections.Add(connection);
            }

            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await connection.RunAsync(socket);
            }
            finally
            {
                connection.Finish();
                lock (gate)
                {
                    connections.Remove(connection);
                    IpQuota quota = ips[ip];
                    quota.Total--;
                    quota.LastSeen = AccountSessions.Now;
                    if (!connection.WasAuthenticated)
                    {
                        quota.Unauthenticated--;
                        unauthenticated--;
                    }
                }
            }
        }

        private bool AdmitLogin(IPAddress ip)
        {
            lock (gate)
            {
                IpQuota quota = ips[ip];
                long window = AccountSessions.Now / 1000;
                if (quota.Window != window)
                {
                    quota.Window = window;
                    quota.LoginAttempts = 0;
                }

                quota.LastSeen = AccountSessions.Now;
                return ++quota.LoginAttempts <= settings.MaxLoginAttemptsPerSecond;
            }
        }

        private void Authenticated(IPAddress ip)
        {
            lock (gate)
            {
                ips[ip].Unauthenticated--;
                unauthenticated--;
            }
        }

        public async ValueTask DisposeAsync()
        {
            ServerConnection[] snapshot;
            lock (gate)
            {
                stopping = true;
                snapshot = connections.ToArray();
            }

            foreach (ServerConnection connection in snapshot)
            {
                connection.Stop();
            }

            await Task.WhenAll(snapshot.Select(connection => connection.Finished));
            accounts.Dispose();
        }
    }
}
