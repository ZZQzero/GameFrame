namespace Game.Server.Login
{
    internal sealed class LoginOptions
    {
        internal int MaxLoginAttemptsPerSecond { get; init; } = 5;
        internal TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(10);
        internal TimeSpan AccountLifetime { get; init; } = TimeSpan.FromHours(24);
        internal int MaxSessions { get; init; } = 1024;

        internal void Validate()
        {
            if (MaxLoginAttemptsPerSecond < 1 || LoginTimeout <= TimeSpan.Zero || LoginTimeout > TimeSpan.FromSeconds(10) ||
                AccountLifetime <= TimeSpan.Zero || AccountLifetime > TimeSpan.FromHours(24) || MaxSessions < 1)
            {
                throw new ArgumentException("Invalid login policy.");
            }
        }
    }
}
