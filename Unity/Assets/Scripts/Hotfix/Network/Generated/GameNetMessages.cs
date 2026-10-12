// Generated from Protocol/*.proto by Tools/NetworkProtocol. Do not edit.
#nullable enable
using System;
using GameFrame.Network;
using MemoryPack;

namespace Game.Hotfix.Network.Protocol
{
    public enum ResultCode : int
    {
        Success = 0,
        InvalidCredentials = 1,
        SessionExpired = 2,
        SessionRevoked = 3,
        CharacterNotOwned = 4,
        WorldUnavailable = 5,
        GrantExpired = 6,
        GrantConsumed = 7,
        ConflictingRequest = 8,
        InvalidWorldSession = 9,
        TooManyEntities = 10,
        InvalidInput = 11,
        AccountAlreadyExists = 12,
    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class EchoRequest
    {
        [NetStringLimit(128, 512)]
        [MemoryPackOrder(0)]
        public string Text { get; set; } = string.Empty;

        [MemoryPackOrder(1)]
        public uint DelayMs { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class EchoResponse
    {
        [NetStringLimit(128, 512)]
        [MemoryPackOrder(0)]
        public string Text { get; set; } = string.Empty;

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class EnterWorldRequest
    {
        [MemoryPackOrder(0)]
        public ulong CharacterId { get; set; }

        [MemoryPackOrder(1)]
        public ulong WorldId { get; set; }

        [MemoryPackOrder(2)]
        public ulong AuthEpoch { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class EntityState
    {
        [MemoryPackOrder(0)]
        public ulong EntityId { get; set; }

        [MemoryPackOrder(1)]
        public uint Incarnation { get; set; }

        [MemoryPackOrder(2)]
        public int PositionXMm { get; set; }

        [MemoryPackOrder(3)]
        public int PositionYMm { get; set; }

        [MemoryPackOrder(4)]
        public int PositionZMm { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class LeaveWorldRequest
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(1)]
        public ulong WorldEpoch { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class LoginRequest
    {
        [NetStringLimit(64, 256)]
        [MemoryPackOrder(0)]
        public string Account { get; set; } = string.Empty;

        [NetStringLimit(128, 512)]
        [MemoryPackOrder(1)]
        public string Password { get; set; } = string.Empty;

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class LoginResponse
    {
        [MemoryPackOrder(0)]
        public global::Game.Hotfix.Network.Protocol.ResultCode ResultCode { get; set; }

        [MemoryPackOrder(1)]
        public ulong AccountId { get; set; }

        [NetArrayLimit(16)]
        [MemoryPackOrder(2)]
        public byte[] AccountSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(3)]
        public ulong AuthEpoch { get; set; }

        [NetArrayLimit(32)]
        [MemoryPackOrder(4)]
        public byte[] ResumeToken { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(5)]
        public long ResumeExpiresAtUtcMs { get; set; }

        [MemoryPackOrder(6)]
        public long AccountExpiresAtUtcMs { get; set; }

        [MemoryPackOrder(7)]
        public uint ResumeRemainingTtlMs { get; set; }

        [MemoryPackOrder(8)]
        public uint AccountRemainingTtlMs { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class LogoutRequest
    {
    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class MoveInput
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(1)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(2)]
        public uint InputSequence { get; set; }

        [MemoryPackOrder(3)]
        public uint ClientTick { get; set; }

        [MemoryPackOrder(4)]
        public short DirectionX { get; set; }

        [MemoryPackOrder(5)]
        public short DirectionZ { get; set; }

        [MemoryPackOrder(6)]
        public ulong EntityId { get; set; }

        [MemoryPackOrder(7)]
        public uint Incarnation { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class OperationResponse
    {
        [MemoryPackOrder(0)]
        public global::Game.Hotfix.Network.Protocol.ResultCode ResultCode { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class RegisterRequest
    {
        [NetStringLimit(64, 256)]
        [MemoryPackOrder(0)]
        public string Account { get; set; } = string.Empty;

        [NetStringLimit(128, 512)]
        [MemoryPackOrder(1)]
        public string Password { get; set; } = string.Empty;

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class RequestWorldGrantRequest
    {
        [MemoryPackOrder(0)]
        public ulong GrantRequestId { get; set; }

        [MemoryPackOrder(1)]
        public ulong CharacterId { get; set; }

        [MemoryPackOrder(2)]
        public ulong WorldId { get; set; }

        [NetArrayLimit(32)]
        [MemoryPackOrder(3)]
        public byte[] ProtocolHash { get; set; } = Array.Empty<byte>();

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class RestoreWorldRequest
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(1)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(2)]
        public ulong LastEventVersion { get; set; }

        [MemoryPackOrder(3)]
        public ulong AuthEpoch { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class ResumeLoginRequest
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] AccountSessionId { get; set; } = Array.Empty<byte>();

        [NetArrayLimit(32)]
        [MemoryPackOrder(1)]
        public byte[] ResumeToken { get; set; } = Array.Empty<byte>();

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class WorldEntityEntered
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(1)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(2)]
        public ulong EventVersion { get; set; }

        [MemoryPackOrder(3)]
        public uint ServerTick { get; set; }

        [MemoryPackOrder(4)]
        public global::Game.Hotfix.Network.Protocol.EntityState Entity { get; set; } = new global::Game.Hotfix.Network.Protocol.EntityState();

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class WorldEntityLeft
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(1)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(2)]
        public ulong EventVersion { get; set; }

        [MemoryPackOrder(3)]
        public ulong EntityId { get; set; }

        [MemoryPackOrder(4)]
        public uint Incarnation { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class WorldGrantResponse
    {
        [MemoryPackOrder(0)]
        public global::Game.Hotfix.Network.Protocol.ResultCode ResultCode { get; set; }

        [MemoryPackOrder(1)]
        public ulong GrantId { get; set; }

        [NetStringLimit(256, 1024)]
        [MemoryPackOrder(2)]
        public string Host { get; set; } = string.Empty;

        [MemoryPackOrder(3)]
        public ushort Port { get; set; }

        [NetArrayLimit(512)]
        [MemoryPackOrder(4)]
        public byte[] Ticket { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(5)]
        public uint Conv { get; set; }

        [NetArrayLimit(16)]
        [MemoryPackOrder(6)]
        public byte[] PskIdentity { get; set; } = Array.Empty<byte>();

        [NetArrayLimit(32)]
        [MemoryPackOrder(7)]
        public byte[] Psk { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(8)]
        public long ExpiresAtUtcMs { get; set; }

        [MemoryPackOrder(9)]
        public uint RemainingTtlMs { get; set; }

        [MemoryPackOrder(10)]
        public uint ReliableLimit { get; set; }

        [MemoryPackOrder(11)]
        public uint UnreliableLimit { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class WorldResponse
    {
        [MemoryPackOrder(0)]
        public global::Game.Hotfix.Network.Protocol.ResultCode ResultCode { get; set; }

        [NetArrayLimit(16)]
        [MemoryPackOrder(1)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(2)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(3)]
        public ulong EventVersion { get; set; }

        [MemoryPackOrder(4)]
        public uint ServerTick { get; set; }

        [NetArrayLimit(64)]
        [MemoryPackOrder(5)]
        public global::Game.Hotfix.Network.Protocol.EntityState[] Entities { get; set; } = Array.Empty<global::Game.Hotfix.Network.Protocol.EntityState>();

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class WorldSnapshot
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(1)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(2)]
        public uint ServerTick { get; set; }

        [MemoryPackOrder(3)]
        public uint SnapshotSequence { get; set; }

        [NetArrayLimit(16)]
        [MemoryPackOrder(4)]
        public global::Game.Hotfix.Network.Protocol.EntityState[] Entities { get; set; } = Array.Empty<global::Game.Hotfix.Network.Protocol.EntityState>();

    }

}
