using System.Security.Cryptography.X509Certificates;
using Game.Server.Login;
using GameFrame.Server.Network;

namespace Game.Server
{
    internal static class LocalWssService
    {
        internal static async Task RunAsync()
        {
            int port = LocalEnvironment.Port("GF_LOCAL_PORT", 7443);
            using var settings = new LocalAccountSettings(LocalEnvironment.Required("GF_LOCAL_ACCOUNT"), LocalEnvironment.Required("GF_LOCAL_PASSWORD"));
            var networkOptions = new WssServerOptions
            {
                AllowedOrigins = (Environment.GetEnvironmentVariable("GF_LOCAL_ORIGINS") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };
            using X509Certificate2 certificate = LoadCertificate();
            using var accounts = new AccountSessions(settings);
            var application = new LobbyApplication(accounts, settings);
            await using var network = new WssServer(application.Application, networkOptions);
            await using WebApplication app = CreateHost(network, networkOptions, certificate, port);
            ConfigurePipeline(app, network);
            await app.RunAsync();
        }

        private static X509Certificate2 LoadCertificate()
        {
            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                LocalEnvironment.Required("GF_LOCAL_CERTIFICATE"), Environment.GetEnvironmentVariable("GF_LOCAL_CERTIFICATE_PASSWORD"));
            DateTime now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() ||
                now >= certificate.NotAfter.ToUniversalTime())
            {
                certificate.Dispose();
                throw new ArgumentException("Invalid local HTTPS certificate: a private key and current validity are required.");
            }

            return certificate;
        }

        private static WebApplication CreateHost(WssServer network, WssServerOptions networkOptions, X509Certificate2 certificate, int port)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxConcurrentConnections = networkOptions.MaxConnections;
                options.Limits.MaxConcurrentUpgradedConnections = networkOptions.MaxConnections;
                options.Limits.RequestHeadersTimeout = networkOptions.HelloTimeout;
                options.ListenLocalhost(port, listen => listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.HandshakeTimeout = networkOptions.HelloTimeout;
                }));
            });
            builder.Services.AddSingleton(network);
            // StoppingAsync joins the network before any hosted StopAsync, including Kestrel.
            builder.Services.AddHostedService(provider => provider.GetRequiredService<WssServer>());
            return builder.Build();
        }

        private static void ConfigurePipeline(WebApplication app, WssServer network)
        {
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
            app.MapGet("/network", network.HandleAsync);
        }
    }
}
