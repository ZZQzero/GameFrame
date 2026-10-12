using System.Net;
using System.Runtime.ExceptionServices;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;

namespace GameFrame.Server.Network
{
    public readonly record struct ServerMessageHeader(uint Opcode, ulong CorrelationId, int BodyLength, NetDelivery Delivery)
    {
        public bool IsRequest => CorrelationId != 0;
    }

    public readonly record struct ServerAdmission(int StatusCode, ushort CloseReason)
    {
        public bool Allowed => StatusCode == 0 && CloseReason == 0;
        public static ServerAdmission Allow => default;
        public static ServerAdmission Deny(int statusCode = 2, ushort closeReason = 5) => new ServerAdmission(statusCode, closeReason);
    }

    public readonly record struct ServerCloseInfo(ushort Reason, Exception? Error);

    public class ServerSession
    {
        public virtual bool RequiresRestrictedTraffic => false;
        public virtual ValueTask ConnectedAsync(ServerConnectionContext connection) => ValueTask.CompletedTask;
        // Called on the transport owner before decoding. Implementations must be fast and thread-safe.
        public virtual ServerAdmission Admit(ServerMessageHeader header) => ServerAdmission.Allow;
        // Called again on the application owner, immediately before decoding and dispatch.
        public virtual ServerAdmission CheckDispatch(ServerMessageHeader header) => ServerAdmission.Allow;
        public virtual ushort? CheckDeadline(long nowMilliseconds) => null;
        public virtual ValueTask ClosedAsync(ServerConnectionContext connection, ServerCloseInfo result) => ValueTask.CompletedTask;
    }

    public sealed class ServerRequestException : Exception
    {
        public int StatusCode { get; }
        public ushort CloseReason { get; }

        public ServerRequestException(int statusCode, ushort closeReason = 0) : base($"Application rejected request ({statusCode}).")
        {
            if (statusCode == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(statusCode));
            }

