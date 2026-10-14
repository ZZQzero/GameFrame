using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Game.Server;
using Game.Server.Configuration;
using GameFrame.Logging;
using GameFrame.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog.Targets;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class ServerLoggingTests
    {
        [Test]
        public async Task NLogRemainsUsableAfterTheWssHostReleasesItsResources()
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            int port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            var target = new MemoryTarget("capture");
            await using NLogLogging output = NLogLogging.Create(new LoggingOptions
            {
                Service = "test",
                Environment = "test",
                InstanceId = "host-lifetime",
                Version = "1"
            }, new NLogOutputOptions(), target);
            await using (WebApplication app = LocalWssService.CreateHost(
                ServerCloseTests.Application((_, _, _) => Task.FromResult(new Game.Protocol.EchoResponse())), new WssServerOptions(), certificate,
                port, TableConfigLoader.Load(null), output.Factory))
            {
                Assert.That(app.Services.GetRequiredService<ILoggerFactory>(), Is.SameAs(output.Factory));
                WssServer network = app.Services.GetRequiredService<WssServer>();
                LocalWssService.ConfigurePipeline(app, network);
                await app.StartAsync(guard.Token);
                await app.StopAsync(guard.Token);
            }

            output.Factory.CreateLogger("ApplicationCleanup").LogInformation("Resources released after host disposal");
            await output.DisposeAsync();
            Assert.That(target.Logs.Count(entry => entry.Contains("Resources released after host disposal", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(output.OutputErrorCount, Is.Zero);
        }
    }
}
