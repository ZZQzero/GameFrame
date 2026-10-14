using System;
using System.Buffers.Binary;
using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed class NetworkProtocolTests
    {
        private static readonly byte[] PingVector =
        {
            0x47, 0x46, 0x4e, 0x50, 0x01, 0x00, 0x20, 0x00,
            0x05, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01
        };

        [Test]
        public void PingMatchesPublishedLittleEndianVector()
        {
            var bytes = new byte[40];
            FrameCodec.WriteHeader(bytes, new FrameHeader(FrameKind.Control, 3, 0, 8));
            ControlCodec.WriteNonce(bytes.AsSpan(32), 0x0102030405060708);
            CollectionAssert.AreEqual(PingVector, bytes);
            FrameHeader header = ReadServerFrame(PingVector);
            Assert.AreEqual(FrameKind.Control, header.Kind);
            Assert.AreEqual(3u, header.Opcode);
            Assert.AreEqual(8, header.BodyLength);
            Assert.AreEqual(0x0102030405060708ul, ControlCodec.ReadNonce(PingVector.AsSpan(32)));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(7)]
        [TestCase(31)]
        [TestCase(32)]
        [TestCase(39)]
        [TestCase(41)]
        [TestCase(80)]
        public void TruncationTrailingBytesAndConcatenationAreRejected(int length)
        {
            var bytes = new byte[length];
            PingVector.AsSpan(0, Math.Min(length, PingVector.Length)).CopyTo(bytes);
            Assert.Throws<NetworkProtocolException>(() => ReadServerFrame(bytes));
        }

        [TestCase(0, 0)]
        [TestCase(4, 2)]
        [TestCase(6, 31)]
        [TestCase(8, 0)]
        [TestCase(8, 6)]
        [TestCase(9, 1)]
        [TestCase(10, 1)]
        [TestCase(11, 1)]
        [TestCase(12, 0)]
        [TestCase(12, 7)]
        [TestCase(16, 1)]
        [TestCase(28, 1)]
        public void InvalidFixedFieldsAreRejected(int offset, byte value)
        {
            var bytes = (byte[])PingVector.Clone();
            bytes[offset] = value;
            Assert.Throws<NetworkProtocolException>(() => ReadServerFrame(bytes));
        }

        [TestCase(32737u)]
        [TestCase(0x7fffffffu)]
        [TestCase(0x80000000u)]
        [TestCase(0xffffffffu)]
        public void HugeDeclaredBodyIsRejectedFromHeaderAlone(uint length)
        {
            var bytes = new byte[32];
            PingVector.AsSpan(0, 32).CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), length);
            Assert.Throws<NetworkProtocolException>(() => FrameCodec.ReadHeader(bytes, 32768,
                NetDelivery.Reliable, FrameDirection.ServerToClient, false));
        }

        [TestCase(32768, NetDelivery.Reliable)]
        [TestCase(1150, NetDelivery.Unreliable)]
        public void ChannelBoundaryIsInclusive(int length, NetDelivery delivery)
        {
            var bytes = new byte[length];
            FrameCodec.WriteHeader(bytes, new FrameHeader(FrameKind.Push, 1000, 0, length - 32));
            Assert.AreEqual(length - 32, FrameCodec.ReadFrame(bytes, length, delivery,
                FrameDirection.ServerToClient, false).BodyLength);
            var oversized = new byte[length + 1];
            FrameCodec.WriteHeader(oversized, new FrameHeader(FrameKind.Push, 1000, 0, length - 32));
            BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(24), (uint)(length - 31));
            Assert.Throws<NetworkProtocolException>(() => FrameCodec.ReadFrame(oversized, length, delivery,
                FrameDirection.ServerToClient, false));
        }

        [TestCase(1, 0ul, false)]
        [TestCase(1, 1ul, true)]
        [TestCase(2, 0ul, false)]
        [TestCase(2, 1ul, true)]
        [TestCase(3, 0ul, true)]
        [TestCase(3, 1ul, false)]
        [TestCase(4, 0ul, true)]
        [TestCase(4, 1ul, false)]
        public void CorrelationRulesMatchFrameKind(int kind, ulong correlation, bool valid)
        {
            var bytes = new byte[32];
            if (valid)
            {
                Assert.DoesNotThrow(() => FrameCodec.WriteHeader(bytes,
                    new FrameHeader((FrameKind)kind, 1000, correlation, 0)));
            }
            else
            {
                Assert.Throws<NetworkProtocolException>(() => FrameCodec.WriteHeader(bytes,
                    new FrameHeader((FrameKind)kind, 1000, correlation, 0)));
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(999)]
        [TestCase(-1)]
        public void RemoteErrorsPreserveKnownAndUnknownCodes(int status)
        {
            var bytes = new byte[32];
            FrameCodec.WriteHeader(bytes, new FrameHeader(FrameKind.Response, 1000, 1, 0, status));
            Assert.AreEqual(status, ReadServerFrame(bytes).StatusCode);
            Assert.AreEqual(status, new NetworkRemoteException(status).StatusCode);
        }

        [Test]
        public void ErrorResponseCannotCarryDto()
        {
            Assert.Throws<NetworkProtocolException>(() => FrameCodec.WriteHeader(new byte[32],
                new FrameHeader(FrameKind.Response, 1000, 1, 1, 1)));
        }

        [Test]
        public void DirectionAndHandshakeAreValidatedBeforeBusinessBody()
        {
            var bytes = new byte[32];
            FrameCodec.WriteHeader(bytes, new FrameHeader(FrameKind.Request, 1000, 1, 0));
            Assert.Throws<NetworkProtocolException>(() => ReadServerFrame(bytes));
            Assert.Throws<NetworkProtocolException>(() => FrameCodec.ReadFrame(bytes, 32768,
                NetDelivery.Reliable, FrameDirection.ClientToServer, true));
            Assert.Throws<NetworkProtocolException>(() => FrameCodec.ReadFrame(bytes, 1150,
                NetDelivery.Unreliable, FrameDirection.ClientToServer, false));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(5)]
        public void HelloAndRejectHaveFixedDirection(int opcode)
        {
            var bytes = new byte[32];
            FrameCodec.WriteHeader(bytes, new FrameHeader(FrameKind.Control, (uint)opcode, 0, 0));
            FrameDirection direction = opcode == 1 ? FrameDirection.ServerToClient : FrameDirection.ClientToServer;
            Assert.Throws<NetworkProtocolException>(() => FrameCodec.ReadFrame(bytes, 32768,
                NetDelivery.Reliable, direction, true));
        }

        [Test]
        public void WssHelloHasEmptyTicketAndExact48ByteBody()
        {
            var bytes = new byte[48];
            var hash = MakeHash();
            Assert.AreEqual(48, ControlCodec.WriteClientHello(bytes, hash, 32768, 0, 0,
                ReadOnlySpan<byte>.Empty, NetworkTransportKind.WebSocket));
            ClientHelloBody hello = ControlCodec.ReadClientHello(bytes, NetworkTransportKind.WebSocket);
            CollectionAssert.AreEqual(hash, hello.Hash.ToArray());
            Assert.AreEqual(32768, hello.ReliableLimit);
            Assert.AreEqual(0, hello.UnreliableLimit);
            Assert.AreEqual(0, hello.Ticket.Length);
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ReadClientHello(bytes, NetworkTransportKind.Kcp));
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.WriteClientHello(new byte[49], hash,
                32768, 0, 0, new byte[1], NetworkTransportKind.WebSocket));
        }

        [Test]
        public void DuplicateHelloAndPreHelloPingAreRejectedByPhasePolicy()
        {
            ControlCodec.ValidatePhase(ControlOpcode.ServerHello, false, FrameDirection.ServerToClient);
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ValidatePhase(ControlOpcode.ServerHello,
                true, FrameDirection.ServerToClient));
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ValidatePhase(ControlOpcode.Ping,
                false, FrameDirection.ServerToClient));
            ControlCodec.ValidatePhase(ControlOpcode.Ping, true, FrameDirection.ServerToClient);
        }

        [Test]
        public void ConnectOptionsEnforceTrustAndGrantExpiryBeforeIo()
        {
            var endpoint = new Uri("wss://localhost:7443/network");
            var client = new NetworkClientOptions { TrustedWebSocketEndpoints = new[] { endpoint } };
            NetworkConnectOptions.WebSocket(endpoint).Validate(client, new NetProtocolCatalog(new byte[32]), 0);
            Assert.Throws<ArgumentException>(() => NetworkConnectOptions.WebSocket(new Uri("wss://untrusted.example/network"))
                .Validate(client, new NetProtocolCatalog(new byte[32]), 0));
            Assert.Throws<ArgumentException>(() => NetworkConnectOptions.Kcp(new Uri("udp://localhost:7444"),
                new byte[1], 0, new byte[16], new byte[32], 1));
            var udp = new Uri("udp://localhost:7444");
            var kcp = NetworkConnectOptions.Kcp(udp, new byte[1], 42, new byte[16], new byte[32], 1);
            client.TrustedUdpEndpoints = new[] { udp };
            Assert.Throws<NetworkException>(() => kcp.Validate(client, new NetProtocolCatalog(new byte[32]), 1));
        }

        [TestCase(1)]
        [TestCase(512)]
        [TestCase(1024)]
        public void KcpHelloHasBoundedNonemptyTicket(int length)
        {
            var ticket = new byte[length];
            ticket[0] = 42;
            var bytes = new byte[48 + length];
            Assert.AreEqual(bytes.Length, ControlCodec.WriteClientHello(bytes, MakeHash(), 32768, 1150, 1,
                ticket, NetworkTransportKind.Kcp));
            ClientHelloBody hello = ControlCodec.ReadClientHello(bytes, NetworkTransportKind.Kcp);
            CollectionAssert.AreEqual(ticket, hello.Ticket.ToArray());
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ReadClientHello(bytes, NetworkTransportKind.WebSocket));
        }

        [TestCase(0)]
        [TestCase(1025)]
        public void InvalidKcpTicketFailsBeforeWriting(int length)
        {
            var destination = new byte[48 + length];
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.WriteClientHello(destination,
                MakeHash(), 32768, 1150, 1, new byte[length], NetworkTransportKind.Kcp));
            CollectionAssert.AreEqual(new byte[destination.Length], destination);
        }

        [TestCase(44)]
        [TestCase(46)]
        public void HelloTicketLengthAndReservedBitsAreStrict(int offset)
        {
            byte[] bytes = WssHello();
            bytes[offset] = 1;
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ReadClientHello(bytes, NetworkTransportKind.WebSocket));
        }

        [TestCase(2047, 0, 0u)]
        [TestCase(32769, 0, 0u)]
        [TestCase(32768, 255, 1u)]
        [TestCase(32768, 1151, 1u)]
        [TestCase(32768, 1150, 0u)]
        [TestCase(32768, 1150, 3u)]
        public void InvalidHelloCapabilitiesAreRejected(int reliable, int unreliable, uint features)
        {
            var bytes = new byte[49];
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.WriteClientHello(bytes, MakeHash(),
                reliable, unreliable, features, new byte[1], NetworkTransportKind.Kcp));
        }

        [TestCase(0u)]
        [TestCase(2u)]
        [TestCase(0xffffffffu)]
        public void WssCannotNegotiateUnreliableCapability(uint features)
        {
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.WriteServerHello(new byte[64],
                MakeHash(), new byte[16], 32768, 256, features, NetworkTransportKind.WebSocket));
        }

        [Test]
        public void NegotiationRequiresMatchingHashAndEnoughRoomForCatalog()
        {
            byte[] client = WssHello();
            var server = new byte[64];
            ControlCodec.WriteServerHello(server, MakeHash(), new byte[16], 2048, 0, 0, NetworkTransportKind.WebSocket);
            ValidateWssNegotiation(client, server, 2016);
            Assert.Throws<NetworkProtocolException>(() => ValidateWssNegotiation(client, server, 2017));
            server[0] ^= 1;
            Assert.Throws<NetworkProtocolException>(() => ValidateWssNegotiation(client, server, 2016));
        }

        [Test]
        public void ServerCannotIncreaseProposalAndMustUseFixedHeartbeat()
        {
            var client = new byte[48];
            ControlCodec.WriteClientHello(client, MakeHash(), 2048, 0, 0, ReadOnlySpan<byte>.Empty,
                NetworkTransportKind.WebSocket);
            var server = new byte[64];
            ControlCodec.WriteServerHello(server, MakeHash(), new byte[16], 4096, 0, 0, NetworkTransportKind.WebSocket);
            Assert.Throws<NetworkProtocolException>(() => ValidateWssNegotiation(client, server, 1));
            BinaryPrimitives.WriteUInt32LittleEndian(server.AsSpan(60), 5001);
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ReadServerHello(server, NetworkTransportKind.WebSocket));
        }

        [Test]
        public void MaliciousHelloLimitDoesNotOverflow()
        {
            byte[] bytes = WssHello();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), uint.MaxValue);
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ReadClientHello(bytes, NetworkTransportKind.WebSocket));
        }

        [Test]
        public void UnknownRejectAndCloseReasonsArePreserved()
        {
            var reject = new byte[36];
            ControlCodec.WriteReject(reject, ushort.MaxValue, MakeHash());
            Assert.AreEqual(ushort.MaxValue, ControlCodec.ReadReject(reject, out ReadOnlySpan<byte> hash));
            CollectionAssert.AreEqual(MakeHash(), hash.ToArray());
            var close = new byte[4];
            ControlCodec.WriteClose(close, 99);
            Assert.AreEqual(99, ControlCodec.ReadClose(close));
            close[2] = 1;
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ReadClose(close));
        }

        [TestCase(7)]
        [TestCase(9)]
        public void NonceBodyMustHaveExactlyEightBytes(int length)
        {
            Assert.Throws<NetworkProtocolException>(() => ControlCodec.ReadNonce(new byte[length]));
        }

        [Test]
        public void SharedAttributesEnforceWireAndAllocationBudgets()
        {
            NetContractLimits.ValidateRoute(1000, NetDelivery.Reliable, 2048);
            Assert.Throws<ArgumentOutOfRangeException>(() => NetContractLimits.ValidateRoute(999, NetDelivery.Reliable, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => NetContractLimits.ValidateRoute(1000, NetDelivery.Unreliable, 1119));
            Assert.Throws<ArgumentOutOfRangeException>(() => NetContractLimits.ValidateRoute(1000, (NetDelivery)0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetStringLimitAttribute(257, 1024));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetStringLimitAttribute(256, 1025));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetArrayLimitAttribute(513));
        }

        [Test]
        public void InvalidHeaderDoesNotTouchDestination()
        {
            var destination = new byte[32];
            Assert.Throws<NetworkProtocolException>(() => FrameCodec.WriteHeader(destination,
                new FrameHeader(FrameKind.Control, 3, 0, -1)));
            CollectionAssert.AreEqual(new byte[32], destination);
        }

        [Test]
        public void DeterministicRandomInputHasOnlyProtocolFailures()
        {
            var random = new Random(467);
            for (int i = 0; i < 4096; i++)
            {
                var bytes = new byte[random.Next(0, 128)];
                random.NextBytes(bytes);
                Assert.Throws<NetworkProtocolException>(() => ReadServerFrame(bytes), $"Sample {i}");
            }
        }

        private static FrameHeader ReadServerFrame(byte[] frame)
        {
            return FrameCodec.ReadFrame(frame, 32768, NetDelivery.Reliable, FrameDirection.ServerToClient, false);
        }

        private static byte[] MakeHash()
        {
            var hash = new byte[32];
            for (int i = 0; i < hash.Length; i++)
            {
                hash[i] = (byte)i;
            }

            return hash;
        }

        private static byte[] WssHello()
        {
            var bytes = new byte[48];
            ControlCodec.WriteClientHello(bytes, MakeHash(), 32768, 0, 0, ReadOnlySpan<byte>.Empty,
                NetworkTransportKind.WebSocket);
            return bytes;
        }

        private static void ValidateWssNegotiation(byte[] client, byte[] server, int reliableBodyLimit)
        {
            ControlCodec.ValidateNegotiation(ControlCodec.ReadServerHello(server, NetworkTransportKind.WebSocket),
                ControlCodec.ReadClientHello(client, NetworkTransportKind.WebSocket), reliableBodyLimit, 1118);
        }
    }
}
