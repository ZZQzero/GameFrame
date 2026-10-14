using System.Globalization;
using System.Reflection;
using GameFrame.Logging;

namespace Game.Server.Configuration
{
    internal static class ServerLogging
    {
        internal static NLogLogging Create()
        {
            LogLevel level = Parse<LogLevel>("GF_LOG_LEVEL", LogLevel.Information);
            NLogOutputFormat format = Parse<NLogOutputFormat>("GF_LOG_FORMAT", NLogOutputFormat.Text);
            int queue = int.Parse(Environment.GetEnvironmentVariable("GF_LOG_QUEUE_CAPACITY") ?? "4096", CultureInfo.InvariantCulture);
            return NLogLogging.Create(new LoggingOptions
            {
                Service = "Game.Server",
                Environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Local",
                InstanceId = Guid.NewGuid().ToString("N"),
                Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                MinimumLevel = level,
                CategoryLevels = new Dictionary<string, LogLevel>
                {
                    ["Microsoft.AspNetCore"] = LogLevel.Warning,
                    ["Npgsql"] = LogLevel.Warning
                }
            }, new NLogOutputOptions
            {
                Format = format,
                QueueCapacity = queue,
                BatchSize = Math.Min(queue, 200)
            });
        }

        private static T Parse<T>(string name, T defaultValue) where T : struct, Enum
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value == null)
            {
                return defaultValue;
            }

            if (!Enum.TryParse(value, true, out T parsed) || !Enum.IsDefined(parsed))
            {
                throw new ArgumentException("Invalid logging option.", name);
            }

            return parsed;
        }
    }
}