            StatusCode = statusCode;
            CloseReason = closeReason;
        }
    }

    public sealed class ServerConnectionContext
    {
        private readonly NetProtocolCatalog catalog;
        private readonly FrameMemoryBudget sendMemory;
        private readonly Func<OwnedFrame, bool> enqueue;
        private readonly Action stop;
        private readonly Action promote;
        private readonly object lifecycleGate = new object();
        private readonly IPEndPoint remoteEndpoint;
        private bool trafficEnded;
        internal Action<Exception> ReportSecondaryFailure { get; set; } = _ => { };
        private readonly TaskCompletionSource<ServerCloseInfo> completion = new TaskCompletionSource<ServerCloseInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int closeAfterResponse;
        private int restricted = 1;
        public ServerSession Session { get; internal set; } = null!;
        public ulong Id { get; }
        public ulong Generation { get; }
        public ulong GrantId { get; }
        public NetworkTransportKind Transport { get; }
        public IPEndPoint RemoteEndpoint => CopyEndpoint(remoteEndpoint);
        public CancellationToken Cancellation { get; }
        public Task<ServerCloseInfo> Completion => completion.Task;
        public long NowMilliseconds => NetworkClock.GetMilliseconds();
        public bool IsRestricted => Volatile.Read(ref restricted) != 0;

        internal ServerConnectionContext(ulong id, ulong generation, ulong grantId, NetworkTransportKind transport,
            IPEndPoint remote, NetProtocolCatalog catalog, FrameMemoryBudget sendMemory, CancellationToken cancellation,
            Func<OwnedFrame, bool> enqueue, Action stop, Action? promote = null)
        {
            Id = id;
            Generation = generation;
            GrantId = grantId;
            Transport = transport;
            remoteEndpoint = CopyEndpoint(remote);
            this.catalog = catalog;
            this.sendMemory = sendMemory;
            Cancellation = cancellation;
            this.enqueue = enqueue;
            this.stop = stop;
            this.promote = promote ?? (() => { });
        }

        // Applications choose their pre-authentication phase; framework quotas do not interpret account identity.
        public void EnableFullTraffic()
        {
            lock (lifecycleGate)
            {
                RequireActive();
                if (Interlocked.Exchange(ref restricted, 0) != 0)
                {
                    promote();
                }
            }
        }

        internal static IPEndPoint CopyEndpoint(IPEndPoint endpoint)
        {
            byte[] address = endpoint.Address.GetAddressBytes();
            return new IPEndPoint(address.Length == 16 ? new IPAddress(address, endpoint.Address.ScopeId) : new IPAddress(address), endpoint.Port);
        }

        private void RequireActive()
        {
            Cancellation.ThrowIfCancellationRequested();
            if (trafficEnded || Completion.IsCompleted)
            {
                throw new NetworkException("Connection is already closing or closed.");
            }
        }

        internal void EndTraffic()
        {
            lock (lifecycleGate)
            {
                trafficEnded = true;
            }
        }

        public void Disconnect() => stop();

        public async Task CloseAsync()
        {
            if (!Completion.IsCompleted)
            {
                stop();
            }

            ServerCloseInfo result = await Completion.ConfigureAwait(false);
            if (result.Error != null)
            {
                ExceptionDispatchInfo.Capture(result.Error).Throw();
            }
        }

        // An owner handler cannot wait for its own exit. Use this to send the current response before orderly Close.
        public void CloseAfterResponse(ushort reason)
        {
            if (reason == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            lock (lifecycleGate)
            {
                RequireActive();
                Interlocked.CompareExchange(ref closeAfterResponse, reason, 0);
            }
        }

        internal ushort TakeCloseReason() => (ushort)Interlocked.Exchange(ref closeAfterResponse, 0);
        internal void Complete(ServerCloseInfo result)
        {
            EndTraffic();
            completion.TrySetResult(result);
        }

        public Task PushAsync<T>(NetPush<T> route, T message) where T : class
        {
            ArgumentNullException.ThrowIfNull(route);
            if (route.Delivery != NetDelivery.Reliable)
            {
                throw new ArgumentException("Use TryPushUnreliable for an unreliable route.", nameof(route));
            }

            if (!Push(route, message))
            {
                throw new NetworkBackpressureException("Reliable server Push was not admitted.");
            }

            return Task.CompletedTask;
        }

        public bool TryPushUnreliable<T>(NetPush<T> route, T message) where T : class
        {
            ArgumentNullException.ThrowIfNull(route);
            if (route.Delivery != NetDelivery.Unreliable)
            {
                throw new ArgumentException("The route must be unreliable.", nameof(route));
            }

            try
            {
                return Push(route, message);
            }
            catch (NetworkBackpressureException)
            {
                return false;
            }
        }

        private bool Push<T>(NetPush<T> route, T message) where T : class
        {
            catalog.RequireRegistered(route);
            Cancellation.ThrowIfCancellationRequested();
            if (Completion.IsCompleted)
            {
                throw new NetworkException("Connection is already closed.");
            }

            OwnedFrame frame = sendMemory.Allocate(32 + route.MaxBodyBytes);
            bool transferred = false;
            try
            {
                var writer = new BoundedFrameWriter(frame);
                route.Codec.Encode(writer, message);
                writer.Complete(FrameKind.Push, route.Opcode, 0);
                frame.Delivery = route.Delivery;
                transferred = enqueue(frame);
                return transferred;
            }
            finally
            {
                if (!transferred)
                {
                    frame.Dispose();
                }
            }
        }
    }

    public sealed class ServerApplication
    {
        private delegate Task<OwnedFrame?> Handler(ServerConnectionContext connection, OwnedFrame request, FrameMemoryBudget memory);
        private readonly Dictionary<uint, Handler> handlers = new Dictionary<uint, Handler>();
        private readonly Func<ServerConnectionContext, ServerSession> createSession;
        private readonly object gate = new object();
        private bool frozen;
        public NetProtocolCatalog Catalog { get; }

        public ServerApplication(NetProtocolCatalog catalog, Func<ServerConnectionContext, ServerSession>? createSession = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.createSession = createSession ?? (_ => new ServerSession());
        }

        public void Rpc<TRequest, TResponse>(NetRpc<TRequest, TResponse> route,
            Func<ServerConnectionContext, TRequest, CancellationToken, Task<TResponse>> handler,
            Action<TResponse>? releaseResponse = null) where TRequest : class where TResponse : class
        {
            ArgumentNullException.ThrowIfNull(handler);
            Register(route, async (connection, frame, memory) =>
            {
                TRequest request = route.RequestCodec.Decode(frame.Body);
                TResponse response = await handler(connection, request, connection.Cancellation).ConfigureAwait(false);
                OwnedFrame encoded;
                try
                {
                    encoded = EncodeResponse(route, frame.Header, response, memory);
                }
                catch
                {
                    try
                    {
                        releaseResponse?.Invoke(response);
                    }
                    catch (Exception cleanup)
                    {
                        connection.ReportSecondaryFailure(cleanup);
                    }

                    throw;
                }

                try
                {
                    releaseResponse?.Invoke(response);
                    return encoded;
                }
                catch
                {
                    encoded.Dispose();
                    throw;
                }
            });
        }

        public void Notify<T>(NetSend<T> route, Func<ServerConnectionContext, T, CancellationToken, Task> handler) where T : class
        {
            ArgumentNullException.ThrowIfNull(handler);
            Register(route, async (connection, frame, _) =>
            {
                await handler(connection, route.Codec.Decode(frame.Body), connection.Cancellation).ConfigureAwait(false);
                return null;
            });
        }

        public void Notify<T>(NetUnreliable<T> route, Func<ServerConnectionContext, T, CancellationToken, Task> handler) where T : class
        {
            ArgumentNullException.ThrowIfNull(handler);
            Register(route, async (connection, frame, _) =>
            {
                await handler(connection, route.Codec.Decode(frame.Body), connection.Cancellation).ConfigureAwait(false);
                return null;
            });
        }

        public void Reject(NetRoute route, int statusCode = 2)
        {
            ArgumentNullException.ThrowIfNull(route);
            if (statusCode == 0 || route.Kind == NetRouteKind.Push)
            {
                throw new ArgumentException("Only inbound routes may be rejected with a nonzero status.");
            }

            Register(route, (_, _, _) => throw new ServerRequestException(statusCode));
        }

        private void Register(NetRoute route, Handler handler)
        {
            lock (gate)
            {
                if (frozen)
                {
                    throw new InvalidOperationException("Application routes are frozen after host construction.");
                }

                Catalog.RequireRegistered(route);
                if (!handlers.TryAdd(route.Opcode, handler))
                {
                    throw new ArgumentException("Duplicate application route.", nameof(route));
                }
            }
        }

        internal void Freeze()
        {
            lock (gate)
            {
                foreach (NetRoute route in Catalog.Routes)
                {
                    if (route.Kind != NetRouteKind.Push && !handlers.ContainsKey(route.Opcode))
                    {
                        throw new ArgumentException($"Inbound route {route.Opcode} needs a handler or explicit rejection.");
                    }
                }

                frozen = true;
            }
        }

        internal void Initialize(ServerConnectionContext connection)
        {
            connection.Session = createSession(connection) ?? throw new InvalidOperationException("Application session factory returned null.");
            if (!connection.Session.RequiresRestrictedTraffic)
            {
                connection.EnableFullTraffic();
            }
        }

        internal async Task<OwnedFrame?> DispatchAsync(ServerConnectionContext connection, OwnedFrame frame, FrameMemoryBudget memory)
        {
            try
            {
                connection.Cancellation.ThrowIfCancellationRequested();
                ServerAdmission admission = connection.Session.CheckDispatch(Header(frame));
                if (!admission.Allowed)
                {
                    throw new ServerRequestException(admission.StatusCode == 0 ? 2 : admission.StatusCode, admission.CloseReason);
                }

                return await handlers[frame.Header.Opcode](connection, frame, memory).ConfigureAwait(false);
            }
            catch (ServerRequestException error)
            {
                if (error.CloseReason != 0)
                {
                    connection.CloseAfterResponse(error.CloseReason);
                }

                if (frame.Header.Kind != FrameKind.Request)
                {
                    throw new NetworkProtocolException("Application rejected Notify.", error);
                }

                OwnedFrame response = memory.Allocate(32);
                new BoundedFrameWriter(response).Complete(FrameKind.Response, frame.Header.Opcode, frame.Header.CorrelationId, error.StatusCode);
                return response;
            }
        }

        internal static ServerMessageHeader Header(OwnedFrame frame) => new ServerMessageHeader(frame.Header.Opcode,
            frame.Header.CorrelationId, frame.Header.BodyLength, frame.Delivery);

        private static OwnedFrame EncodeResponse<TRequest, TResponse>(NetRpc<TRequest, TResponse> route, FrameHeader request,
            TResponse response, FrameMemoryBudget memory) where TRequest : class where TResponse : class
        {
            OwnedFrame frame = memory.Allocate(32 + route.ResponseMaxBodyBytes);
            try
            {
                var writer = new BoundedFrameWriter(frame);
                route.ResponseCodec.Encode(writer, response);
                writer.Complete(FrameKind.Response, request.Opcode, request.CorrelationId);
                return frame;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }
    }
}
