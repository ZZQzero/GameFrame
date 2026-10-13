namespace Game.Hotfix
{
    /// <summary>已登录账号的只读业务信息；认证凭据由会话内部持有。</summary>
    public sealed class GameUser
    {
        public ulong AccountId { get; }
        public string AccountName { get; }
        public ulong AuthEpoch { get; }
        public long AccountDeadlineMilliseconds { get; }

        internal GameUser(ulong accountId, string accountName, ulong authEpoch, long accountDeadlineMilliseconds)
        {
            AccountId = accountId;
            AccountName = accountName;
            AuthEpoch = authEpoch;
            AccountDeadlineMilliseconds = accountDeadlineMilliseconds;
        }
    }
}
