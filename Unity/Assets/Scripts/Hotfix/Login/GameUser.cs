namespace Game.Hotfix
{
    /// <summary>已登录账号的只读业务信息，不持有认证凭据。</summary>
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
