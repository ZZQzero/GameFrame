using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;

namespace GameFrame.Server.Network
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
        internal static async Task RunAsync(string credentialsPath, int port)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

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
                await using var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, port), GameNetProtocol.Catalog, HandleAsync);
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

        internal static async Task<OwnedFrame?> HandleAsync(KcpConnectionInfo connection, OwnedFrame frame, FrameMemoryBudget budget, CancellationToken token)
        {
            if (frame.Header.Kind != FrameKind.Request || frame.Header.Opcode != GameNetRoutes.Echo.Opcode)
            {
                throw new NetworkProtocolException("The local KCP transport harness only supports Echo; no world owner is installed.");
            }

            EchoRequest request = GameNetRoutes.Echo.RequestCodec.Decode(frame.Body);
            if (request.DelayMs > 5000)
            {
                throw new NetworkProtocolException("Local Echo delay exceeds 5000ms.");
            }

            await Task.Delay((int)request.DelayMs, token);
            OwnedFrame reply = budget.Allocate(32 + GameNetRoutes.Echo.ResponseMaxBodyBytes);
            try
            {
                var writer = new BoundedFrameWriter(reply);
                GameNetRoutes.Echo.ResponseCodec.Encode(writer, new EchoResponse { Text = request.Text });
                writer.Complete(FrameKind.Response, frame.Header.Opcode, frame.Header.CorrelationId);
                return reply;
            }
            catch
            {
                reply.Dispose();
                throw;
            }
        }
    }
}
