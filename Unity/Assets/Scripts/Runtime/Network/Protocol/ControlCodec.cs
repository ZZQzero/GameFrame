using System;
using System.Buffers.Binary;

namespace GameFrame.Network
{
    internal enum ControlOpcode : uint
    {
        ClientHello = 1,
        ServerHello = 2,
        Ping = 3,
        Pong = 4,
        Reject = 5,
        Close = 6
    }

    internal readonly ref struct ClientHelloBody
    {
        public ReadOnlySpan<byte> Hash { get; }
        public int ReliableLimit { get; }
        public int UnreliableLimit { get; }
        public uint Features { get; }
        public ReadOnlySpan<byte> Ticket { get; }

        public ClientHelloBody(ReadOnlySpan<byte> hash, int reliableLimit, int unreliableLimit, uint features,
            ReadOnlySpan<byte> ticket)
        {
            Hash = hash;
            ReliableLimit = reliableLimit;
            UnreliableLimit = unreliableLimit;
            Features = features;
            Ticket = ticket;
        }
    }

    internal readonly ref struct ServerHelloBody
    {
        public ReadOnlySpan<byte> Hash { get; }
        public ReadOnlySpan<byte> SessionId { get; }
        public int ReliableLimit { get; }
        public int UnreliableLimit { get; }
        public uint Features { get; }
        public uint HeartbeatMs { get; }

        public ServerHelloBody(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> sessionId, int reliableLimit,
            int unreliableLimit, uint features, uint heartbeatMs)
        {
            Hash = hash;
            SessionId = sessionId;
            ReliableLimit = reliableLimit;
            UnreliableLimit = unreliableLimit;
            Features = features;
            HeartbeatMs = heartbeatMs;
        }
    }

    /// <summary>返回的 Span 只在调用者持有输入缓冲期间有效，不交给业务层。</summary>
    internal static class ControlCodec
    {
        public const uint UnreliableFeature = 1;
        public const uint HeartbeatMs = 5000;
        public const int HashBytes = 32;
        public const int SessionIdBytes = 16;
        public const int MaxTicketBytes = 1024;

        public static void ValidatePhase(ControlOpcode opcode, bool helloComplete, FrameDirection direction)
        {
            if (direction != FrameDirection.ClientToServer && direction != FrameDirection.ServerToClient)
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }

            bool allowed = helloComplete
                ? opcode == ControlOpcode.Ping || opcode == ControlOpcode.Pong || opcode == ControlOpcode.Close
                : opcode == ControlOpcode.Close ||
                    (direction == FrameDirection.ClientToServer && opcode == ControlOpcode.ClientHello) ||
                    (direction == FrameDirection.ServerToClient && (opcode == ControlOpcode.ServerHello || opcode == ControlOpcode.Reject));
            if (!allowed)
            {
                throw new NetworkProtocolException("Control packet is invalid for the current Hello phase.");
            }
        }

        // Header-time validation prevents allocating a large control body before discovering
        // that the selected fixed control packet cannot contain it.
        public static void ValidateBodyLength(ControlOpcode opcode, int bodyLength, NetworkTransportKind transport)
        {
            ValidateTransport(transport);
            bool valid;
            switch (opcode)
            {
                case ControlOpcode.ClientHello:
                    valid = transport == NetworkTransportKind.WebSocket ? bodyLength == 48 :
                        bodyLength >= 49 && bodyLength <= 48 + MaxTicketBytes;
                    break;
                case ControlOpcode.ServerHello:
                    valid = bodyLength == 64;
                    break;
                case ControlOpcode.Ping:
                case ControlOpcode.Pong:
                    valid = bodyLength == 8;
                    break;
                case ControlOpcode.Reject:
                    valid = bodyLength == 36;
                    break;
                case ControlOpcode.Close:
                    valid = bodyLength == 4;
                    break;
                default:
                    valid = false;
                    break;
            }

            if (!valid)
            {
                throw new NetworkProtocolException("Invalid control body length.");
            }
        }

