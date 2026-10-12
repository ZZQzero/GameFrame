namespace Game.Server
{
    internal static class LocalEnvironment
    {
        internal static string Required(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Required local configuration is missing: {name}.");
            }

            return value;
        }

        internal static int Port(string name, int defaultPort)
        {
            int port = int.Parse(Environment.GetEnvironmentVariable(name) ?? defaultPort.ToString());
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(name, "Local port must be between 1 and 65535.");
            }

            return port;
        }
    }
}
