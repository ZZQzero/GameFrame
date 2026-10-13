namespace Game.Server.Sessions
{
    // A snapshot identifies an authenticated binding; callers must recheck the session
    // after external IO. It is neither a resume credential nor permanent authorization.
    internal readonly record struct AccountIdentity(ulong AccountId, ulong AuthEpoch, long BindingId);
}