        public static ClientHelloBody ReadClientHello(ReadOnlySpan<byte> body, NetworkTransportKind transport)
        {
            if (body.Length < 48)
            {
                throw new NetworkProtocolException("Truncated ClientHello.");
            }

            int ticketLength = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(44));
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(46)) != 0 || body.Length != 48 + ticketLength)
            {
                throw new NetworkProtocolException("Invalid ClientHello reserved bits or ticket length.");
            }

            ValidateTicket(transport, ticketLength);
            int reliableLimit = ReadLimit(body.Slice(32));
            int unreliableLimit = ReadLimit(body.Slice(36));
            uint features = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(40));
            ValidateCapabilities(transport, reliableLimit, unreliableLimit, features);
            return new ClientHelloBody(body.Slice(0, HashBytes), reliableLimit, unreliableLimit, features,
                body.Slice(48, ticketLength));
        }

        public static ServerHelloBody ReadServerHello(ReadOnlySpan<byte> body, NetworkTransportKind transport)
        {
            RequireLength(body, 64);
            int reliableLimit = ReadLimit(body.Slice(48));
            int unreliableLimit = ReadLimit(body.Slice(52));
            uint features = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(56));
            ValidateCapabilities(transport, reliableLimit, unreliableLimit, features);
            uint heartbeatMs = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(60));
            if (heartbeatMs != HeartbeatMs)
            {
                throw new NetworkProtocolException("V1 requires a 5000ms heartbeat interval.");
            }

            return new ServerHelloBody(body.Slice(0, HashBytes), body.Slice(32, SessionIdBytes), reliableLimit,
                unreliableLimit, features, heartbeatMs);
        }

        public static void ValidateNegotiation(ServerHelloBody hello, ClientHelloBody proposal,
            int catalogReliableBodyLimit, int catalogUnreliableBodyLimit)
        {
            if (catalogReliableBodyLimit < 0 || catalogReliableBodyLimit > FrameCodec.MaxReliableFrameBytes - FrameCodec.HeaderBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(catalogReliableBodyLimit));
            }

            if (catalogUnreliableBodyLimit < 0 || catalogUnreliableBodyLimit > FrameCodec.MaxUnreliableFrameBytes - FrameCodec.HeaderBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(catalogUnreliableBodyLimit));
            }

            if (!hello.Hash.SequenceEqual(proposal.Hash))
            {
                throw new NetworkProtocolException("Server protocol hash does not match the client catalog.");
            }

            if (hello.ReliableLimit > proposal.ReliableLimit || hello.UnreliableLimit > proposal.UnreliableLimit ||
                (hello.Features & ~proposal.Features) != 0)
            {
                throw new NetworkProtocolException("ServerHello exceeds the proposed capabilities.");
            }

            if (catalogReliableBodyLimit + FrameCodec.HeaderBytes > hello.ReliableLimit ||
                (hello.UnreliableLimit != 0 && catalogUnreliableBodyLimit + FrameCodec.HeaderBytes > hello.UnreliableLimit))
            {
                throw new NetworkProtocolException("Negotiated limits cannot carry every registered route.");
            }
        }

        public static int WriteClientHello(Span<byte> destination, ReadOnlySpan<byte> hash, int reliableLimit,
            int unreliableLimit, uint features, ReadOnlySpan<byte> ticket, NetworkTransportKind transport)
        {
            RequireHash(hash);
            ValidateTicket(transport, ticket.Length);
            ValidateCapabilities(transport, reliableLimit, unreliableLimit, features);
            int length = 48 + ticket.Length;
            RequireCapacity(destination, length);
            hash.CopyTo(destination);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(32), (uint)reliableLimit);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(36), (uint)unreliableLimit);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(40), features);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(44), (ushort)ticket.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(46), 0);
            ticket.CopyTo(destination.Slice(48));
            return length;
        }

        public static int WriteServerHello(Span<byte> destination, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> sessionId,
            int reliableLimit, int unreliableLimit, uint features, NetworkTransportKind transport)
        {
            RequireHash(hash);
            if (sessionId.Length != SessionIdBytes)
            {
                throw new ArgumentException("Session ID must contain 16 bytes.", nameof(sessionId));
            }

            ValidateCapabilities(transport, reliableLimit, unreliableLimit, features);
            RequireCapacity(destination, 64);
            hash.CopyTo(destination);
            sessionId.CopyTo(destination.Slice(32));
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(48), (uint)reliableLimit);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(52), (uint)unreliableLimit);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(56), features);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(60), HeartbeatMs);
            return 64;
        }

        public static ulong ReadNonce(ReadOnlySpan<byte> body)
        {
            RequireLength(body, 8);
            return BinaryPrimitives.ReadUInt64LittleEndian(body);
        }

        public static int WriteNonce(Span<byte> destination, ulong nonce)
        {
            RequireCapacity(destination, 8);
            BinaryPrimitives.WriteUInt64LittleEndian(destination, nonce);
            return 8;
        }

        public static ushort ReadClose(ReadOnlySpan<byte> body)
        {
            RequireLength(body, 4);
            return ReadReason(body);
        }

        public static ushort ReadReject(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> expectedHash)
        {
            RequireLength(body, 36);
            ushort reason = ReadReason(body);
            expectedHash = body.Slice(4, HashBytes);
            return reason;
        }

        public static int WriteClose(Span<byte> destination, ushort reason)
        {
            RequireCapacity(destination, 4);
            WriteReason(destination, reason);
            return 4;
        }

        public static int WriteReject(Span<byte> destination, ushort reason, ReadOnlySpan<byte> expectedHash)
        {
            RequireHash(expectedHash);
            RequireCapacity(destination, 36);
            WriteReason(destination, reason);
            expectedHash.CopyTo(destination.Slice(4));
            return 36;
        }

        private static int ReadLimit(ReadOnlySpan<byte> bytes)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (value > int.MaxValue)
            {
                throw new NetworkProtocolException("Hello limit exceeds the supported integer range.");
            }

            return (int)value;
        }

        private static void ValidateCapabilities(NetworkTransportKind transport, int reliableLimit,
            int unreliableLimit, uint features)
        {
            ValidateTransport(transport);
            if (reliableLimit < 2048 || reliableLimit > FrameCodec.MaxReliableFrameBytes ||
                (unreliableLimit != 0 && (unreliableLimit < 256 || unreliableLimit > FrameCodec.MaxUnreliableFrameBytes)))
            {
                throw new NetworkProtocolException("Hello limits are outside V1 bounds.");
            }

            if ((features & ~UnreliableFeature) != 0 ||
                (transport == NetworkTransportKind.WebSocket && (features != 0 || unreliableLimit != 0)) ||
                ((features & UnreliableFeature) == 0 && unreliableLimit != 0))
            {
                throw new NetworkProtocolException("Hello features and channel limits disagree.");
            }
        }

        private static void ValidateTicket(NetworkTransportKind transport, int length)
        {
            ValidateTransport(transport);
            if (transport == NetworkTransportKind.WebSocket ? length != 0 : length < 1 || length > MaxTicketBytes)
            {
                throw new NetworkProtocolException("Hello ticket is invalid for the selected transport.");
            }
        }

        private static void ValidateTransport(NetworkTransportKind transport)
        {
            if (transport != NetworkTransportKind.WebSocket && transport != NetworkTransportKind.Kcp)
            {
                throw new ArgumentOutOfRangeException(nameof(transport));
            }
        }

        private static ushort ReadReason(ReadOnlySpan<byte> body)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2)) != 0)
            {
                throw new NetworkProtocolException("Control reserved bits must be zero.");
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(body);
        }

        private static void WriteReason(Span<byte> destination, ushort reason)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination, reason);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), 0);
        }

        private static void RequireLength(ReadOnlySpan<byte> body, int length)
        {
            if (body.Length != length)
            {
                throw new NetworkProtocolException("Control body has an incorrect length.");
            }
        }

        private static void RequireCapacity(Span<byte> destination, int length)
        {
            if (destination.Length < length)
            {
                throw new ArgumentException("Control destination is too small.", nameof(destination));
            }
        }

        private static void RequireHash(ReadOnlySpan<byte> hash)
        {
            if (hash.Length != HashBytes)
            {
                throw new ArgumentException("Protocol hash must contain 32 bytes.", nameof(hash));
            }
        }
    }
}
