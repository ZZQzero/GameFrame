// Generated from Protocol/*.proto by Tools/NetworkProtocol. Do not edit.
#nullable enable
using System;
using GameFrame.Network;
using MemoryPack;

namespace Game.Hotfix.Network.Protocol
{
    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class BattleGrantResponse
    {
        [MemoryPackOrder(0)]
        public uint ResultCode { get; set; }

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
        public float X { get; set; }

        [MemoryPackOrder(2)]
        public float Y { get; set; }

        [MemoryPackOrder(3)]
        public float Z { get; set; }

        [MemoryPackOrder(4)]
        public uint Tick { get; set; }

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
        public uint ResultCode { get; set; }

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
    public sealed partial class MoveCommand
    {
        [MemoryPackOrder(0)]
        public ulong CommandId { get; set; }

        [MemoryPackOrder(1)]
        public uint ClientTick { get; set; }

        [MemoryPackOrder(2)]
        public short DirectionX { get; set; }

        [MemoryPackOrder(3)]
        public short DirectionZ { get; set; }

        [MemoryPackOrder(4)]
        public ulong EntityId { get; set; }

        [MemoryPackOrder(5)]
        public ulong WorldEpoch { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class OperationResponse
    {
        [MemoryPackOrder(0)]
        public uint ResultCode { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class RequestBattleGrantRequest
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
    public sealed partial class WorldEvent
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(1)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(2)]
        public ulong EventVersion { get; set; }

        [MemoryPackOrder(3)]
        public global::Game.Hotfix.Network.Protocol.EntityState Entity { get; set; } = new global::Game.Hotfix.Network.Protocol.EntityState();

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class WorldResponse
    {
        [MemoryPackOrder(0)]
        public uint ResultCode { get; set; }

        [NetArrayLimit(16)]
        [MemoryPackOrder(1)]
        public byte[] WorldSessionId { get; set; } = Array.Empty<byte>();

        [MemoryPackOrder(2)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(3)]
        public ulong EventVersion { get; set; }

        [MemoryPackOrder(4)]
        public uint Tick { get; set; }

        [NetArrayLimit(64)]
        [MemoryPackOrder(5)]
        public global::Game.Hotfix.Network.Protocol.EntityState[] Entities { get; set; } = Array.Empty<global::Game.Hotfix.Network.Protocol.EntityState>();

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class WorldSnapshot
    {
        [MemoryPackOrder(0)]
        public ulong WorldEpoch { get; set; }

        [MemoryPackOrder(1)]
        public uint Tick { get; set; }

        [NetArrayLimit(16)]
        [MemoryPackOrder(2)]
        public global::Game.Hotfix.Network.Protocol.EntityState[] Entities { get; set; } = Array.Empty<global::Game.Hotfix.Network.Protocol.EntityState>();

    }

}
