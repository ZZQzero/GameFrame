using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Network.Connection;
using GameFrame.Server.Network;
using Game.Server;
using Game.Server.Login;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace GameFrame.Tests
{

    [TestFixture(false)]
    [TestFixture(true)]
    public sealed class WssServerTests
    {
        private readonly bool usePostgres;
        private PostgresTestDatabase? database;
        private WebApplication app = null!;
        private WssServer server = null!;
        private LoginOptions settings = null!;
        private AccountSessions accounts = null!;
        private AccountService service = null!;
        private TestAccountRepository repository = null!;
        private X509Certificate2 certificate = null!;
        private Uri endpoint = null!;
        private readonly FrameMemoryBudget memory = new FrameMemoryBudget(1024 * 1024);
        private readonly List<ConnectionContext> clients = new List<ConnectionContext>();
        private ulong correlation;
        private CancellationTokenSource timeout = null!;

        public WssServerTests(bool usePostgres)
        {
            this.usePostgres = usePostgres;
        }

        [OneTimeSetUp]
        public void RequireDatabase()
        {
            if (usePostgres && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GF_POSTGRES_TEST_CONNECTION")))
            {
                Assert.Ignore("PostgreSQL WSS integration requires GF_POSTGRES_TEST_CONNECTION.");
            }
        }

        [SetUp]
        public async Task Start()
        {
            correlation = 0;
            timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            settings = new LoginOptions()
            {
                LoginTimeout = TimeSpan.FromSeconds(2)
            };
            repository = new TestAccountRepository();
            if (usePostgres)
            {
                database = await PostgresTestDatabase.CreateAsync();
            }

            IAccountRepository storage = database == null ? repository : new PostgresAccountRepository(database.Database);
            accounts = new AccountSessions(settings);
            service = new AccountService(storage, accounts);
            await service.RegisterAsync("local-test", "local-secret", timeout.Token);
            server = new WssServer(new LobbyApplication(accounts, service, settings).Application, new WssServerOptions { HelloTimeout = TimeSpan.FromMilliseconds(500) });
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            builder.Services.AddSingleton(server);
            builder.Services.AddHostedService(provider => provider.GetRequiredService<WssServer>());
            app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
            app.MapGet("/network", server.HandleAsync);
            await app.StartAsync(timeout.Token);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            endpoint = new Uri(address.Replace("https:", "wss:") + "/network");
        }

        [TearDown]
        public async Task Stop()
        {
            foreach (ConnectionContext client in clients)
            {
                client.BeginClose();
                await PumpUntil(client, client.CloseTask);
                await client.IoTask;
                Assert.That(client.Metrics().OwnedBytes, Is.Zero);
            }

            clients.Clear();
            await server.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
            certificate.Dispose();
            accounts.Dispose();
            service.Dispose();
            repository.Dispose();
            if (database != null)
            {
                await database.DisposeAsync();
                database = null;
            }
            timeout.Dispose();
            Assert.That(server.OwnedBytes, Is.Zero, "All in-flight/queued buffers must be released.");
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        private async Task<ClientWebSocket> Connect(bool hello = true, byte[]? hash = null, string? origin = null)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.Zero;
            // Test-only trust of this exact ephemeral certificate. Production code has no override.
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) => cert != null &&
                errors != SslPolicyErrors.RemoteCertificateNameMismatch &&
                cert.GetCertHashString() == certificate.GetCertHashString();
            if (origin != null)
            {
                socket.Options.SetRequestHeader("Origin", origin);
            }

            try
            {
                await socket.ConnectAsync(endpoint, timeout.Token);
                if (hello)
                {
                    using OwnedFrame frame = memory.Allocate(80);
                    byte[] protocolHash = hash ?? new byte[32];
                    if (hash == null)
                    {
                        GameNetProtocol.Catalog.CopyHashTo(protocolHash);
                    }

                    ControlCodec.WriteClientHello(frame.Bytes.AsSpan(32), protocolHash, 32768, 0, 0, ReadOnlySpan<byte>.Empty,
                        NetworkTransportKind.WebSocket);
                    frame.Header = new FrameHeader(FrameKind.Control, 1, 0, 48);
                    frame.Length = 80;
                    FrameCodec.WriteHeader(frame.Bytes, frame.Header);
                    await WebSocketFrameTransport.SendAsync(socket, frame, timeout.Token);
                }

                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private async Task Hello(ClientWebSocket socket)
        {
            using OwnedFrame frame = (await Receive(socket))!;
            Assert.That(frame.Header.Opcode, Is.EqualTo((uint)ControlOpcode.ServerHello));
            ServerHelloBody hello = ControlCodec.ReadServerHello(frame.Body, NetworkTransportKind.WebSocket);
            Assert.That(hello.SessionId.Length, Is.EqualTo(16));
            Assert.That(hello.Hash.SequenceEqual(GameNetProtocol.Catalog.Hash), Is.True);
        }

        private Task<OwnedFrame?> Receive(ClientWebSocket socket) => WebSocketFrameTransport.ReceiveAsync(socket, memory,
            32768, FrameDirection.ServerToClient, false, timeout.Token)!;

        private async Task<ulong> Send<TRequest, TResponse>(ClientWebSocket socket, NetRpc<TRequest, TResponse> route, TRequest dto)
            where TRequest : class where TResponse : class
        {
            ulong id = ++correlation;
            using OwnedFrame frame = memory.Allocate(32 + route.MaxBodyBytes);
            var writer = new BoundedFrameWriter(frame);
            route.RequestCodec.Encode(writer, dto);
            writer.Complete(FrameKind.Request, route.Opcode, id);
            await WebSocketFrameTransport.SendAsync(socket, frame, timeout.Token);
            return id;
        }

        private async Task<TResponse> Rpc<TRequest, TResponse>(ClientWebSocket socket, NetRpc<TRequest, TResponse> route, TRequest dto)
            where TRequest : class where TResponse : class
        {
            ulong id = await Send(socket, route, dto);
            using OwnedFrame frame = (await Receive(socket))!;
            Assert.That(frame.Header.CorrelationId, Is.EqualTo(id));
            Assert.That(frame.Header.Opcode, Is.EqualTo(route.Opcode));
            Assert.That(frame.Header.StatusCode, Is.Zero);
            return route.ResponseCodec.Decode(frame.Body);
        }

        private Task<LoginResponse> Login(ClientWebSocket socket) => Rpc(socket, GameNetRoutes.Login,
            new LoginRequest { Account = "local-test", Password = "local-secret" });

        private async Task AssertClose(ClientWebSocket socket, ushort reason)
        {
            using OwnedFrame close = (await Receive(socket))!;
            Assert.That(close.Header.Opcode, Is.EqualTo((uint)ControlOpcode.Close));
            Assert.That(ControlCodec.ReadClose(close.Body), Is.EqualTo(reason));
            using OwnedFrame? end = await Receive(socket);
            Assert.That(end, Is.Null);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
        }

        [Test]
        public async Task RegisterThenLoginOnTheSameConnectionWithoutServerConfiguredPlayerCredentials()
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            OperationResponse registered = await Rpc(socket, GameNetRoutes.Register,
                new RegisterRequest { Account = "new-player", Password = "new-password" });
            Assert.That(registered.ResultCode, Is.EqualTo(ResultCode.Success));
            OperationResponse duplicate = await Rpc(socket, GameNetRoutes.Register,
                new RegisterRequest { Account = "new-player", Password = "different-password" });
            Assert.That(duplicate.ResultCode, Is.EqualTo(ResultCode.AccountAlreadyExists));
            LoginResponse login = await Rpc(socket, GameNetRoutes.Login,
                new LoginRequest { Account = "new-player", Password = "new-password" });
            Assert.That(login.ResultCode, Is.EqualTo(ResultCode.Success));
            Assert.That(login.AccountId, Is.GreaterThan(1));
            Assert.That((await Rpc(socket, GameNetRoutes.Echo, new EchoRequest { Text = "registered" })).Text, Is.EqualTo("registered"));
        }

        [Test]
        public async Task RegisterDoesNotAuthorizeBusinessRequests()
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            Assert.That((await Rpc(socket, GameNetRoutes.Register,
                new RegisterRequest { Account = "unbound", Password = "new-password" })).ResultCode, Is.EqualTo(ResultCode.Success));
            await Send(socket, GameNetRoutes.Echo, new EchoRequest());
            using OwnedFrame denied = (await Receive(socket))!;
            Assert.That(denied.Header.StatusCode, Is.EqualTo(2));
        }

        [Test]
        public async Task LoginEchoLogoutRevokesResumeAndReleasesMemory()
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            LoginResponse login = await Login(socket);
            Assert.That(login.ResultCode, Is.EqualTo(ResultCode.Success));
            Assert.That(login.AccountSessionId.Length, Is.EqualTo(16));
            Assert.That(login.ResumeToken.Length, Is.EqualTo(32));
            Assert.That(login.AuthEpoch, Is.GreaterThan(0));
            Assert.That(login.AccountRemainingTtlMs, Is.InRange(1, 86400000));
            EchoResponse echo = await Rpc(socket, GameNetRoutes.Echo, new EchoRequest { Text = "你好 WSS" });
            Assert.That(echo.Text, Is.EqualTo("你好 WSS"));
            WorldGrantResponse grant = await Rpc(socket, GameNetRoutes.RequestWorldGrant, new RequestWorldGrantRequest());
            Assert.That(grant.ResultCode, Is.EqualTo(ResultCode.WorldUnavailable));
            OperationResponse logout = await Rpc(socket, GameNetRoutes.Logout, new LogoutRequest());
            Assert.That(logout.ResultCode, Is.EqualTo(ResultCode.Success));
            using OwnedFrame close = (await Receive(socket))!;
            Assert.That(close.Header.Opcode, Is.EqualTo(6));
            Assert.That(ControlCodec.ReadClose(close.Body), Is.EqualTo(1));
            using ClientWebSocket resume = await Connect();
            await Hello(resume);
            LoginResponse refused = await Rpc(resume, GameNetRoutes.ResumeLogin,
                new ResumeLoginRequest { AccountSessionId = login.AccountSessionId, ResumeToken = login.ResumeToken });
            Assert.That(refused.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
        }

        [Test]
        public async Task ResumeRotatesTokenRetainsAccountGenerationAndConsumesExactlyOnce()
        {
            using ClientWebSocket first = await Connect();
            await Hello(first);
            LoginResponse login = await Login(first);
            using ClientWebSocket second = await Connect();
            await Hello(second);
            LoginResponse resumed = await Rpc(second, GameNetRoutes.ResumeLogin,
                new ResumeLoginRequest { AccountSessionId = login.AccountSessionId, ResumeToken = login.ResumeToken });
            Assert.That(resumed.AuthEpoch, Is.EqualTo(login.AuthEpoch));
            Assert.That(resumed.AccountSessionId, Is.EqualTo(login.AccountSessionId));
            Assert.That(resumed.ResumeToken, Is.Not.EqualTo(login.ResumeToken));
            await AssertClose(first, 2);
            using ClientWebSocket third = await Connect();
            await Hello(third);
            LoginResponse reused = await Rpc(third, GameNetRoutes.ResumeLogin,
                new ResumeLoginRequest { AccountSessionId = login.AccountSessionId, ResumeToken = login.ResumeToken });
            Assert.That(reused.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
            Assert.That((await Rpc(second, GameNetRoutes.Echo, new EchoRequest { Text = "still alive" })).Text,
                Is.EqualTo("still alive"));
        }

        [Test]
        public async Task NewLoginRevokesOldEpochAndOldResumeToken()
        {
            using ClientWebSocket first = await Connect();
            await Hello(first);
            LoginResponse old = await Login(first);
            using ClientWebSocket second = await Connect();
            await Hello(second);
            LoginResponse replacement = await Login(second);
            Assert.That(replacement.AuthEpoch, Is.GreaterThan(old.AuthEpoch));
            Assert.That(replacement.AccountSessionId, Is.Not.EqualTo(old.AccountSessionId));
            await AssertClose(first, 2);
            using ClientWebSocket third = await Connect();
            await Hello(third);
            LoginResponse refused = await Rpc(third, GameNetRoutes.ResumeLogin,
                new ResumeLoginRequest { AccountSessionId = old.AccountSessionId, ResumeToken = old.ResumeToken });
            Assert.That(refused.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
        }

        [Test]
        public async Task InvalidCredentialsReturnUniformFailureThenClose()
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            LoginResponse response = await Rpc(socket, GameNetRoutes.Login,
                new LoginRequest { Account = "unknown", Password = "wrong" });
            Assert.That(response.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
            Assert.That(response.ResumeToken, Is.Empty);
            using OwnedFrame close = (await Receive(socket))!;
            Assert.That(ControlCodec.ReadClose(close.Body), Is.EqualTo(5));
        }

        [Test]
        public async Task UnauthenticatedEchoIsRejectedBeforeMalformedDtoIsDecoded()
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            byte[] frame = new byte[33];
            FrameCodec.WriteHeader(frame, new FrameHeader(FrameKind.Request, GameNetRoutes.Echo.Opcode, 1, 1));
            frame[32] = 255;
            await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, timeout.Token);
            using OwnedFrame response = (await Receive(socket))!;
            Assert.That(response.Header.StatusCode, Is.EqualTo(2));
            Assert.That(response.Header.BodyLength, Is.Zero);
            using OwnedFrame close = (await Receive(socket))!;
            Assert.That(ControlCodec.ReadClose(close.Body), Is.EqualTo(5));
        }

        [Test]
        public async Task HashMismatchRejectsBeforeLogin()
        {
            using ClientWebSocket socket = await Connect(hash: new byte[32]);
            using OwnedFrame reject = (await Receive(socket))!;
            Assert.That(reject.Header.Opcode, Is.EqualTo(5));
            Assert.That(ControlCodec.ReadReject(reject.Body, out ReadOnlySpan<byte> expected), Is.EqualTo(1));
            Assert.That(expected.SequenceEqual(GameNetProtocol.Catalog.Hash), Is.True);
        }

        [Test]
        public async Task LoginDeadlineIsNotExtendedByPing()
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            using OwnedFrame ping = memory.Allocate(40);
            ControlCodec.WriteNonce(ping.Bytes.AsSpan(32), 42);
            ping.Header = new FrameHeader(FrameKind.Control, 3, 0, 8);
            ping.Length = 40;
            FrameCodec.WriteHeader(ping.Bytes, ping.Header);
            await WebSocketFrameTransport.SendAsync(socket, ping, timeout.Token);
            using OwnedFrame pong = (await Receive(socket))!;
            Assert.That(ControlCodec.ReadNonce(pong.Body), Is.EqualTo(42));
            using OwnedFrame close = (await Receive(socket))!;
            Assert.That(ControlCodec.ReadClose(close.Body), Is.EqualTo(6));
        }

        [Test]
        public async Task FragmentedHeaderAndBodyAreAcceptedExactlyOnce()
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            await Login(socket);
            using OwnedFrame echo = memory.Allocate(1056);
            var writer = new BoundedFrameWriter(echo);
            GameNetRoutes.Echo.RequestCodec.Encode(writer, new EchoRequest { Text = "fragmented" });
            writer.Complete(FrameKind.Request, GameNetRoutes.Echo.Opcode, ++correlation);
            for (int i = 0; i < echo.Length; i++)
            {
                await socket.SendAsync(new ArraySegment<byte>(echo.Bytes, i, 1), WebSocketMessageType.Binary,
                    i == echo.Length - 1, timeout.Token);
            }

            using OwnedFrame response = (await Receive(socket))!;
            Assert.That(GameNetRoutes.Echo.ResponseCodec.Decode(response.Body).Text, Is.EqualTo("fragmented"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task TextAndOversizedDeclaredBodyCloseWithoutResponse(bool text)
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            byte[] bytes = new byte[32];
            if (!text)
            {
                FrameCodec.WriteHeader(bytes, new FrameHeader(FrameKind.Request, 1000, 1, 32736));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), uint.MaxValue);
            }

            await socket.SendAsync(new ArraySegment<byte>(bytes), text ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                true, timeout.Token);
            Assert.ThrowsAsync<WebSocketException>(async () => await Receive(socket));
        }

        [Test]
        public void BrowserOriginOutsideAllowlistIsRejected()
        {
            Assert.ThrowsAsync<WebSocketException>(async () =>
            {
                using ClientWebSocket socket = await Connect(origin: "https://untrusted.example");
            });
        }

        [Test]
        public void CertificateValidationRemainsEnabled()
        {
            Assert.ThrowsAsync<WebSocketException>(async () =>
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(endpoint, timeout.Token);
            });
        }
        private async Task<ConnectionContext> StartClient(NetworkClientOptions? clientOptions = null)
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert?.GetCertHashString() == certificate.GetCertHashString();
            clientOptions ??= new NetworkClientOptions();
            var client = new ConnectionContext(1, clientOptions, GameNetProtocol.Catalog,
                NetworkConnectOptions.WebSocket(endpoint), memory, timeout.Token, socket);
            clients.Add(client);
            client.Start();
            await PumpUntil(client, client.ConnectTask);
            return client;
        }

        private async Task PumpUntil(ConnectionContext client, Task task)
        {
            while (!task.IsCompleted)
            {
                timeout.Token.ThrowIfCancellationRequested();
                for (int i = 0; i < 16 && client.PumpOne(); i++)
                {
                }

                if (!task.IsCompleted)
                {
                    await Task.Delay(5, timeout.Token);
                }
            }

            await task;
        }

        private async Task WaitFor(Func<bool> condition)
        {
            while (!condition())
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        private async Task LoginClient(ConnectionContext client)
        {
            Task<LoginResponse> login = client.Request(GameNetRoutes.Login,
                new LoginRequest { Account = "local-test", Password = "local-secret" }, TimeSpan.FromSeconds(1), default);
            await PumpUntil(client, login);
            Assert.That(login.Result.ResultCode, Is.EqualTo(ResultCode.Success));
        }

        [Test]
        public async Task ActualClientCompletesOnlyWhenMainPumpRuns()
        {
            ConnectionContext client = await StartClient();
            await LoginClient(client);
            Task<EchoResponse> echo = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = "main pump" },
                TimeSpan.FromSeconds(1), default);
            await WaitFor(() => client.Metrics().ReliableReceiveCount == 1);
            Assert.That(echo.IsCompleted, Is.False);
            Assert.That(client.Metrics().DeadlineCount, Is.Zero, "Arrival reserves the response and removes its deadline.");
            await PumpUntil(client, echo);
            Assert.That(echo.Result.Text, Is.EqualTo("main pump"));
            Assert.That(client.Metrics().PendingCount, Is.Zero);
        }

        [Test]
        public async Task ClientDeadlineRunsWithoutMainPumpAndLateResponseIsDiscarded()
        {
            ConnectionContext client = await StartClient();
            await LoginClient(client);
            Task<EchoResponse> echo = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = "late", DelayMs = 150 },
                TimeSpan.FromMilliseconds(20), default);
            await WaitFor(() => client.Metrics().RequestTimeouts == 1);
            Assert.That(echo.IsCompleted, Is.False);
            Assert.That(client.Metrics().PendingCount, Is.EqualTo(1), "Completion retains its admission slot until main-thread delivery.");
            Assert.ThrowsAsync<NetworkRequestTimeoutException>(async () => await PumpUntil(client, echo));
            await WaitFor(() => client.Metrics().LateResponses == 1);
            Assert.That(client.Metrics().ReliableReceiveCount, Is.Zero);
            Assert.That(client.Metrics().DeadlineCount, Is.Zero);
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Connected));
        }

        [Test]
        public async Task CallerCancellationWinsAfterResponseReservationBeforeMainCompletion()
        {
            ConnectionContext client = await StartClient();
            await LoginClient(client);
            using var cancellation = new CancellationTokenSource();
            Task<EchoResponse> echo = client.Request(GameNetRoutes.Echo, new EchoRequest { Text = "reserved" },
                TimeSpan.FromSeconds(1), cancellation.Token);
            await WaitFor(() => client.Metrics().ReliableReceiveCount == 1);
            cancellation.Cancel();
            Assert.ThrowsAsync<TaskCanceledException>(async () => await PumpUntil(client, echo));
            while (client.PumpOne())
            {
            }

            Assert.That(client.Metrics().PendingCount, Is.Zero);
            Assert.That(client.Metrics().DeadlineCount, Is.Zero);
            Assert.That(client.Metrics().RequestCancellations, Is.EqualTo(1));
        }

        [Test]
        public async Task PendingQuotaIncludesReservedResponsesAndRecoversAfterCompletion()
        {
            ConnectionContext client = await StartClient(new NetworkClientOptions { PendingCount = 1 });
            await LoginClient(client);
            Task<EchoResponse> first = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            await WaitFor(() => client.Metrics().ReliableReceiveCount == 1);
            Assert.Throws<NetworkBackpressureException>(() => client.Request(GameNetRoutes.Echo,
                new EchoRequest(), TimeSpan.FromSeconds(1), default));
            await PumpUntil(client, first);
            Task<EchoResponse> second = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            await PumpUntil(client, second);
        }

        [Test]
        public async Task OrderedAuthenticationFailureResponsePrecedesRemoteClose()
        {
            ConnectionContext client = await StartClient();
            Task<LoginResponse> login = client.Request(GameNetRoutes.Login,
                new LoginRequest { Account = "wrong", Password = "wrong" }, TimeSpan.FromSeconds(1), default);
            await WaitFor(() => client.Metrics().ReliableReceiveCount == 1);
            await PumpUntil(client, login);
            Assert.That(login.Result.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
            await WaitFor(() => client.IoTask.IsCompleted);
            while (client.PumpOne())
            {
            }

            Assert.That(client.State, Is.EqualTo(NetworkClientState.Faulted));
        }

        [Test]
        public async Task CloseFailsReservedResponsesAndWaitsForActualIoExit()
        {
            ConnectionContext client = await StartClient();
            await LoginClient(client);
            Task<EchoResponse> echo = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            await WaitFor(() => client.Metrics().ReliableReceiveCount == 1);
            var states = new List<NetworkClientState>();
            client.StateChanged = (state, _) => states.Add(state);
            client.BeginClose();
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Closing));
            Assert.ThrowsAsync<NetworkException>(async () => await PumpUntil(client, echo));
            await PumpUntil(client, client.CloseTask);
            Assert.That(client.IoTask.IsCompleted, Is.True);
            Assert.That(client.Metrics().OwnedBytes, Is.Zero);
            CollectionAssert.AreEqual(new[] { NetworkClientState.Closing, NetworkClientState.Idle }, states);
        }

        [Test]
        public async Task AuthenticationRejectionWaitsForPeerCloseAcknowledgement()
        {
            using ClientWebSocket socket = await Connect();
            await Hello(socket);
            LoginResponse failure = await Rpc(socket, GameNetRoutes.Login,
                new LoginRequest { Account = "wrong", Password = "wrong" });
            Assert.That(failure.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
            using OwnedFrame close = (await Receive(socket))!;
            Assert.That(ControlCodec.ReadClose(close.Body), Is.EqualTo(5));
            using OwnedFrame? end = await Receive(socket);
            Assert.That(end, Is.Null);
            Assert.That(server.ConnectionCount, Is.EqualTo(1), "The receive owner must remain alive for the peer acknowledgement.");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
            await WaitFor(() => server.ConnectionCount == 0);
        }

        [Test]
        public async Task HostFailureRejectsReservedResponseAndReleasesAllOwnedFrames()
        {
            ConnectionContext client = await StartClient();
            await LoginClient(client);
            Task<EchoResponse> echo = client.Request(GameNetRoutes.Echo, new EchoRequest(), TimeSpan.FromSeconds(1), default);
            await WaitFor(() => client.Metrics().ReliableReceiveCount == 1);
            var failure = new InvalidOperationException("Injected host invariant failure.");
            client.FailHost(failure);
            Exception? result = Assert.ThrowsAsync<InvalidOperationException>(async () => await PumpUntil(client, echo));
            Assert.That(result, Is.SameAs(failure));
            Assert.That(client.State, Is.EqualTo(NetworkClientState.Faulted));
            await client.IoTask;
            while (client.PumpOne())
            {
            }

            Assert.That(client.HostCleanupComplete, Is.True);
            Assert.That(client.Metrics().PendingCount, Is.Zero);
            Assert.That(client.Metrics().DeadlineCount, Is.Zero);
            Assert.That(client.Metrics().ReliableReceiveCount, Is.Zero);
            Assert.That(client.Metrics().OwnedBytes, Is.Zero);
            client.BeginClose();
            await PumpUntil(client, client.CloseTask);
            Assert.That(client.Metrics().OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task HostFailureSupersedesOrderlyCloseWithoutDecodingReservedResponse()
        {
            ConnectionContext client = await StartClient();
            Task<LoginResponse> login = client.Request(GameNetRoutes.Login,
                new LoginRequest { Account = "wrong", Password = "wrong" }, TimeSpan.FromSeconds(1), default);
            await WaitFor(() => client.Metrics().ReliableReceiveCount == 1 && client.IoTask.IsCompleted);
            var failure = new InvalidOperationException("Host failed before reliable delivery.");
            client.FailHost(failure);
            Exception? result = Assert.ThrowsAsync<InvalidOperationException>(async () => await PumpUntil(client, login));
            Assert.That(result, Is.SameAs(failure));
            while (client.PumpOne())
            {
            }

            Assert.That(client.HostCleanupComplete, Is.True);
            Assert.That(client.Metrics().OwnedBytes, Is.Zero);
        }

        [Test]
        public void ProductionWithoutAuthenticationProviderRefusesStartup()
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Game.Server.Program.Main(Array.Empty<string>()));
        }

        [Test]
        public async Task ClosingFromConnectedObserverCannotReportConnectSuccess()
        {
            using var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert?.GetCertHashString() == certificate.GetCertHashString();
            var client = new ConnectionContext(1, new NetworkClientOptions(), GameNetProtocol.Catalog,
                NetworkConnectOptions.WebSocket(endpoint), memory, timeout.Token, socket);
            clients.Add(client);
            client.StateChanged = (state, _) =>
            {
                if (state == NetworkClientState.Connected)
                {
                    client.BeginClose();
                }
            };
            client.Start();
            Assert.ThrowsAsync<NetworkException>(async () => await PumpUntil(client, client.ConnectTask));
            await PumpUntil(client, client.CloseTask);
            Assert.That(client.IoTask.IsCompleted, Is.True);
            Assert.That(client.Metrics().OwnedBytes, Is.Zero);
        }

    }
}
