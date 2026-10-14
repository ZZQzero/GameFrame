namespace GameFrame.Logging
{
    public enum NLogOutputFormat
    {
        Text,
        Json
    }

    public sealed record NLogOutputOptions
    {
        public NLogOutputFormat Format { get; init; } = NLogOutputFormat.Text;
        public int QueueCapacity { get; init; } = 4096;
        public int BatchSize { get; init; } = 200;
        public TimeSpan FlushTimeout { get; init; } = TimeSpan.FromSeconds(5);

        public void Validate()
        {
            if (!Enum.IsDefined(Format))
            {
                throw new ArgumentOutOfRangeException(nameof(Format));
            }

            if (QueueCapacity < 1 || QueueCapacity > 65536)
            {
                throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
            }

            if (BatchSize < 1 || BatchSize > QueueCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(BatchSize));
            }

            if (FlushTimeout <= TimeSpan.Zero || FlushTimeout > TimeSpan.FromSeconds(30))
            {
                throw new ArgumentOutOfRangeException(nameof(FlushTimeout));
            }
        }
    }
}
