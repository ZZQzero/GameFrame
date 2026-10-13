namespace Game.Server
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            string profile = ParseProfile(args);
            switch (profile)
            {
                case "local-wss":
                    await LocalWssService.RunAsync();
                    break;
                case "local-kcp-dtls":
                    await LocalKcpService.RunAsync();
                    break;
                default:
                    throw new InvalidOperationException("Production hosting is not configured. Only explicit local profiles are available.");
            }
        }

        private static string ParseProfile(string[] args)
        {
            if (args.Length == 0)
            {
                throw new InvalidOperationException("Production hosting is not configured. Only explicit local profiles are available. " +
                    "For local development, select the local-wss/local-kcp-dtls launch profile or pass --profile local-wss|local-kcp-dtls, " +
                    "and configure the selected profile as documented in Docs/DotNetUsage.md.");
            }

            if (args.Length != 2 || args[0] != "--profile")
            {
                throw new ArgumentException("Usage: dotnet run --project Game.Server -- --profile local-wss|local-kcp-dtls");
            }

            return args[1];
        }
    }
}
