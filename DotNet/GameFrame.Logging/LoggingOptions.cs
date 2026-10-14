using Microsoft.Extensions.Logging;

namespace GameFrame.Logging
{
    public sealed record LoggingOptions
    {
        public required string Service { get; init; }
        public required string Environment { get; init; }
        public required string InstanceId { get; init; }
        public required string Version { get; init; }
        public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
        public IReadOnlyDictionary<string, LogLevel> CategoryLevels { get; init; } = new Dictionary<string, LogLevel>();

        public void Validate()
        {
            ValidateField(Service, nameof(Service));
            ValidateField(Environment, nameof(Environment));
            ValidateField(InstanceId, nameof(InstanceId));
            ValidateField(Version, nameof(Version));
            if (!Enum.IsDefined(MinimumLevel))
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumLevel));
            }

            ArgumentNullException.ThrowIfNull(CategoryLevels);
            foreach (KeyValuePair<string, LogLevel> entry in CategoryLevels)
            {
                ValidateField(entry.Key, nameof(CategoryLevels));
                if (!Enum.IsDefined(entry.Value))
                {
                    throw new ArgumentOutOfRangeException(nameof(CategoryLevels));
                }
            }
        }

        private static void ValidateField(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            {
                throw new ArgumentException("Log metadata must contain 1 to 128 characters without control characters.", name);
            }
        }
    }
}
