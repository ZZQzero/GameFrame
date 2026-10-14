using Game.Config.TableConfig;
using Game.Server.Configuration;
using Game.Server.Diagnostics;
using GameFrame.Logging;

namespace Game.Server
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            NLogLogging? logging = null;
            string stage = "logging initialization";
            int exitCode = 0;
            try
            {
                logging = ServerLogging.Create();
                ILogger logger = logging.Factory.CreateLogger(typeof(Program).FullName!);
                stage = "profile validation";
                string profile = ParseProfile(args);
                ServerLog.Starting(logger, profile);
                stage = "configuration loading";
                Tables tables = TableConfigLoader.Load(Environment.GetEnvironmentVariable("GF_CONFIG_DIR"));
                stage = profile;
                await RunAsync(profile, tables, logging.Factory);
                ServerLog.Stopped(logger, logging.DroppedCount);
            }
            catch (Exception error)
            {
                exitCode = 1;
                if (logging == null)
                {
                    Console.Error.WriteLine("Server logging initialization failed; check logging configuration.");
                }
                else
                {
                    ServerLog.Failed(logging.Factory.CreateLogger(typeof(Program).FullName!), stage,
                        error.GetType().FullName ?? error.GetType().Name, error.StackTrace);
                }
            }
            finally
            {
                if (logging != null)
                {
                    try
                    {
                        await logging.DisposeAsync();
                    }
                    catch (Exception)
                    {
                        Console.Error.WriteLine("Server log output did not finish successfully.");
                        exitCode = 1;
                    }
                }
            }

            return exitCode;
        }

        private static async Task RunAsync(string profile, Tables tables, ILoggerFactory logging)
        {
            switch (profile)
            {
                case "local-wss":
                    await LocalWssService.RunAsync(tables, logging);
                    break;
                case "local-kcp-dtls":
                    await LocalKcpService.RunAsync(logging);
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

            if (args[1] != "local-wss" && args[1] != "local-kcp-dtls")
            {
                throw new ArgumentException("Production hosting is not configured. Select an explicit local profile.");
            }

            return args[1];
        }
    }
}
