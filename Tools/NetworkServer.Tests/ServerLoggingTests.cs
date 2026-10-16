using Game.Protocol.Diagnostics;
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
        private sealed class SensitiveException : Exception
        {
            internal const string Secret = "password-ticket-psk-connection-string-must-not-escape";
            internal int FormattingCalls;

            internal SensitiveException() : base(Secret, new Exception(Secret))
            {
                Data["secret"] = Secret;
            }

            public override string StackTrace
            {
                get
                {
                    FormattingCalls++;
                    return Secret;
                }
            }

            public override string ToString()
            {
                FormattingCalls++;
                return Secret;
            }
        }

        [TestCase(NLogOutputFormat.Text)]
        [TestCase(NLogOutputFormat.Json)]
        public async Task NetworkFaultsNeverPassExceptionPayloadOrCustomFormattingToNLog(NLogOutputFormat format)
        {
            var target = new MemoryTarget("capture");
            await using var output = NLogLogging.Create(new LoggingOptions
            {
                Service = "test", Environment = "test", InstanceId = "redaction", Version = "1"
            }, new NLogOutputOptions { Format = format }, target);
            var error = new SensitiveException();
            try
            {
                throw error;
            }
            catch (SensitiveException failure)
            {
                NetworkLog.Failure(output.Factory.CreateLogger<WssServer>(), LogLevel.Error,
                    NetworkLog.ConnectionFailure, failure, NetworkTransportKind.WebSocket, 7, 2, "Dispatch", 101, 42);
            }

            await output.DisposeAsync();
            Assert.That(error.FormattingCalls, Is.Zero);
            Assert.That(target.Logs, Has.Count.EqualTo(1));
            Assert.That(target.Logs[0], Does.Not.Contain(SensitiveException.Secret));
            Assert.That(target.Logs[0], Does.Contain(nameof(SensitiveException)));
            Assert.That(target.Logs[0], Does.Contain(nameof(NetworkFaultsNeverPassExceptionPayloadOrCustomFormattingToNLog)));
            if (format == NLogOutputFormat.Json)
            {
                using var json = System.Text.Json.JsonDocument.Parse(target.Logs[0]);
                Assert.That(json.RootElement.GetProperty("ConnectionId").GetUInt64(), Is.EqualTo(7UL));
                Assert.That(json.RootElement.GetProperty("CorrelationId").GetUInt64(), Is.EqualTo(42UL));
            }
        }

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
                ServerCloseTests.Application((_, _, _) => Task.FromResult(new Game.Protocol.Diagnostics.EchoResponse())), new WssServerOptions(), certificate,
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
