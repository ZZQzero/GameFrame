using System;
using System.Collections.Generic;
using Game.Protocol.Gomoku;

namespace Game.Hotfix
{
    /// <summary>已校验房间状态的只读副本，避免展示层修改网络 DTO 和成员数组。</summary>
    public sealed class GomokuRoomView
    {
        public string RoomId { get; }
        public string RoomCode { get; }
        public ulong Version { get; }
        public GomokuRoomState State { get; }
        public GomokuOutcome Outcome { get; }
        public GomokuEndReason EndReason { get; }
        public ulong RecipientAccountId { get; }
        public ulong RecipientAuthEpoch { get; }
        public ulong RecipientMemberId { get; }
        public ulong AdmissionCommandId { get; }
        public IReadOnlyList<GomokuMemberView> Members { get; }

        internal GomokuRoomView(GomokuSnapshot snapshot)
        {
            RoomId = snapshot.RoomId;
            RoomCode = snapshot.RoomCode;
            Version = snapshot.Version;
            State = snapshot.State;
            Outcome = snapshot.Outcome;
            EndReason = snapshot.EndReason;
            RecipientAccountId = snapshot.RecipientAccountId;
            RecipientAuthEpoch = snapshot.RecipientAuthEpoch;
            RecipientMemberId = snapshot.RecipientMemberId;
            AdmissionCommandId = snapshot.AdmissionCommandId;
            var members = new GomokuMemberView[snapshot.Members.Length];
            for (int i = 0; i < members.Length; i++)
            {
                GomokuMember member = snapshot.Members[i];
                members[i] = new GomokuMemberView(member.AccountId, member.MemberId, member.Ready, member.IsHost);
            }

            Members = Array.AsReadOnly(members);
        }
    }

    public readonly struct GomokuMemberView
    {
        public ulong AccountId { get; }
        public ulong MemberId { get; }
        public bool Ready { get; }
        public bool IsHost { get; }

        internal GomokuMemberView(ulong accountId, ulong memberId, bool ready, bool isHost)
        {
            AccountId = accountId;
            MemberId = memberId;
            Ready = ready;
            IsHost = isHost;
        }
    }
}
