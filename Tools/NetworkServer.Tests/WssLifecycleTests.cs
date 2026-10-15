using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Game.Protocol;
using Game.Server;
using Game.Server.Configuration;
using Game.Server.Login;
using GameFrame.Network;
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
            internal readonly ConcurrentQueue<(string Category, LogLevel Level, Exception? Error, string Text,
                EventId EventId, IReadOnlyDictionary<string, object?> Fields)> Entries = new();
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
                public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
                {
                    var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                        ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                        : new Dictionary<string, object?>();
                    owner.Entries.Enqueue((category, level, error, formatter(state, error), eventId, fields));
                }
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
        private ILoggerFactory logging = null!;

        [SetUp]
        public void Prepare()
        {
            logs.Entries.Clear();
            guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            logging = LoggerFactory.Create(builder => builder.AddProvider(logs));
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
            var tables = TableConfigLoader.Load(null);
            app = LocalWssService.CreateHost(application, new WssServerOptions(), certificate, port, tables, logging);
            Assert.That(app.Services.GetRequiredService<Game.Config.TableConfig.Tables>(), Is.SameAs(tables));
            Assert.That(app.Services.GetRequiredService<ILoggerFactory>(), Is.SameAs(logging));
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
            logging.Dispose();
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
            using var service = new AuthenticationService(repository, sessions, new LoginOptions());
            await service.RegisterAsync("player", "valid-password", guard.Token);
            await Start(new LobbyApplication(new LoginModule(sessions, service, options)).Application);
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
            using var service = new AuthenticationService(repository, sessions, new LoginOptions());
            using var cancellation = new CancellationTokenSource();
            Task[] blocked = Enumerable.Range(0, 4).Select(index => service.LoginAsync("blocked-" + index, "valid-password", _ => { }, cancellation.Token)).ToArray();
            try
            {
                await Start(new LobbyApplication(new LoginModule(sessions, service, new LoginOptions())).Application);
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
            const string secret = "sensitive-message-must-not-be-logged";
            var failure = new InvalidOperationException(secret, new Exception(secret));
            failure.Data["credential"] = secret;
            ServerConnectionContext? context = null;
            await Start(ServerCloseTests.Application((connection, _, _) =>
            {
                context = connection;
                throw failure;
            }));
            await Send(GameNetRoutes.Echo, new EchoRequest { Text = secret }, 42);
            using (OwnedFrame response = (await Receive())!)
            {
                Assert.That(response.Header.StatusCode, Is.EqualTo(3));
            }

            await Closed(1);
            var entry = logs.Entries.Single(entry => entry.EventId == NetworkLog.ConnectionFailure);
            Assert.That(entry.Category, Is.EqualTo(typeof(WssServer).FullName));
            Assert.That(entry.Level, Is.EqualTo(LogLevel.Error));
            Assert.That(entry.Error, Is.Null, "Providers must never receive the original application exception.");
            Assert.That(entry.Fields["ConnectionId"], Is.EqualTo(context!.Id));
            Assert.That(entry.Fields["Generation"], Is.EqualTo(context.Generation));
            Assert.That(entry.Fields["Transport"], Is.EqualTo(NetworkTransportKind.WebSocket));
            Assert.That(entry.Fields["Opcode"], Is.EqualTo(GameNetRoutes.Echo.Opcode));
            Assert.That(entry.Fields["CorrelationId"], Is.EqualTo(42UL));
            Assert.That(entry.Fields["Stage"], Is.EqualTo("Dispatch"));
            Assert.That(entry.Fields["ExceptionStack"], Is.Not.Empty);
            Assert.That((await context.Completion).Error, Is.SameAs(failure), "Public error semantics retain the first exception.");
            Assert.That(logs.Entries.All(entry => !entry.Text.Contains(secret, StringComparison.Ordinal)), Is.True);
        }

        private sealed class FailingCloseSession : ServerSession
        {
            internal readonly Exception Error = new InvalidOperationException("private-cleanup-credential");
            internal Exception? Observed;

            public override ValueTask ClosedAsync(ServerConnectionContext connection, ServerCloseInfo result)
            {
                Observed = result.Error;
                throw Error;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClosedFailureIsRecordedIndependentlyAndPreservesTheFirstError(bool failHandler)
        {
            var session = new FailingCloseSession();
            var primary = new InvalidOperationException("private-handler-credential");
            ServerConnectionContext? context = null;
            await Start(ServerCloseTests.Application((connection, _, _) =>
            {
                context = connection;
                if (failHandler)
                {
                    throw primary;
                }

                connection.RequestClose(1);
                return Task.FromResult(new EchoResponse());
            }, _ => session));
            await Send(GameNetRoutes.Echo, new EchoRequest());
            if (failHandler)
            {
                using OwnedFrame response = (await Receive())!;
                Assert.That(response.Header.StatusCode, Is.EqualTo(3));
            }

            await Closed(1);
            var cleanup = logs.Entries.Single(entry => entry.EventId == NetworkLog.ConnectionCleanupFailure);
            Assert.That(cleanup.Fields["Stage"], Is.EqualTo("Closed"));
            Assert.That(cleanup.Fields["ConnectionId"], Is.EqualTo(context!.Id));
            Assert.That(logs.Entries.Count(entry => entry.EventId == NetworkLog.ConnectionFailure), Is.EqualTo(failHandler ? 1 : 0));
            Assert.That(session.Observed, Is.SameAs(failHandler ? primary : null));
            Assert.That((await context.Completion).Error, Is.SameAs(failHandler ? primary : session.Error));
            Assert.That(logs.Entries.All(entry => entry.Error == null &&
                !entry.Text.Contains("private-cleanup-credential", StringComparison.Ordinal) &&
                !entry.Text.Contains("private-handler-credential", StringComparison.Ordinal)), Is.True);
        }
    }
}
