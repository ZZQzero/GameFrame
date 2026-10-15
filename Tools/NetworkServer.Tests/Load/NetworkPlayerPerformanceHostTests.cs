using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Game.Protocol;
using GameFrame.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed class NetworkPlayerPerformanceHostTests
    {
        private sealed class Settings
        {
            public string Transport { get; set; } = "kcp";
            public int Connections { get; set; }
            public bool Push { get; set; }
            public int PushMoves { get; set; }
        }

        private sealed class Session : ServerSession
        {
            private readonly string directory;
            internal ulong Sequence;

            internal Session(string directory)
            {
                this.directory = directory;
            }

            public override async ValueTask ClosedAsync(ServerConnectionContext connection, ServerCloseInfo result)
            {
                if (result.Error != null)
                {
                    // This companion only handles synthetic Echo data; never write grants or message bodies.
                    await File.WriteAllTextAsync(Path.Combine(directory, "server-failure-" + connection.Id + ".json"),
                        JsonSerializer.Serialize(new { result.Reason, ErrorType = result.Error.GetType().FullName,
                            Error = result.Error.Message }));
                }
            }
        }

        [Test]
        [Explicit("Isolated companion for the Release IL2CPP performance Player.")]
        public async Task ServePerformance()
        {
            string directory = Path.GetFullPath(TestContext.Parameters.Get("HostDirectory", ""));
            var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(directory, "performance.json")))!;
            Assert.That(settings.Connections, Is.InRange(1, 4));
            Assert.That(settings.PushMoves, Is.InRange(0, 225));
            Assert.That(settings.Transport, Is.AnyOf("wss", "kcp"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var sessions = new ConcurrentBag<ServerConnectionContext>();
            long calls = 0;
            long pushes = 0;
            var application = new ServerApplication(GameNetProtocol.Catalog, connection =>
            {
                sessions.Add(connection);
                return new Session(directory);
            });
            var moves = Enumerable.Range(0, settings.PushMoves).Select(i => new GomokuMoveData
            {
                Column = i % 15, Row = i / 15, Stone = i % 2 + 1
            }).ToArray();
            application.Rpc(GameNetRoutes.Echo, async (connection, request, cancellation) =>
            {
                Interlocked.Increment(ref calls);
                if (request.DelayMs != 0)
                {
                    await Task.Delay((int)request.DelayMs, cancellation);
                }
                if (settings.Push)
                {
                    await connection.PushAsync(GameNetRoutes.GomokuRoomSnapshot, new GomokuSnapshot
                    {
                        Version = ++((Session)connection.Session).Sequence, Moves = moves
                    });
                    Interlocked.Increment(ref pushes);
                }
                return new EchoResponse { Text = request.Text };
            });
            foreach (NetRoute route in GameNetProtocol.Catalog.Routes)
            {
                if (route.Kind != NetRouteKind.Push && route != GameNetRoutes.Echo)
                {
                    application.Reject(route);
                }
            }

            KcpListenerHost? kcp = null;
            WebApplication? web = null;
            WssServer? wss = null;
            X509Certificate2? certificate = null;
            try
            {
                if (settings.Transport == "kcp")
                {
                    KcpTestLibrary.Load();
                    kcp = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), application);
                    await kcp.Started.WaitAsync(timeout.Token);
                    var grants = new List<object>();
                    for (int index = 0; index < settings.Connections; index++)
                    {
                        byte[] identity = RandomNumberGenerator.GetBytes(16);
                        byte[] psk = RandomNumberGenerator.GetBytes(32);
                        byte[] ticket = RandomNumberGenerator.GetBytes(32);
                        try
                        {
                            uint conv = (uint)index + 1;
                            await kcp.RegisterGrantAsync(conv, identity, psk, ticket);
                            grants.Add(new { Conv = conv, Identity = Convert.ToHexString(identity),
                                Psk = Convert.ToHexString(psk), Ticket = Convert.ToHexString(ticket) });
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(identity);
                            CryptographicOperations.ZeroMemory(psk);
                            CryptographicOperations.ZeroMemory(ticket);
                        }
                    }
                    await PublishReadyAsync(directory, new
                    {
                        Endpoint = "udp://127.0.0.1:" + kcp.LocalEndpoint!.Port, Grants = grants
                    });
                }
                else
                {
                    certificate = X509CertificateLoader.LoadPkcs12FromFile(
                        Environment.GetEnvironmentVariable("GF_PERFORMANCE_CERTIFICATE")!,
                        Environment.GetEnvironmentVariable("GF_PERFORMANCE_CERTIFICATE_PASSWORD"));
                    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
                    builder.Logging.ClearProviders();
                    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
                    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
                    builder.Services.AddSingleton(provider => new WssServer(application, logger: provider.GetRequiredService<ILogger<WssServer>>()));
                    builder.Services.AddHostedService(provider => provider.GetRequiredService<WssServer>());
                    web = builder.Build();
                    wss = web.Services.GetRequiredService<WssServer>();
                    web.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
                    web.MapGet("/network", wss.HandleAsync);
                    await web.StartAsync(timeout.Token);
                    string address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                    await PublishReadyAsync(directory, new
                    {
                        Endpoint = address.Replace("https:", "wss:").Replace("127.0.0.1", "localhost") + "/network"
                    });
                }

                while (!File.Exists(Path.Combine(directory, "stop-performance")))
                {
                    await Task.Delay(20, timeout.Token);
                }
                Assert.That(sessions.Count, Is.EqualTo(settings.Connections));
                foreach (ServerConnectionContext connection in sessions)
                {
                    ServerCloseInfo close = await connection.Completion.WaitAsync(timeout.Token);
                    Assert.That(close.Reason, Is.EqualTo(1));
                    Assert.That(close.Error, Is.Null);
                }
                if (web != null)
                {
                    await web.StopAsync(timeout.Token);
                }
                if (kcp != null)
                {
                    await kcp.DisposeAsync();
                }
                long owned = kcp?.OwnedBytes ?? wss!.OwnedBytes;
                Assert.That(owned, Is.Zero);
                await File.WriteAllTextAsync(Path.Combine(directory, "performance-host-result.json"),
                    JsonSerializer.Serialize(new { Passed = true, Connections = sessions.Count, Calls = calls, Pushes = pushes,
                        OwnedBytes = owned, NativeLiveBytesAvailable = false }));
            }
            finally
            {
                if (web != null)
                {
                    await web.DisposeAsync();
                }
                if (kcp != null)
                {
                    await kcp.DisposeAsync();
                }
                certificate?.Dispose();
            }
        }

        private static async Task PublishReadyAsync(string directory, object ready)
        {
            string temporary = Path.Combine(directory, "ready.tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(ready));
            File.Move(temporary, Path.Combine(directory, "ready.json"));
        }
    }
}
