// Generated from Protocol/*.proto by DotNet/NetworkProtocol. Do not edit.
#nullable enable
using System;
using GameFrame.Network;
using MemoryPack;

namespace Game.Protocol
{
    public enum GomokuEndReason : int
    {
        NoReason = 0,
        FiveInRow = 1,
        BoardFull = 2,
        PlayerLeft = 3,
        Disconnected = 4,
        Expired = 5,
        ServiceStopped = 6,
    }

    public enum GomokuOutcome : int
    {
        NoOutcome = 0,
        BlackVictory = 1,
        WhiteVictory = 2,
        Draw = 3,
        Aborted = 4,
    }

    public enum GomokuResult : int
    {
        Ok = 0,
        RoomNotFound = 1,
        RoomFull = 2,
        AlreadyInRoom = 3,
        InvalidState = 4,
        NotHost = 5,
        NotReady = 6,
        StaleVersion = 7,
        InvalidMove = 8,
        InvalidCommand = 9,
        Overloaded = 10,
        RateLimited = 11,
        UnknownCommand = 12,
    }

    public enum GomokuRoomState : int
    {
        Waiting = 0,
        Playing = 1,
        Finished = 2,
        Closed = 3,
    }

    public enum ResultCode : int
    {
        Success = 0,
        InvalidCredentials = 1,
        SessionExpired = 2,
        SessionRevoked = 3,
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
    public sealed partial class GomokuCommand
    {
        [MemoryPackOrder(0)]
        public ulong CommandId { get; set; }

        [NetStringLimit(32, 32)]
        [MemoryPackOrder(1)]
        public string RoomId { get; set; } = string.Empty;

        [MemoryPackOrder(2)]
        public ulong ExpectedVersion { get; set; }

        [NetStringLimit(8, 8)]
        [MemoryPackOrder(3)]
        public string RoomCode { get; set; } = string.Empty;

        [MemoryPackOrder(4)]
        public bool Ready { get; set; }

        [MemoryPackOrder(5)]
        public int Column { get; set; }

        [MemoryPackOrder(6)]
        public int Row { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class GomokuMember
    {
        [MemoryPackOrder(0)]
        public ulong AccountId { get; set; }

        [MemoryPackOrder(1)]
        public ulong MemberId { get; set; }

        [MemoryPackOrder(2)]
        public bool Ready { get; set; }

        [MemoryPackOrder(3)]
        public bool IsHost { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class GomokuMoveData
    {
        [MemoryPackOrder(0)]
        public int Column { get; set; }

        [MemoryPackOrder(1)]
        public int Row { get; set; }

        [MemoryPackOrder(2)]
        public int Stone { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class GomokuQuery
    {
        [MemoryPackOrder(0)]
        public ulong CommandId { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class GomokuResponse
    {
        [MemoryPackOrder(0)]
        public global::Game.Protocol.GomokuResult Result { get; set; }

        [MemoryPackOrder(1)]
        public ulong CommandId { get; set; }

        [MemoryPackOrder(2)]
        public bool CommandKnown { get; set; }

        [MemoryPackOrder(3)]
        public global::Game.Protocol.GomokuSnapshot? Snapshot { get; set; }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class GomokuSnapshot
    {
        [NetStringLimit(32, 32)]
        [MemoryPackOrder(0)]
        public string RoomId { get; set; } = string.Empty;

        [NetStringLimit(8, 8)]
        [MemoryPackOrder(1)]
        public string RoomCode { get; set; } = string.Empty;

        [MemoryPackOrder(2)]
        public ulong Version { get; set; }

        [MemoryPackOrder(3)]
        public global::Game.Protocol.GomokuRoomState State { get; set; }

        [MemoryPackOrder(4)]
        public global::Game.Protocol.GomokuOutcome Outcome { get; set; }

        [MemoryPackOrder(5)]
        public global::Game.Protocol.GomokuEndReason EndReason { get; set; }

        [NetArrayLimit(2)]
        [MemoryPackOrder(6)]
        public global::Game.Protocol.GomokuMember[] Members { get; set; } = Array.Empty<global::Game.Protocol.GomokuMember>();

        [NetArrayLimit(225)]
        [MemoryPackOrder(7)]
        public global::Game.Protocol.GomokuMoveData[] Moves { get; set; } = Array.Empty<global::Game.Protocol.GomokuMoveData>();

        [MemoryPackOrder(8)]
        public ulong RecipientAccountId { get; set; }

        [MemoryPackOrder(9)]
        public ulong RecipientAuthEpoch { get; set; }

        [MemoryPackOrder(10)]
        public ulong RecipientMemberId { get; set; }

        [MemoryPackOrder(11)]
        public ulong AdmissionCommandId { get; set; }

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
        public global::Game.Protocol.ResultCode ResultCode { get; set; }

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
    public sealed partial class OperationResponse
    {
        [MemoryPackOrder(0)]
        public global::Game.Protocol.ResultCode ResultCode { get; set; }

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
    public sealed partial class ResumeLoginRequest
    {
        [NetArrayLimit(16)]
        [MemoryPackOrder(0)]
        public byte[] AccountSessionId { get; set; } = Array.Empty<byte>();

        [NetArrayLimit(32)]
        [MemoryPackOrder(1)]
        public byte[] ResumeToken { get; set; } = Array.Empty<byte>();

    }

}
