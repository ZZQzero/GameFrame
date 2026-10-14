namespace Game.Server.Gomoku
{
    internal sealed class GomokuOptions
    {
        internal int MaxRooms { get; init; } = 64;
        internal int MaxBindings { get; init; } = 128;
        internal TimeSpan WaitingLifetime { get; init; } = TimeSpan.FromMinutes(10);
        internal TimeSpan GameLifetime { get; init; } = TimeSpan.FromHours(1);
        internal TimeSpan FinishedLifetime { get; init; } = TimeSpan.FromSeconds(60);
        internal TimeSpan CommandLifetime { get; init; } = TimeSpan.FromSeconds(120);

        internal void Validate()
        {
            if (MaxRooms < 1 || MaxRooms > 64 || MaxBindings < 2 || MaxBindings > 128 ||
                WaitingLifetime <= TimeSpan.Zero || GameLifetime <= TimeSpan.Zero ||
                FinishedLifetime <= TimeSpan.Zero || CommandLifetime <= TimeSpan.Zero ||
                WaitingLifetime > TimeSpan.FromDays(1) || GameLifetime > TimeSpan.FromDays(1) ||
                FinishedLifetime > TimeSpan.FromDays(1) ||
                CommandLifetime > TimeSpan.FromSeconds(120))
            {
                throw new ArgumentException("Invalid Gomoku room capacities or lifetimes.");
            }
        }
    }
}
