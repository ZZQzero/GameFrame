using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using Game.Server;
using Game.Server.Login;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Server.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class WssLifecycleTests
    {
        internal sealed class LogCapture : ILoggerProvider
        {
            internal readonly ConcurrentQueue<(string Category, LogLevel Level, Exception? Error, string Text)> Entries = new();
            private sealed class Logger : ILogger
            {
                private readonly LogCapture owner;
                private readonly string category;
                internal Logger(LogCapture owner, string category)
                {
                    this.owner = owner;
                    this.category = category;
                }
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
                    owner.Entries.Enqueue((category, level, error, formatter(state, error)));
            }
            public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
            public void Dispose()
            {
            }
        }

        private X509Certificate2 certificate = null!;
        private WebApplication app = null!;
        private WssServer network = null!;
        private ClientWebSocket socket = null!;
        private readonly FrameMemoryBudget memory = new(1024 * 1024);
        private CancellationTokenSource guard = null!;
        private readonly LogCapture logs = new();

        [SetUp]
        public void Prepare()
        {
            guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        }

        private async Task Start(ServerApplication application)
        {
            var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            int port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            app = LocalWssService.CreateHost(application, new WssServerOptions(), certificate, port);
            app.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            network = app.Services.GetRequiredService<WssServer>();
            LocalWssService.ConfigurePipeline(app, network);
            await app.StartAsync(guard.Token);
            socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == certificate.GetCertHashString();
            await socket.ConnectAsync(new Uri($"wss://localhost:{port}/network"), guard.Token);
            using OwnedFrame hello = memory.Allocate(80);
            ControlCodec.WriteClientHello(hello.Bytes.AsSpan(32), GameNetProtocol.Catalog.Hash, 32768, 0, 0, ReadOnlySpan<byte>.Empty,
                NetworkTransportKind.WebSocket);
            hello.Header = new FrameHeader(FrameKind.Control, (uint)ControlOpcode.ClientHello, 0, 48);
            hello.Length = 80;
            FrameCodec.WriteHeader(hello.Bytes, hello.Header);
            await WebSocketFrameTransport.SendAsync(socket, hello, guard.Token);
            using OwnedFrame response = (await Receive())!;
            Assert.That(response.Header.Opcode, Is.EqualTo((uint)ControlOpcode.ServerHello));
        }

        private Task<OwnedFrame?> Receive() => WebSocketFrameTransport.ReceiveAsync(socket, memory, 32768,
            FrameDirection.ServerToClient, false, guard.Token);

        private async Task Send<TRequest, TResponse>(NetRpc<TRequest, TResponse> route, TRequest dto, ulong correlation = 1)
            where TRequest : class where TResponse : class
        {
            using OwnedFrame frame = memory.Allocate(32 + route.MaxBodyBytes);
            var writer = new BoundedFrameWriter(frame);
            route.RequestCodec.Encode(writer, dto);
            writer.Complete(FrameKind.Request, route.Opcode, correlation);
            await WebSocketFrameTransport.SendAsync(socket, frame, guard.Token);
        }

        private async Task Closed(ushort reason)
        {
            using OwnedFrame close = (await Receive())!;
            Assert.That(close.Header.Opcode, Is.EqualTo((uint)ControlOpcode.Close));
            Assert.That(ControlCodec.ReadClose(close.Body), Is.EqualTo(reason));
            using OwnedFrame? end = await Receive();
            Assert.That(end, Is.Null);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, guard.Token);
            while (network.ConnectionCount != 0)
            {
                await Task.Delay(1, guard.Token);
            }
        }

        [TearDown]
        public async Task Stop()
        {
            socket?.Dispose();
            if (app != null)
            {
                await app.StopAsync(guard.Token);
                await app.DisposeAsync();
                Assert.That(network.OwnedBytes, Is.Zero);
            }

            certificate.Dispose();
            guard.Dispose();
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AuthenticatedConnectionReportsExpiryOrRevocationOnTheWire(bool revoke)
        {
            var options = new LoginOptions { AccountLifetime = TimeSpan.FromMilliseconds(300) };
            using var sessions = new AccountSessions(options);
            using var repository = new TestAccountRepository();
            using var service = new AccountService(repository, sessions);
            await service.RegisterAsync("player", "valid-password", guard.Token);
            await Start(new LobbyApplication(sessions, service, options).Application);
            await Send(GameNetRoutes.Login, new LoginRequest { Account = "player", Password = "valid-password" });
            using (OwnedFrame response = (await Receive())!)
            {
                Assert.That(GameNetRoutes.Login.ResponseCodec.Decode(response.Body).ResultCode, Is.EqualTo(ResultCode.Success));
            }

            if (revoke)
            {
                sessions.Dispose();
            }

            await Closed(revoke ? (ushort)7 : (ushort)4);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AuthenticationOverloadResponsePrecedesCloseOrAllowsAnExplicitRegisterRetry(bool register)
        {
            var repository = new AccountLifecycleTests.BlockedRepository();
            using var sessions = new AccountSessions(new LoginOptions());
            using var service = new AccountService(repository, sessions);
            using var cancellation = new CancellationTokenSource();
            Task[] blocked = Enumerable.Range(0, 4).Select(_ => service.LoginAsync("player", "valid-password", _ => { }, cancellation.Token)).ToArray();
            try
            {
                await Start(new LobbyApplication(sessions, service, new LoginOptions()).Application);
                if (register)
                {
                    await Send(GameNetRoutes.Register, new RegisterRequest { Account = "player", Password = "valid-password" });
                }
                else
                {
                    await Send(GameNetRoutes.Login, new LoginRequest { Account = "player", Password = "valid-password" });
                }

                using (OwnedFrame response = (await Receive())!)
                {
                    Assert.That(response.Header.StatusCode, Is.EqualTo(1));
                    Assert.That(response.Header.BodyLength, Is.Zero);
                }

                cancellation.Cancel();
                foreach (Task task in blocked)
                {
                    Assert.CatchAsync<OperationCanceledException>(async () => await task);
                }

                if (register)
                {
                    await Send(GameNetRoutes.Register, new RegisterRequest { Account = "player", Password = "valid-password" }, 2);
                    using OwnedFrame response = (await Receive())!;
                    Assert.That(response.Header.StatusCode, Is.Zero);
                    Assert.That(GameNetRoutes.Register.ResponseCodec.Decode(response.Body).ResultCode, Is.EqualTo(ResultCode.Success));
                }
                else
                {
                    await Closed(1);
                }
            }
            finally
            {
                cancellation.Cancel();
                foreach (Task task in blocked)
                {
                    try
                    {
                        await task;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
        }

        [Test]
        public async Task ActualStartupCompositionRecordsHandlerFailureThroughTheInjectedLogger()
        {
            var failure = new InvalidOperationException("Injected repository/handler failure.");
            await Start(ServerCloseTests.Application((_, _, _) => throw failure));
            await Send(GameNetRoutes.Echo, new EchoRequest { Text = "sensitive-message-must-not-be-logged" });
            using (OwnedFrame response = (await Receive())!)
            {
                Assert.That(response.Header.StatusCode, Is.EqualTo(3));
            }

            await Closed(1);
            Assert.That(logs.Entries.Any(entry => entry.Category == typeof(WssServer).FullName &&
                entry.Level == LogLevel.Error && ReferenceEquals(entry.Error, failure)), Is.True);
            Assert.That(logs.Entries.All(entry => !entry.Text.Contains("sensitive-message-must-not-be-logged", StringComparison.Ordinal)), Is.True);
        }
    }
}
