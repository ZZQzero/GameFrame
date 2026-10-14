using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Server.Network;

namespace Game.Server
{
    internal static class LocalKcpService
    {
        private sealed class Credentials
        {
            public uint Conv { get; set; }
            public string Identity { get; set; } = string.Empty;
            public string Psk { get; set; } = string.Empty;
            public string Ticket { get; set; } = string.Empty;
        }

        // Explicit local transport harness. This does not issue world/account authorization.
        internal static async Task RunAsync()
        {
            string credentialsPath = LocalEnvironment.Required("GF_LOCAL_KCP_GRANT");
            int port = LocalEnvironment.Port("GF_LOCAL_KCP_PORT", 7450);
            byte[] file = await File.ReadAllBytesAsync(credentialsPath);
            Credentials credentials;
            try
            {
                credentials = JsonSerializer.Deserialize<Credentials>(file) ?? throw new ArgumentException("Missing KCP credentials.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(file);
            }

            byte[] identity = Array.Empty<byte>();
            byte[] psk = Array.Empty<byte>();
            byte[] ticket = Array.Empty<byte>();
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, args) =>
            {
                args.Cancel = true;
                stop.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                identity = Convert.FromHexString(credentials.Identity);
                psk = Convert.FromHexString(credentials.Psk);
                ticket = Convert.FromHexString(credentials.Ticket);
                credentials.Identity = credentials.Psk = credentials.Ticket = string.Empty;
                using ILoggerFactory logging = LoggerFactory.Create(builder => builder.AddSimpleConsole());
                await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, port), CreateApplication(),
                    logger: logging.CreateLogger<KcpListenerHost>());
                await host.RegisterGrantAsync(credentials.Conv, identity, psk, ticket);
                CryptographicOperations.ZeroMemory(psk);
                Console.WriteLine($"Local KCP/DTLS listening on {host.LocalEndpoint}; the explicitly configured grant expires in 30 seconds. Ctrl+C stops the listener.");
                Task canceled = Task.Delay(Timeout.Infinite, stop.Token);
                await Task.WhenAny(canceled, host.Stopped);
                if (host.Stopped.IsCompleted)
                {
                    await host.Stopped;
                }
            }
            finally
            {
                credentials.Identity = credentials.Psk = credentials.Ticket = string.Empty;
                Console.CancelKeyPress -= cancel;
                CryptographicOperations.ZeroMemory(identity);
                CryptographicOperations.ZeroMemory(psk);
                CryptographicOperations.ZeroMemory(ticket);
            }
        }

        internal static ServerApplication CreateApplication()
        {
            var application = new ServerApplication(GameNetProtocol.Catalog);
            application.Rpc(GameNetRoutes.Echo, async (_, request, token) =>
            {
                if (request.DelayMs > 5000)
                {
                    throw new ServerRequestException(2);
                }

                await Task.Delay((int)request.DelayMs, token);
                return new EchoResponse { Text = request.Text };
            });
            application.Reject(GameNetRoutes.Login);
            application.Reject(GameNetRoutes.Register);
            application.Reject(GameNetRoutes.ResumeLogin);
            application.Reject(GameNetRoutes.Logout);
            application.Reject(GameNetRoutes.CreateGomokuRoom);
            application.Reject(GameNetRoutes.JoinGomokuRoom);
            application.Reject(GameNetRoutes.SetGomokuReady);
            application.Reject(GameNetRoutes.StartGomokuGame);
            application.Reject(GameNetRoutes.PlaceGomokuMove);
            application.Reject(GameNetRoutes.LeaveGomokuRoom);
            application.Reject(GameNetRoutes.QueryMyGomokuRoom);
            return application;
        }
    }
}
