using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Server.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace GameFrame.Tests
{
    // This project references only the framework; its independent catalog has no game DTOs or account service.
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public sealed class FrameworkBoundaryTests
    {
        private sealed class Message
        {
            internal uint Value;
        }

        private sealed class Session : ServerSession
        {
            internal Func<ServerMessageHeader, ServerAdmission>? Admission;
            internal Func<ServerConnectionContext, ServerCloseInfo, ValueTask>? OnClosed;
            internal Action<ServerConnectionContext>? OnConnected;
            internal readonly TaskCompletionSource<ServerConnectionContext> Connected = new TaskCompletionSource<ServerConnectionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Closes;
            internal bool Restricted;
            public override bool RequiresRestrictedTraffic => Restricted;

            public override ValueTask ConnectedAsync(ServerConnectionContext context)
            {
                OnConnected?.Invoke(context);
                Connected.TrySetResult(context);
                return ValueTask.CompletedTask;
            }

            public override ServerAdmission Admit(ServerMessageHeader header) => Admission?.Invoke(header) ?? ServerAdmission.Allow;

            public override ValueTask ClosedAsync(ServerConnectionContext context, ServerCloseInfo result)
            {
                Interlocked.Increment(ref Closes);
                return OnClosed?.Invoke(context, result) ?? ValueTask.CompletedTask;
            }
        }

        private readonly FrameMemoryBudget memory = new FrameMemoryBudget(1024 * 1024);
        private readonly NetPush<Message> push;
        private readonly NetRpc<Message, Message> rpc;
        private readonly NetSend<Message> notify;
        private readonly NetProtocolCatalog catalog;
        private readonly Session session = new Session();
        private int decodes;
        private WssServer server = null!;
        private WebApplication app = null!;
        private X509Certificate2 certificate = null!;
        private Uri endpoint = null!;
        private CancellationTokenSource guard = null!;
        private Exception? expectedShutdownFailure;
        private readonly TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<ServerConnectionContext, Message, CancellationToken, Task<Message>>? handler;
        private bool notifyReceived;
        private Action<Message>? releaseResponse;

        public FrameworkBoundaryTests()
        {
            var codec = new NetMessageCodec<Message>((writer, message) =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(writer.GetSpan(4), message.Value);
                writer.Advance(4);
            }, bytes =>
            {
                Interlocked.Increment(ref decodes);
                return new Message { Value = BinaryPrimitives.ReadUInt32LittleEndian(bytes) };
            }, _ => { }, bytes =>
            {
                if (bytes.Length != 4)
                {
                    throw new NetworkProtocolException("Invalid independent DTO.");
                }
            });
            rpc = new NetRpc<Message, Message>(2000, 4, 4, codec, codec);
            notify = new NetSend<Message>(2001, 4, codec);
            push = new NetPush<Message>(2002, NetDelivery.Reliable, 4, codec);
            catalog = new NetProtocolCatalog(SHA256.HashData("independent-framework-test"u8), rpc, notify, push);
        }

        [SetUp]
        public async Task Start()
        {
            decodes = 0;
            guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var application = new ServerApplication(catalog, _ => session);
            application.Rpc(rpc, (connection, message, token) => handler?.Invoke(connection, message, token) ?? Task.FromResult(new Message { Value = message.Value + 1 }), value => releaseResponse?.Invoke(value));
            application.Notify(notify, (_, _, _) =>
            {
                notifyReceived = true;
                return Task.CompletedTask;
            });
            server = new WssServer(application);
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            builder.Services.AddSingleton(server);
            builder.Services.AddHostedService(provider => provider.GetRequiredService<WssServer>());
            app = builder.Build();
            app.UseWebSockets();
            app.MapGet("/network", server.HandleAsync);
            await app.StartAsync(guard.Token);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            endpoint = new Uri(address.Replace("https:", "wss:") + "/network");
        }

        [TearDown]
        public async Task Stop()
        {
            foreach (Func<Task> cleanup in new Func<Task>[] { () => app.StopAsync(), () => server.DisposeAsync().AsTask(), () => app.DisposeAsync().AsTask() })
            {
                try
                {
                    await cleanup();
                }
                catch (Exception error) when (expectedShutdownFailure != null &&
                    (ReferenceEquals(error, expectedShutdownFailure) || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Contains(expectedShutdownFailure)))
                {
                }
            }

            certificate.Dispose();
            guard.Dispose();
            Assert.That(server.ConnectionCount, Is.Zero);
            Assert.That(server.OwnedBytes, Is.Zero);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        private async Task<ClientWebSocket> Connect(bool hello = true)
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == certificate.GetCertHashString();
            await socket.ConnectAsync(endpoint, guard.Token);
            if (hello)
            {
                using OwnedFrame frame = memory.Allocate(80);
                ControlCodec.WriteClientHello(frame.Bytes.AsSpan(32), catalog.Hash, 32768, 0, 0, ReadOnlySpan<byte>.Empty, NetworkTransportKind.WebSocket);
                // ControlCodec wrote the body directly; explicitly retain the full control frame length.
                frame.Header = new FrameHeader(FrameKind.Control, 1, 0, 48);
                frame.Length = 80;
                FrameCodec.WriteHeader(frame.Bytes, frame.Header);
                await WebSocketFrameTransport.SendAsync(socket, frame, guard.Token);
                using OwnedFrame response = (await Receive(socket))!;
                Assert.That(response.Header.Opcode, Is.EqualTo(2));
            }

            return socket;
        }

        private Task<OwnedFrame?> Receive(ClientWebSocket socket) => WebSocketFrameTransport.ReceiveAsync(socket, memory,
            32768, FrameDirection.ServerToClient, false, guard.Token)!;

        private async Task Send(ClientWebSocket socket, uint opcode, ulong correlation, byte[] body)
        {
            using OwnedFrame frame = memory.Allocate(32 + body.Length);
            body.CopyTo(frame.Bytes, 32);
            frame.Header = new FrameHeader(correlation == 0 ? FrameKind.Notify : FrameKind.Request, opcode, correlation, body.Length);
            frame.Length = frame.Capacity;
            FrameCodec.WriteHeader(frame.Bytes, frame.Header);
            await WebSocketFrameTransport.SendAsync(socket, frame, guard.Token);
        }

        [Test]
        public async Task IndependentCatalogSupportsTypedRpcNotifyAndUnsolicitedPush()
        {
            using ClientWebSocket socket = await Connect();
            ServerConnectionContext context = await session.Connected.Task.WaitAsync(guard.Token);
            await context.PushAsync(push, new Message { Value = 77 });
            using OwnedFrame pushed = (await Receive(socket))!;
            Assert.That(pushed.Header.Kind, Is.EqualTo(FrameKind.Push));
            Assert.That(pushed.Header.CorrelationId, Is.Zero);
            Assert.That(push.Codec.Decode(pushed.Body).Value, Is.EqualTo(77));
            await Send(socket, notify.Opcode, 0, BitConverter.GetBytes(1u));
            await Send(socket, rpc.Opcode, 9, BitConverter.GetBytes(41u));
            using OwnedFrame response = (await Receive(socket))!;
            Assert.That(notifyReceived, Is.True);
            Assert.That(response.Header.CorrelationId, Is.EqualTo(9));
            Assert.That(response.Header.Opcode, Is.EqualTo(rpc.Opcode));
            Assert.That(rpc.ResponseCodec.Decode(response.Body).Value, Is.EqualTo(42));
            Assert.That(typeof(WssServer).Assembly.GetReferencedAssemblies().Any(name => name.Name!.StartsWith("Game.")), Is.False);
            Assert.That(typeof(NetProtocolCatalog).Assembly.GetType("GameFrame.Network.Transport.Kcp.KcpWorker"), Is.Null);
        }

        [Test]
        public async Task EndedContextCannotPromoteTrafficAndEndpointIsImmutable()
        {
            session.Restricted = true;
            using ClientWebSocket socket = await Connect();
            ServerConnectionContext context = await session.Connected.Task.WaitAsync(guard.Token);
            IPEndPoint endpointCopy = context.RemoteEndpoint;
            int originalPort = endpointCopy.Port;
            endpointCopy.Port = 1;
            Assert.That(context.RemoteEndpoint.Port, Is.EqualTo(originalPort));
            await server.DisposeAsync();
            Assert.That(context.IsRestricted, Is.True);
            Assert.Throws<OperationCanceledException>(context.EnableFullTraffic);
            Assert.Throws<OperationCanceledException>(() => context.CloseAfterResponse(1));
            Assert.That(server.ConnectionCount, Is.Zero);
        }

        [Test]
        public async Task PeerOrderlyClosePublishesItsReasonAfterResourcesAreReleased()
        {
            using ClientWebSocket socket = await Connect();
            ServerConnectionContext context = await session.Connected.Task.WaitAsync(guard.Token);
            using (OwnedFrame frame = memory.Allocate(36))
            {
                ControlCodec.WriteClose(frame.Bytes.AsSpan(32), 9);
                frame.Header = new FrameHeader(FrameKind.Control, (uint)ControlOpcode.Close, 0, 4);
                frame.Length = 36;
                FrameCodec.WriteHeader(frame.Bytes, frame.Header);
                await WebSocketFrameTransport.SendAsync(socket, frame, guard.Token);
            }

            using OwnedFrame? end = await Receive(socket);
            Assert.That(end, Is.Null);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, guard.Token);
            ServerCloseInfo result = await context.Completion.WaitAsync(guard.Token);
            Assert.That(result.Reason, Is.EqualTo(9));
            Assert.That(result.Error, Is.Null);
            Assert.That(server.ConnectionCount, Is.Zero);
            Assert.That(server.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task PhaseRejectionPrecedesMalformedDtoDecoding()
        {
            session.Admission = _ => ServerAdmission.Deny();
            using ClientWebSocket socket = await Connect();
            await Send(socket, rpc.Opcode, 1, new byte[] { 255 });
            using OwnedFrame response = (await Receive(socket))!;
            Assert.That(response.Header.StatusCode, Is.EqualTo(2));
            Assert.That(decodes, Is.Zero);
        }

        [Test]
        public async Task LifecycleStoppingDrainsWebSocketBeforeKestrelTimeout()
        {
            using ClientWebSocket socket = await Connect(hello: false);
            var timer = Stopwatch.StartNew();
            await app.StopAsync(guard.Token);
            Assert.That(timer.ElapsedMilliseconds, Is.LessThan(1500));
            Assert.That(server.ConnectionCount, Is.Zero);
            Assert.That(server.OwnedBytes, Is.Zero);
            Assert.That(session.Closes, Is.EqualTo(1));
        }

        [Test]
        public async Task CancellationCallbackFailureStillJoinsApplicationAndReleasesAdmission()
        {
            var failure = new InvalidOperationException("Application cancellation callback failed.");
            handler = async (_, _, token) =>
            {
                using var registration = token.Register(() => throw failure);
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new Message();
            };
            using ClientWebSocket socket = await Connect();
            ServerConnectionContext context = await session.Connected.Task.WaitAsync(guard.Token);
            await Send(socket, rpc.Opcode, 1, BitConverter.GetBytes(1u));
            await entered.Task.WaitAsync(guard.Token);
            AggregateException error = Assert.ThrowsAsync<AggregateException>(async () => await server.DisposeAsync())!;
            expectedShutdownFailure = failure;
            Assert.That(error.Flatten().InnerExceptions, Does.Contain(failure));
            ServerCloseInfo result = await context.Completion.WaitAsync(guard.Token);
            Assert.That(result.Error, Is.SameAs(error));
            Assert.That(session.Closes, Is.EqualTo(1));
            Assert.That(server.OwnedBytes, Is.Zero);
            Assert.ThrowsAsync<AggregateException>(async () => await server.DisposeAsync());
        }

        [Test]
        public async Task ConnectedCallbackCanQueuePushBeforeOwnersStart()
        {
            session.OnConnected = context => context.PushAsync(push, new Message { Value = 88 }).GetAwaiter().GetResult();
            using ClientWebSocket socket = await Connect();
            using OwnedFrame received = (await Receive(socket))!;
            Assert.That(received.Header.Kind, Is.EqualTo(FrameKind.Push));
            Assert.That(push.Codec.Decode(received.Body).Value, Is.EqualTo(88));
        }

        [Test]
        public async Task ClosedCallbackFailureIsTerminalAndDoesNotSkipResourceRelease()
        {
            var failure = new InvalidOperationException("Closed notification failed.");
            session.OnClosed = (_, _) => throw failure;
            using ClientWebSocket socket = await Connect();
            ServerConnectionContext context = await session.Connected.Task.WaitAsync(guard.Token);
            expectedShutdownFailure = failure;
            Exception? error = Assert.ThrowsAsync<InvalidOperationException>(async () => await server.DisposeAsync());
            Assert.That(error, Is.SameAs(failure));
            Assert.That((await context.Completion).Error, Is.SameAs(failure));
            Assert.That(session.Closes, Is.EqualTo(1));
            Assert.That(server.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task ResponseCleanupFailureReleasesEncodedFrameAndPreservesOriginalError()
        {
            var failure = new InvalidOperationException("Response credential cleanup failed.");
            releaseResponse = _ => throw failure;
            using ClientWebSocket socket = await Connect();
            ServerConnectionContext context = await session.Connected.Task.WaitAsync(guard.Token);
            await Send(socket, rpc.Opcode, 1, BitConverter.GetBytes(1u));
            using OwnedFrame error = (await Receive(socket))!;
            Assert.That(error.Header.StatusCode, Is.EqualTo(3));
            using OwnedFrame close = (await Receive(socket))!;
            Assert.That(close.Header.Opcode, Is.EqualTo(6));
            using OwnedFrame? end = await Receive(socket);
            Assert.That(end, Is.Null);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, guard.Token);
            Assert.That((await context.Completion.WaitAsync(guard.Token)).Error, Is.SameAs(failure));
            Assert.That(server.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task DuplicateOrMissingRegistrationsFailBeforeHostStarts()
        {
            var missing = new ServerApplication(catalog);
            Assert.Throws<ArgumentException>(() => new WssServer(missing));
            var duplicate = new ServerApplication(catalog);
            duplicate.Rpc(rpc, (_, value, _) => Task.FromResult(value));
            Assert.Throws<ArgumentException>(() => duplicate.Rpc(rpc, (_, value, _) => Task.FromResult(value)));
        }
    }
}
