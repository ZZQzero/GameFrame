using System.Security.Cryptography.X509Certificates;
using GameFrame.Server.Network;

namespace GameFrame.Server
{

    public static class Program
    {
        public static async Task Main(string[] args)
        {
            string? profile = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "--profile" || i + 1 >= args.Length || profile != null)
                {
                    throw new ArgumentException("Usage: dotnet run -- --profile local-wss");
                }

                profile = args[++i];
            }

            if (profile != "local-wss")
            {
                throw new InvalidOperationException("Production authentication is not configured. Only explicit local-wss is available.");
            }

            using var settings = new ServerSettings(Required("GF_LOCAL_ACCOUNT"), Required("GF_LOCAL_PASSWORD"))
            {
                AllowedOrigins = (Environment.GetEnvironmentVariable("GF_LOCAL_ORIGINS") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };
            using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                Required("GF_LOCAL_CERTIFICATE"), Environment.GetEnvironmentVariable("GF_LOCAL_CERTIFICATE_PASSWORD"));
            int port = int.Parse(Environment.GetEnvironmentVariable("GF_LOCAL_PORT") ?? "7443");
            if (port < 1 || port > 65535 || !certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
                DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            {
                throw new ArgumentException("Invalid local HTTPS port or certificate.");
            }

            WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxConcurrentConnections = settings.MaxConnections;
                options.Limits.MaxConcurrentUpgradedConnections = settings.MaxConnections;
                options.Limits.RequestHeadersTimeout = settings.HelloTimeout;
                options.ListenLocalhost(port, listen => listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.HandshakeTimeout = settings.HelloTimeout;
                }));
            });
            await using WebApplication app = builder.Build();
            await using var network = new LocalWssService(settings, app.Logger);
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
            app.MapGet("/network", network.HandleAsync);
            await app.RunAsync();
        }

        private static string Required(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Required local configuration is missing: {name}.");
            }

            return value;
        }
    }
}
