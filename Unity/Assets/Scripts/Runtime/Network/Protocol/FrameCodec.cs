using System;
using System.Buffers.Binary;

namespace GameFrame.Network
{
    internal enum FrameKind : byte
    {
        Request = 1,
        Response = 2,
        Push = 3,
        Notify = 4,
        Control = 5
    }

    internal enum FrameDirection
    {
        ClientToServer = 1,
        ServerToClient = 2
    }

    internal readonly struct FrameHeader
    {
        public FrameKind Kind { get; }
        public uint Opcode { get; }
        public ulong CorrelationId { get; }
        public int BodyLength { get; }
        public int StatusCode { get; }

        public FrameHeader(FrameKind kind, uint opcode, ulong correlationId, int bodyLength, int statusCode = 0)
        {
            Kind = kind;
            Opcode = opcode;
            CorrelationId = correlationId;
            BodyLength = bodyLength;
            StatusCode = statusCode;
        }
    }

    /// <summary>只处理固定帧；路由类型和连接阶段由连接状态机校验。</summary>
    internal static class FrameCodec
    {
        public const uint Magic = 0x504E4647;
        public const ushort WireVersion = 1;
        public const int HeaderBytes = 32;
        public const int MaxReliableFrameBytes = 32768;
        public const int MaxUnreliableFrameBytes = 1150;

        // WebSocket 头齐全后即可调用，不需要等待正文完成或分配声明长度。
        public static FrameHeader ReadHeader(ReadOnlySpan<byte> bytes, int frameLimit, NetDelivery delivery,
            FrameDirection direction, bool handshaking)
        {
            ValidateLimit(frameLimit, delivery);
            if (direction != FrameDirection.ClientToServer && direction != FrameDirection.ServerToClient)
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }

            if (bytes.Length < HeaderBytes)
            {
                throw new NetworkProtocolException("Truncated frame header.");
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4)) != WireVersion ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6)) != HeaderBytes)
            {
                throw new NetworkProtocolException("Invalid frame magic, wire version or header size.");
            }

            if (bytes[9] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(10)) != 0)
            {
                throw new NetworkProtocolException("Unsupported flags or reserved bits.");
            }

            uint bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24));
            if (bodyLength > (uint)(frameLimit - HeaderBytes))
            {
                throw new NetworkProtocolException("Declared body exceeds the channel limit.");
            }

            var header = new FrameHeader((FrameKind)bytes[8],
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(16)), (int)bodyLength,
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28)));
            ValidateFields(header);
            ValidateDirection(header, direction);
            if (handshaking && header.Kind != FrameKind.Control)
            {
                throw new NetworkProtocolException("Business frames are forbidden before Hello completes.");
            }

            if (delivery == NetDelivery.Unreliable &&
                header.Kind != FrameKind.Push && header.Kind != FrameKind.Notify)
            {
                throw new NetworkProtocolException("Only Push and Notify may use the unreliable channel.");
            }

            return header;
        }

        public static FrameHeader ReadFrame(ReadOnlySpan<byte> frame, int frameLimit, NetDelivery delivery,
            FrameDirection direction, bool handshaking)
        {
            FrameHeader header = ReadHeader(frame, frameLimit, delivery, direction, handshaking);
            if (frame.Length != HeaderBytes + header.BodyLength)
            {
                throw new NetworkProtocolException("The frame must contain exactly one complete message.");
            }

            return header;
        }

        public static void WriteHeader(Span<byte> destination, FrameHeader header)
        {
            if (destination.Length < HeaderBytes)
            {
                throw new ArgumentException("Header destination is too small.", nameof(destination));
            }

            ValidateFields(header);
            destination.Slice(0, HeaderBytes).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), WireVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6), HeaderBytes);
            destination[8] = (byte)header.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12), header.Opcode);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(16), header.CorrelationId);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24), (uint)header.BodyLength);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(28), header.StatusCode);
        }

        internal static void ValidateLimit(int frameLimit, NetDelivery delivery)
        {
            if (delivery != NetDelivery.Reliable && delivery != NetDelivery.Unreliable)
            {
                throw new ArgumentOutOfRangeException(nameof(delivery));
            }

            int hardLimit = delivery == NetDelivery.Reliable ? MaxReliableFrameBytes : MaxUnreliableFrameBytes;
            if (frameLimit < HeaderBytes || frameLimit > hardLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(frameLimit));
            }
        }

        private static void ValidateFields(FrameHeader header)
        {
            if (header.Kind < FrameKind.Request || header.Kind > FrameKind.Control)
            {
                throw new NetworkProtocolException("Unknown frame kind.");
            }

            if (header.BodyLength < 0 || header.BodyLength > MaxReliableFrameBytes - HeaderBytes)
            {
                throw new NetworkProtocolException("Invalid body length.");
            }

            bool rpc = header.Kind == FrameKind.Request || header.Kind == FrameKind.Response;
            if (rpc != (header.CorrelationId != 0))
            {
                throw new NetworkProtocolException("Invalid correlation ID for the frame kind.");
            }

            if (header.Kind == FrameKind.Control ? header.Opcode < 1 || header.Opcode > 6 : header.Opcode < 1000)
            {
                throw new NetworkProtocolException("Opcode does not belong to this frame kind.");
            }

            if (header.StatusCode != 0 && (header.Kind != FrameKind.Response || header.BodyLength != 0))
            {
                throw new NetworkProtocolException("Only empty error responses may have a nonzero status.");
            }
        }

        private static void ValidateDirection(FrameHeader header, FrameDirection direction)
        {
            bool clientFrame = header.Kind == FrameKind.Request || header.Kind == FrameKind.Notify;
            bool serverFrame = header.Kind == FrameKind.Response || header.Kind == FrameKind.Push;
            if ((direction == FrameDirection.ClientToServer && serverFrame) ||
                (direction == FrameDirection.ServerToClient && clientFrame))
            {
                throw new NetworkProtocolException("Frame kind has the wrong direction.");
            }

            if (header.Kind == FrameKind.Control &&
                ((direction == FrameDirection.ServerToClient && header.Opcode == 1) ||
                (direction == FrameDirection.ClientToServer && (header.Opcode == 2 || header.Opcode == 5))))
            {
                throw new NetworkProtocolException("Control opcode has the wrong direction.");
            }
        }
    }
}
