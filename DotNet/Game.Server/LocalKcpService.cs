using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Game.Protocol;
using Game.Protocol.Diagnostics;
using Game.Protocol.Gomoku;
using Game.Protocol.Session;
using Game.Server.Diagnostics;
using GameFrame.Network;

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
        internal static async Task RunAsync(ILoggerFactory logging)
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
                await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, port), CreateApplication(),
                    logger: logging.CreateLogger<KcpListenerHost>());
                await host.RegisterGrantAsync(credentials.Conv, identity, psk, ticket);
                CryptographicOperations.ZeroMemory(psk);
                ServerLog.KcpReady(logging.CreateLogger(typeof(LocalKcpService).FullName!), host.LocalEndpoint!.ToString());
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
            application.Rpc(DiagnosticsRoutes.Echo, async (_, request, token) =>
            {
                if (request.DelayMs > 5000)
                {
                    throw new ServerRequestException(2);
                }

                await Task.Delay((int)request.DelayMs, token);
                return new EchoResponse { Text = request.Text };
            });
            application.Reject(SessionRoutes.Login);
            application.Reject(SessionRoutes.Register);
            application.Reject(SessionRoutes.ResumeLogin);
            application.Reject(SessionRoutes.Logout);
            application.Reject(GomokuRoutes.CreateGomokuRoom);
            application.Reject(GomokuRoutes.JoinGomokuRoom);
            application.Reject(GomokuRoutes.SetGomokuReady);
            application.Reject(GomokuRoutes.StartGomokuGame);
            application.Reject(GomokuRoutes.PlaceGomokuMove);
            application.Reject(GomokuRoutes.LeaveGomokuRoom);
            application.Reject(GomokuRoutes.QueryMyGomokuRoom);
            return application;
        }
    }
}
