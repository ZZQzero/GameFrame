#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;

namespace GameFrame.Network
{
    public enum NetRouteKind
    {
        Rpc = 1,
        Push = 2,
        Notify = 3
    }

    public delegate void NetSerialize<T>(IBufferWriter<byte> writer, T value);
    public delegate T NetDeserialize<T>(ReadOnlySpan<byte> bytes);
    public delegate void NetValidateBytes(ReadOnlySpan<byte> bytes);

    public sealed class NetMessageCodec<T> where T : class
    {
        private readonly NetSerialize<T> serialize;
        private readonly NetDeserialize<T> deserialize;
        private readonly Action<T> validateObject;
        private readonly NetValidateBytes validateBytes;

        public NetMessageCodec(NetSerialize<T> serialize, NetDeserialize<T> deserialize,
            Action<T> validateObject, NetValidateBytes validateBytes)
        {
            this.serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
            this.deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
            this.validateObject = validateObject ?? throw new ArgumentNullException(nameof(validateObject));
            this.validateBytes = validateBytes ?? throw new ArgumentNullException(nameof(validateBytes));
        }

        internal void Encode(IBufferWriter<byte> writer, T value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            validateObject(value);
            serialize(writer, value);
        }

        internal T Decode(ReadOnlySpan<byte> bytes)
        {
            validateBytes(bytes);
            T value = deserialize(bytes);
            if (value == null)
            {
                throw new NetworkProtocolException("Root DTO must not be null.");
            }

            return value;
        }
    }

    public abstract class NetRoute
    {
        public uint Opcode { get; }
        public NetDelivery Delivery { get; }
        public int MaxBodyBytes { get; }
        public abstract NetRouteKind Kind { get; }
        internal abstract Type MessageType { get; }
        internal virtual Type? ResponseType => null;
        internal virtual int ResponseMaxBodyBytes => 0;

        private protected NetRoute(uint opcode, NetDelivery delivery, int maxBodyBytes)
        {
            NetContractLimits.ValidateRoute(opcode, delivery, maxBodyBytes);
            Opcode = opcode;
            Delivery = delivery;
            MaxBodyBytes = maxBodyBytes;
        }
    }

    public sealed class NetRpc<TRequest, TResponse> : NetRoute where TRequest : class where TResponse : class
    {
        public override NetRouteKind Kind => NetRouteKind.Rpc;
        internal NetMessageCodec<TRequest> RequestCodec { get; }
        internal NetMessageCodec<TResponse> ResponseCodec { get; }
        internal override Type MessageType => typeof(TRequest);
        internal override Type ResponseType => typeof(TResponse);
        internal override int ResponseMaxBodyBytes { get; }

        public NetRpc(uint opcode, int requestMaxBodyBytes, int responseMaxBodyBytes,
            NetMessageCodec<TRequest> requestCodec, NetMessageCodec<TResponse> responseCodec)
            : base(opcode, NetDelivery.Reliable, requestMaxBodyBytes)
        {
            NetContractLimits.ValidateRoute(opcode, NetDelivery.Reliable, responseMaxBodyBytes);
            RequestCodec = requestCodec ?? throw new ArgumentNullException(nameof(requestCodec));
            ResponseCodec = responseCodec ?? throw new ArgumentNullException(nameof(responseCodec));
            ResponseMaxBodyBytes = responseMaxBodyBytes;
        }
    }

    public sealed class NetPush<T> : NetRoute where T : class
    {
        public override NetRouteKind Kind => NetRouteKind.Push;
        internal NetMessageCodec<T> Codec { get; }
        internal override Type MessageType => typeof(T);

        public NetPush(uint opcode, NetDelivery delivery, int maxBodyBytes, NetMessageCodec<T> codec)
            : base(opcode, delivery, maxBodyBytes)
        {
            Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }
    }

    public sealed class NetSend<T> : NetRoute where T : class
    {
        public override NetRouteKind Kind => NetRouteKind.Notify;
        internal NetMessageCodec<T> Codec { get; }
        internal override Type MessageType => typeof(T);

        public NetSend(uint opcode, int maxBodyBytes, NetMessageCodec<T> codec)
            : base(opcode, NetDelivery.Reliable, maxBodyBytes)
        {
            Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }
    }

    public sealed class NetUnreliable<T> : NetRoute where T : class
    {
        public override NetRouteKind Kind => NetRouteKind.Notify;
        internal NetMessageCodec<T> Codec { get; }
        internal override Type MessageType => typeof(T);

        public NetUnreliable(uint opcode, int maxBodyBytes, NetMessageCodec<T> codec)
            : base(opcode, NetDelivery.Unreliable, maxBodyBytes)
        {
            Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }
    }

    public sealed class NetProtocolCatalog
    {
        private readonly byte[] hash;
        private readonly Dictionary<uint, NetRoute> routes;
        public int MaxReliableBodyBytes { get; }
        public int MaxUnreliableBodyBytes { get; }
        public int RouteCount => routes.Count;

        public NetProtocolCatalog(byte[] protocolHash, params NetRoute[] routes)
        {
            if (protocolHash == null || protocolHash.Length != 32)
            {
                throw new ArgumentException("Protocol hash must contain 32 bytes.", nameof(protocolHash));
            }

            if (routes == null)
            {
                throw new ArgumentNullException(nameof(routes));
            }

            hash = (byte[])protocolHash.Clone();
            this.routes = new Dictionary<uint, NetRoute>(routes.Length);
            foreach (NetRoute route in routes)
            {
                if (route == null)
                {
                    throw new ArgumentException("Catalog contains a null route.", nameof(routes));
                }

                if (this.routes.ContainsKey(route.Opcode))
                {
                    throw new ArgumentException($"Duplicate opcode {route.Opcode}.", nameof(routes));
                }

                this.routes.Add(route.Opcode, route);
                int bodyLimit = Math.Max(route.MaxBodyBytes, route.ResponseMaxBodyBytes);
                if (route.Delivery == NetDelivery.Reliable)
                {
                    MaxReliableBodyBytes = Math.Max(MaxReliableBodyBytes, bodyLimit);
                }
                else
                {
                    MaxUnreliableBodyBytes = Math.Max(MaxUnreliableBodyBytes, bodyLimit);
                }
            }
        }

        public void CopyHashTo(Span<byte> destination)
        {
            if (destination.Length < hash.Length)
            {
                throw new ArgumentException("Hash destination is too small.", nameof(destination));
            }

            hash.AsSpan().CopyTo(destination);
        }

        internal ReadOnlySpan<byte> Hash => hash;

        internal NetRoute Find(uint opcode)
        {
            if (!routes.TryGetValue(opcode, out NetRoute? route))
            {
                throw new NetworkProtocolException($"Unknown opcode {opcode}.");
            }

            return route;
        }

        internal void RequireRegistered(NetRoute route)
        {
            if (route == null)
            {
                throw new ArgumentNullException(nameof(route));
            }

            if (!routes.TryGetValue(route.Opcode, out NetRoute? registered) || !ReferenceEquals(route, registered))
            {
                throw new ArgumentException("Route does not belong to this client's frozen catalog.", nameof(route));
            }
        }
    }
}
