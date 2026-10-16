using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Game.Protocol;
using Game.Protocol.Diagnostics;
using GameFrame.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    [NonParallelizable]
    public sealed partial class WssLoadTests
    {
        private sealed record Pending(Task<EchoResponse> Task, long Started);

        private sealed class Client
        {
            internal readonly ConnectionContext Context;
            internal readonly string Payload;
            internal readonly List<Pending> Pending;
            internal readonly uint DelayMs;
            internal NetworkLoadTraffic Traffic = null!;
            internal long NextSlot;
            internal Exception? StateError;

            internal Client(ConnectionContext context, string payload, int inflight, uint delayMs)
            {
                Context = context;
                Payload = payload;
                Pending = new List<Pending>(inflight);
                DelayMs = delayMs;
            }
        }

        private sealed class CloseStormState
        {
            internal readonly ConcurrentBag<ServerConnectionContext> Connections = new ConcurrentBag<ServerConnectionContext>();
            internal readonly TaskCompletionSource Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal volatile bool Active;
            internal int Started;
            internal int Finished;
            internal int Canceled;

            internal async Task<EchoResponse> HandleAsync(ServerConnectionContext connection, EchoRequest request, CancellationToken token)
            {
                Interlocked.Increment(ref Started);
                try
                {
                    await Release.Task.WaitAsync(token);
                    return new EchoResponse { Text = request.Text };
                }
                finally
                {
                    if (token.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref Canceled);
                    }

                    Interlocked.Increment(ref Finished);
                }
            }
        }

        private sealed class Session : ServerSession
        {
            private readonly Action<ServerCloseInfo> closed;
            internal Session(Action<ServerCloseInfo> closed) => this.closed = closed;
            public override ValueTask ClosedAsync(ServerConnectionContext connection, ServerCloseInfo result)
            {
                closed(result);
                return ValueTask.CompletedTask;
            }
        }

        private sealed record RunResult(NetworkLoadOptions Options, NetworkLoadSample Warmup, NetworkLoadSample Measurement,
            NetworkLoadTraffic? PostIdleProbe, NetworkCloseStormSample? CloseStorm,
            double CloseSeconds, long FinalClientOwnedBytes, long FinalServerOwnedBytes, int FinalConnections,
            int FinalPending, int FinalDeadlines, int FinalQueued, int ClosedSessions, int ServerErrors,
            NetworkKcpCompletion? KcpCompletion, KcpRelayReport? Relay)
        {
            public bool ResourcesReleased => FinalClientOwnedBytes == 0 && FinalServerOwnedBytes == 0 && FinalConnections == 0 &&
                FinalPending == 0 && FinalDeadlines == 0 && FinalQueued == 0 && ClosedSessions == Options.Connections &&
                (KcpCompletion?.Released ?? true) && (Relay?.Released ?? true);
            public bool Healthy => Warmup.Healthy && Measurement.Healthy && ResourcesReleased && ServerErrors == 0 &&
                (Relay?.Healthy ?? true) && (Options.NetworkProfile == "direct" || Relay != null) &&
                (Options.Transport != "kcp" || (Measurement.Kcp != null && KcpCompletion != null)) &&
                (Options.Scenario != "close-storm" || (CloseStorm != null && CloseStorm.Healthy)) &&
                (Options.Scenario != "idle" || (Measurement.Idle != null && PostIdleProbe != null &&
                    PostIdleProbe.Planned == Options.Connections && PostIdleProbe.Healthy));
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        [Test]
        [Explicit("Run with Tools/network_load.py; excluded from normal regression runs.")]
        public async Task SustainedEcho()
        {
#if DEBUG
            Assert.Fail("Network load measurements require a Release build.");
#endif
            int Parameter(string name, int fallback) => int.Parse(TestContext.Parameters.Get(name,
                fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            string scenario = TestContext.Parameters.Get("Scenario", "echo");
            var options = new NetworkLoadOptions
            {
                Scenario = scenario,
                Transport = TestContext.Parameters.Get("Transport", "wss"),
                NetworkProfile = TestContext.Parameters.Get("NetworkProfile", "direct"),
                NetworkSeed = Parameter("NetworkSeed", 1),
                SlowHandlerMilliseconds = Parameter("SlowHandlerMs", 0),
                BurstSize = Parameter("BurstSize", scenario == "burst" ? 8 : 0),
                BurstIntervalMilliseconds = Parameter("BurstIntervalMs", scenario == "burst" ? 1000 : 0),
                CloseMode = TestContext.Parameters.Get("CloseMode", scenario == "close-storm" ? "client" : ""),
                CloseRequestsPerConnection = Parameter("CloseRequests", scenario == "close-storm" ? 8 : 0),
                Connections = Parameter("Connections", 4),
                RatePerConnection = Parameter("Rate", scenario == "idle" || scenario == "burst" ? 0 : 20),
                WarmupSeconds = Parameter("Warmup", 2),
                DurationSeconds = Parameter("Duration", scenario == "idle" ? 30 : 10),
                PayloadBytes = Parameter("PayloadBytes", 128),
                InflightPerConnection = Parameter("Inflight", 8),
                RequestTimeoutSeconds = Parameter("RequestTimeout", 3),
                PumpIntervalMilliseconds = Parameter("PumpMs", scenario == "idle" ? 20 : 1)
            };
            options.Validate();
            int repeat = Parameter("Repeat", 1);
            Assert.That(repeat, Is.InRange(1, 10));
            var runs = new List<RunResult>(repeat);
            for (int index = 0; index < repeat; index++)
            {
                RunResult result = await RunAsync(options);
                runs.Add(result);
                TestContext.Progress.WriteLine($"{options.Transport} load run {index + 1}/{repeat}: succeeded={result.Measurement.Succeeded}, " +
                    $"planned={result.Measurement.Planned}, healthy={result.Healthy}, released={result.ResourcesReleased}, " +
                    $"idleHealthy={result.Measurement.Idle?.Healthy}, postIdleSucceeded={result.PostIdleProbe?.Succeeded}");
            }

            var report = new
            {
                SchemaVersion = 7,
                Transport = options.Transport,
                Scenario = options.Transport + "-" + (options.Scenario == "echo" ? "sustained-echo" : options.Scenario),
                Native = JsonSerializer.Deserialize<JsonElement>(TestContext.Parameters.Get("NativeProof", "null")),
                TimestampUtc = DateTimeOffset.UtcNow,
                Revision = TestContext.Parameters.Get("Revision", "unspecified"),
                WorktreeDirty = TestContext.Parameters.Get("WorktreeDirty", "unspecified"),
                Sdk = TestContext.Parameters.Get("Sdk", "unspecified"),
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                ProtocolHash = Convert.ToHexString(GameNetProtocol.Catalog.Hash),
                Build = "Release",
                Scope = "Same-process loopback; client + server + harness; logging disabled; no database or Unity",
                Latency = "Request call through observed completion, including encode/decode/Pump; quantiles are bucket upper bounds (<=3.125% width, minimum 1us)",
                NetworkEmulation = new
                {
                    Profile = options.NetworkProfile, Seed = options.NetworkSeed,
                    Plan = KcpImpairmentPlan.For(options.NetworkProfile),
                    Scope = "UDP both directions after all connections establish; warmup + measurement + post-idle probe; setup and close bypass impairment",
                    PacketCapacity = KcpLoadRelay.PacketCapacity, ByteCapacity = KcpLoadRelay.ByteCapacity
                },
                Limits = new { Client = new NetworkClientOptions(), Server = options.Transport == "kcp"
                    ? (object)new KcpServerOptions() : ServerOptions(options),
                    ClientWorkerCapacity = options.Transport == "kcp" ? options.Connections : (int?)null },
                Runs = runs
            };
            string json = JsonSerializer.Serialize(report, JsonOptions);
            TestContext.Progress.WriteLine("Network load report:\n" + json);
            string output = TestContext.Parameters.Get("JsonPath", "");
            if (output.Length != 0)
            {
                await File.WriteAllTextAsync(output, json);
            }

            Assert.That(runs.All(run => run.Healthy), Is.True,
                "Load or scenario checks failed. Inspect outcomes, skipped slots, close/heartbeat activity and resources.");
        }

        [Test]
        public async Task FourRealWssClientsEchoAndReleaseResources()
        {
            RunResult result = await RunAsync(new NetworkLoadOptions { WarmupSeconds = 0, DurationSeconds = 1, RatePerConnection = 5 });
            Assert.That(result.Measurement.Healthy, Is.True);
            Assert.That(result.Measurement.Succeeded, Is.EqualTo(20));
            Assert.That(result.Measurement.SuccessLatency.Count, Is.EqualTo(20));
            Assert.That(result.ResourcesReleased, Is.True);
            Assert.That(result.ServerErrors, Is.Zero);
        }

        [Test]
        public async Task KcpClientsEchoAndJoinNativeOwnersAsync()
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = "kcp", Connections = 3, WarmupSeconds = 0, DurationSeconds = 1, RatePerConnection = 5
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.True);
                Assert.That(result.Measurement.Succeeded, Is.EqualTo(15));
                Assert.That(result.Measurement.Kcp!.ServerSendQueue.Count, Is.GreaterThan(0));
                Assert.That(result.Measurement.Kcp.ServerDispatchQueue.Count, Is.GreaterThan(0));
                Assert.That(result.Measurement.Kcp.ClientNativeOwnedSampledPeak, Is.GreaterThan(0));
                Assert.That(result.KcpCompletion!.Released, Is.True);
                Assert.That(result.ResourcesReleased, Is.True);
                Assert.That(result.ServerErrors, Is.Zero);
            });
        }

        [Test]
        public async Task KcpIdleClientsExchangeHeartbeatThenEchoAndReleaseAsync()
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = "kcp", Scenario = "idle", RatePerConnection = 0, Connections = 2,
                WarmupSeconds = 0, DurationSeconds = 6, PumpIntervalMilliseconds = 20
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.True);
                Assert.That(result.Measurement.Idle!.ClientsWithBidirectionalControlTraffic, Is.EqualTo(2));
                Assert.That(result.Measurement.Kcp!.ClientNativeReservedStart, Is.EqualTo(8 * 1024 * 1024));
                Assert.That(result.Measurement.Kcp.ClientNativeReservedEnd, Is.EqualTo(8 * 1024 * 1024));
                Assert.That(result.Measurement.Kcp.ServerNativeOwnedBytes, Is.Null);
                Assert.That(result.PostIdleProbe!.Succeeded, Is.EqualTo(2));
                Assert.That(result.KcpCompletion!.Released, Is.True);
                Assert.That(result.ResourcesReleased, Is.True);
            });
        }

        [Test]
        public async Task KcpLateGeneratorReportsSkippedLoadAndStillReleasesAsync()
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = "kcp", Connections = 2, WarmupSeconds = 0, DurationSeconds = 1,
                RatePerConnection = 40, PumpIntervalMilliseconds = 100
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.False);
                Assert.That(result.Measurement.Planned, Is.EqualTo(80));
                Assert.That(result.Measurement.GeneratorLateSkipped, Is.GreaterThan(0));
                Assert.That(result.Measurement.Succeeded, Is.EqualTo(result.Measurement.Attempted));
                Assert.That(result.Measurement.Unresolved, Is.Zero);
                Assert.That(result.ResourcesReleased, Is.True);
                Assert.That(result.ServerErrors, Is.Zero);
            });
        }

        [TestCase("loss")]
        [TestCase("delay")]
        [TestCase("jitter")]
        [TestCase("reorder")]
        [TestCase("mixed")]
        public async Task ImpairedKcpEchoCompletesAndReleasesRelayAsync(string profile)
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = "kcp", NetworkProfile = profile, Connections = 2,
                RatePerConnection = 10, WarmupSeconds = 0, DurationSeconds = 2
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.True);
                Assert.That(result.Measurement.Succeeded, Is.EqualTo(40));
                Assert.That(result.Measurement.Unresolved, Is.Zero);
                Assert.That(result.Relay!.ProfileExercised, Is.True);
                Assert.That(result.Relay.QueueOverflowDrops, Is.Zero);
                Assert.That(result.Relay.PeakQueuePackets, Is.LessThanOrEqualTo(KcpLoadRelay.PacketCapacity));
                Assert.That(result.Relay.PeakQueueBytes, Is.LessThanOrEqualTo(KcpLoadRelay.ByteCapacity));
                Assert.That(result.Relay.Released, Is.True);
                Assert.That(result.KcpCompletion!.Released, Is.True);
                Assert.That(result.ResourcesReleased, Is.True);
            });
        }

        [Test]
        public async Task ImpairedKcpIdleKeepsHeartbeatAndPostIdleProbeAsync()
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = "kcp", NetworkProfile = "jitter", Scenario = "idle", Connections = 2,
                RatePerConnection = 0, WarmupSeconds = 0, DurationSeconds = 6, PumpIntervalMilliseconds = 20
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.True);
                Assert.That(result.Measurement.Idle!.ClientsWithBidirectionalControlTraffic, Is.EqualTo(2));
                Assert.That(result.PostIdleProbe!.Succeeded, Is.EqualTo(2));
                Assert.That(result.Relay!.Healthy, Is.True);
                Assert.That(result.ResourcesReleased, Is.True);
            });
        }

        [Test]
        public async Task ImpairedKcpBurstKeepsReliableEchoAndReleasesAsync()
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = "kcp", NetworkProfile = "mixed", Scenario = "burst", Connections = 2,
                RatePerConnection = 0, BurstSize = 4, BurstIntervalMilliseconds = 500,
                WarmupSeconds = 0, DurationSeconds = 2
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.True);
                Assert.That(result.Measurement.Succeeded, Is.EqualTo(32));
                Assert.That(result.Relay!.DroppedPackets, Is.GreaterThan(0));
                Assert.That(result.Relay.OutOfOrderPackets, Is.GreaterThan(0));
                Assert.That(result.ResourcesReleased, Is.True);
            });
        }

        [TestCase("wss")]
        [TestCase("kcp")]
        public async Task BurstClientsEchoEveryPlannedRequestAndReleaseResourcesAsync(string transport)
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = transport,
                Scenario = "burst", RatePerConnection = 0, BurstSize = 8, BurstIntervalMilliseconds = 250,
                WarmupSeconds = 0, DurationSeconds = 1
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.True);
                Assert.That(result.Measurement.Planned, Is.EqualTo(128));
                Assert.That(result.Measurement.Succeeded, Is.EqualTo(128));
                Assert.That(result.Measurement.SuccessLatency.Count, Is.EqualTo(128));
                Assert.That(result.Measurement.BurstClientIssueLatency!.Count, Is.EqualTo(16));
                Assert.That(result.ResourcesReleased, Is.True);
            });
        }

        [TestCase("wss", "client", 0)]
        [TestCase("wss", "client", 8)]
        [TestCase("wss", "server", 0)]
        [TestCase("wss", "server", 8)]
        [TestCase("kcp", "client", 0)]
        [TestCase("kcp", "client", 8)]
        [TestCase("kcp", "server", 0)]
        [TestCase("kcp", "server", 8)]
        public async Task ConcentratedCloseJoinsClientsAndServersWithPendingRequestsAsync(string transport, string mode, int requests)
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = transport, Scenario = "close-storm", CloseMode = mode, CloseRequestsPerConnection = requests,
                WarmupSeconds = 0, DurationSeconds = 1, RatePerConnection = 5
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.True);
                Assert.That(result.Measurement.Succeeded, Is.EqualTo(20));
                Assert.That(result.CloseStorm!.Prepared, Is.True);
                Assert.That(result.CloseStorm.PendingAtClose, Is.EqualTo(4 * requests));
                Assert.That(result.CloseStorm.ClosedRequests, Is.EqualTo(4 * requests));
                Assert.That(result.CloseStorm.Unresolved, Is.Zero);
                Assert.That(result.CloseStorm.UnexpectedFailures, Is.Zero);
                Assert.That(result.CloseStorm.CompletedClients, Is.EqualTo(4));
                Assert.That(result.CloseStorm.CompletedServers, Is.EqualTo(4));
                Assert.That(result.CloseStorm.ClientCloseLatency.Count, Is.EqualTo(4));
                Assert.That(result.CloseStorm.ServerCloseLatency.Count, Is.EqualTo(4));
                Assert.That(result.ResourcesReleased, Is.True);
                Assert.That(result.ServerErrors, Is.Zero);
            });
        }

        [TestCase("wss")]
        [TestCase("kcp")]
        public async Task BurstInflightOverflowCountsSkippedRequestsAndReleasesResourcesAsync(string transport)
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = transport,
                Scenario = "burst", RatePerConnection = 0, BurstSize = 16, BurstIntervalMilliseconds = 1000,
                InflightPerConnection = 4, WarmupSeconds = 0, DurationSeconds = 1
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.False);
                Assert.That(result.Measurement.Planned, Is.EqualTo(64));
                Assert.That(result.Measurement.Succeeded, Is.EqualTo(16));
                Assert.That(result.Measurement.InflightLimitSkipped, Is.EqualTo(48));
                Assert.That(result.Measurement.Backpressure, Is.Zero, "A tool limit must not be mislabeled as network backpressure.");
                Assert.That(result.Measurement.Unresolved, Is.Zero);
                Assert.That(result.ResourcesReleased, Is.True);
                Assert.That(result.ServerErrors, Is.Zero);
            });
        }

        [TestCase("wss")]
        [TestCase("kcp")]
        public async Task LateBurstGeneratorSkipsOldBatchesInsteadOfReplayingThemAsync(string transport)
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = transport,
                Scenario = "burst", RatePerConnection = 0, BurstSize = 2, BurstIntervalMilliseconds = 10,
                Connections = 2, PumpIntervalMilliseconds = 100, WarmupSeconds = 0, DurationSeconds = 1
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.False);
                Assert.That(result.Measurement.Planned, Is.EqualTo(400));
                Assert.That(result.Measurement.GeneratorLateSkipped, Is.GreaterThan(0));
                Assert.That(result.Measurement.Planned, Is.EqualTo(result.Measurement.Attempted + result.Measurement.GeneratorLateSkipped));
                Assert.That(result.Measurement.Succeeded, Is.EqualTo(result.Measurement.Attempted));
                Assert.That(result.Measurement.InflightLimitSkipped, Is.Zero);
                Assert.That(result.ResourcesReleased, Is.True);
                Assert.That(result.ServerErrors, Is.Zero);
            });
        }

        [TestCase("wss")]
        [TestCase("kcp")]
        public async Task DelayedBurstReportsTimeoutsWhileNormalConnectionsSucceedAsync(string transport)
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = transport,
                Scenario = "burst", RatePerConnection = 0, BurstSize = 4, BurstIntervalMilliseconds = 1000,
                SlowHandlerMilliseconds = 400, Connections = 2, RequestTimeoutSeconds = 1,
                WarmupSeconds = 0, DurationSeconds = 1
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.False);
                Assert.That(result.Measurement.Normal.Healthy, Is.True);
                Assert.That(result.Measurement.Normal.Succeeded, Is.EqualTo(4));
                Assert.That(result.Measurement.Slow!.TimedOut, Is.GreaterThan(0));
                Assert.That(result.Measurement.Unresolved, Is.Zero);
                Assert.That(result.ResourcesReleased, Is.True);
                Assert.That(result.ServerErrors, Is.Zero);
            });
        }

        [Test]
        public async Task IdleClientsExchangeHeartbeatThenEchoAndReleaseResourcesAsync()
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Scenario = "idle", RatePerConnection = 0, DurationSeconds = 6, WarmupSeconds = 0,
                PumpIntervalMilliseconds = 20
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Healthy, Is.True);
                Assert.That(result.Measurement.Planned, Is.Zero);
                Assert.That(result.Measurement.Attempted, Is.Zero);
                Assert.That(result.Measurement.SuccessLatency.Count, Is.Zero);
                Assert.That(result.Measurement.Idle!.ClientsWithBidirectionalControlTraffic, Is.EqualTo(4));
                Assert.That(result.Measurement.Idle.SentApplicationBytes, Is.GreaterThanOrEqualTo(320));
                Assert.That(result.Measurement.Idle.ReceivedApplicationBytes, Is.GreaterThanOrEqualTo(320));
                Assert.That(result.Measurement.Idle.ReceivedBusinessFrames, Is.Zero);
                Assert.That(result.Measurement.ElapsedSeconds, Is.GreaterThanOrEqualTo(6));
                Assert.That(result.PostIdleProbe!.Succeeded, Is.EqualTo(4));
                Assert.That(result.PostIdleProbe.SuccessLatency.Count, Is.EqualTo(4));
                Assert.That(result.ResourcesReleased, Is.True);
            });
        }

        [TestCase("wss")]
        [TestCase("kcp")]
        public async Task WaitingHandlerAllowsAnotherConnectionToDispatch(string transport)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var normalDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<RunResult> running = RunAsync(new NetworkLoadOptions
            {
                Transport = transport,
                Scenario = "slow-handler", SlowHandlerMilliseconds = 25,
                Connections = 2, WarmupSeconds = 0, DurationSeconds = 1, RatePerConnection = 5
            }, async (_, request, token) =>
            {
                if (request.DelayMs != 0)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                else
                {
                    await entered.Task.WaitAsync(token);
                    normalDispatched.TrySetResult();
                }

                return new EchoResponse { Text = request.Text };
            });
            try
            {
                await normalDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(release.Task.IsCompleted, Is.False,
                    "A normal connection must reach dispatch while the slow handler is still waiting.");
            }
            finally
            {
                release.TrySetResult();
                await running;
            }

            RunResult result = await running;
            Assert.Multiple(() =>
            {
                Assert.That(result.Measurement.Normal.Succeeded, Is.EqualTo(5));
                Assert.That(result.Measurement.Slow!.Succeeded, Is.EqualTo(5));
                Assert.That(result.Measurement.Healthy, Is.True);
                Assert.That(result.ResourcesReleased, Is.True);
                Assert.That(result.ServerErrors, Is.Zero);
            });
        }

        [TestCase("wss")]
        [TestCase("kcp")]
        public async Task SlowHandlerTimeoutsKeepNormalTrafficHealthyAndReleaseResources(string transport)
        {
            RunResult result = await RunAsync(new NetworkLoadOptions
            {
                Transport = transport,
                Scenario = "slow-handler", SlowHandlerMilliseconds = 1500,
                Connections = 2, WarmupSeconds = 0, DurationSeconds = 1, RatePerConnection = 2,
                RequestTimeoutSeconds = 1
            });
            Assert.Multiple(() =>
            {
                Assert.That(result.Measurement.Normal.Healthy, Is.True);
                Assert.That(result.Measurement.Normal.Succeeded, Is.EqualTo(2));
                Assert.That(result.Measurement.Slow!.TimedOut, Is.EqualTo(2));
                Assert.That(result.Measurement.Slow.FailureLatency.Count, Is.EqualTo(2));
                Assert.That(result.Measurement.Unresolved, Is.Zero);
                Assert.That(result.Measurement.Healthy, Is.False);
                Assert.That(result.ResourcesReleased, Is.True);
                Assert.That(result.ServerErrors, Is.Zero);
            });
        }

        private static WssServerOptions ServerOptions(NetworkLoadOptions options) => new WssServerOptions
        {
            MaxConnections = options.Connections,
            MaxConnectionsPerIp = options.Connections,
            MaxRestrictedConnections = options.Connections,
            MaxRestrictedConnectionsPerIp = options.Connections
        };

        private static async Task<RunResult> RunAsync(NetworkLoadOptions options,
            Func<ServerConnectionContext, EchoRequest, CancellationToken, Task<EchoResponse>>? handler = null)
        {
            options.Validate();
            if (options.Transport == "kcp")
            {
                KcpTestLibrary.Load();
            }

            using X509Certificate2? certificate = options.Transport == "wss" ? CreateCertificate() : null;
            string? certificateHash = certificate?.GetCertHashString();
            int closedSessions = 0;
            int serverErrors = 0;
            var closeState = new CloseStormState();
            var application = ServerCloseTests.Application((connection, echo, token) => closeState.Active
                ? closeState.HandleAsync(connection, echo, token) : (handler ?? EchoAsync)(connection, echo, token),
                context =>
                {
                    closeState.Connections.Add(context);
                    return new Session(result =>
                    {
                        Interlocked.Increment(ref closedSessions);
                        if (result.Error != null)
                        {
                            Interlocked.Increment(ref serverErrors);
                        }
                    });
                });
            WssServer? server = options.Transport == "wss" ? new WssServer(application, ServerOptions(options)) : null;
            await using WebApplication? app = server == null ? null : CreateWssHost(server, certificate!);
            await using KcpListenerHost? kcp = options.Transport == "kcp"
                ? new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), application) : null;
            KcpWorker? worker = null;
            KcpLoadRelay? relay = null;
            Task? workerStopped = null;
            (long OwnedBytes, int Connections) ServerSnapshot() => server != null
                ? (server.OwnedBytes, server.ConnectionCount)
                : (kcp!.OwnedBytes, closeState.Connections.Count(context => !context.Completion.IsCompleted));
            var clients = new List<Client>(options.Connections);
            var memory = new FrameMemoryBudget((long)options.Connections * new NetworkClientOptions().MaxOwnedBytes);
            NetworkLoadSample warmup = new NetworkLoadSample();
            NetworkLoadSample measured = new NetworkLoadSample();
            NetworkLoadTraffic? postIdleProbe = null;
            NetworkCloseStormSample? closeStorm = null;
            double closeSeconds = 0;
            var cleanupErrors = new List<Exception>();
            Exception? primary = null;
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Uri endpoint;
                if (app != null)
                {
                    await app.StartAsync(startup.Token);
                    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                    endpoint = new Uri(address.Replace("https:", "wss:") + "/network");
                }
                else
                {
                    await kcp!.Started.WaitAsync(startup.Token);
                    endpoint = new Uri($"udp://{kcp.LocalEndpoint!.Address}:{kcp.LocalEndpoint.Port}");
                    worker = new KcpWorker(options.Connections);
                    if (options.NetworkProfile != "direct")
                    {
                        relay = new KcpLoadRelay(kcp.LocalEndpoint, options.Connections, options.NetworkProfile, options.NetworkSeed);
                    }
                }

                for (int index = 0; index < options.Connections; index++)
                {
                    ConnectionContext context;
                    NetworkConnectOptions? credentials = null;
                    if (worker == null)
                    {
                        var socket = new ClientWebSocket();
                        // Trust only this run's ephemeral certificate; never change machine trust.
                        socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == certificateHash;
                        context = new ConnectionContext(1, new NetworkClientOptions(), GameNetProtocol.Catalog,
                            NetworkConnectOptions.WebSocket(endpoint), memory, CancellationToken.None, socket);
                    }
                    else
                    {
                        Uri clientEndpoint = relay?.Endpoint(index) ?? endpoint;
                        credentials = await CreateGrantAsync(kcp!, clientEndpoint, (uint)index + 1);
                        try
                        {
                            context = new ConnectionContext(1, new NetworkClientOptions { TrustedUdpEndpoints = new[] { clientEndpoint } },
                                GameNetProtocol.Catalog, credentials, memory, CancellationToken.None, kcpWorker: worker);
                        }
                        catch
                        {
                            ClearGrant(credentials);
                            throw;
                        }
                    }
                    var client = new Client(context, index.ToString("D4", CultureInfo.InvariantCulture) + ":" +
                        new string('x', options.PayloadBytes - 5), options.InflightPerConnection,
                        index == 0 ? (uint)options.SlowHandlerMilliseconds : 0);
                    context.StateChanged = (_, error) =>
                    {
                        if (error != null)
                        {
                            client.StateError = error;
                        }
                    };
                    clients.Add(client);
                    try
                    {
                        context.Start();
                        await PumpUntilAsync(clients, () => context.ConnectTask.IsCompleted, TimeSpan.FromSeconds(10));
                        await context.ConnectTask;
                    }
                    finally
                    {
                        if (credentials != null)
                        {
                            ClearGrant(credentials);
                        }
                    }
                }

                relay?.SetImpaired(true);
                warmup = await MeasureAsync(clients, ServerSnapshot, memory, options, options.WarmupSeconds);
                measured = await MeasureAsync(clients, ServerSnapshot, memory, options, options.DurationSeconds, requireIdleHeartbeat: true);
                if (options.Scenario == "idle")
                {
                    postIdleProbe = await ProbeAsync(clients, options);
                }

                relay?.SetImpaired(false);
                if (options.Scenario == "close-storm")
                {
                    closeStorm = await CloseStormAsync(clients, closeState, options);
                }
            }
            catch (Exception error)
            {
                primary = error;
            }
            finally
            {
                relay?.SetImpaired(false);
                closeState.Release.TrySetResult();
                long closeStarted = Stopwatch.GetTimestamp();
                async Task CleanupAsync(Func<Task> action)
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception error)
                    {
                        cleanupErrors.Add(error);
                    }
                }

                foreach (Client client in clients)
                {
                    await CleanupAsync(() =>
                    {
                        client.Context.BeginClose();
                        return Task.CompletedTask;
                    });
                }

                await CleanupAsync(() => PumpUntilAsync(clients,
                    () => clients.All(client => client.Context.CloseTask.IsCompleted && client.Context.IoTask.IsCompleted), TimeSpan.FromSeconds(10)));
                if (cleanupErrors.Count != 0)
                {
                    foreach (Client client in clients)
                    {
                        await CleanupAsync(() =>
                        {
                            client.Context.FailHost(new NetworkException("Load harness cleanup."));
                            return Task.CompletedTask;
                        });
                    }

                    await CleanupAsync(() => PumpUntilAsync(clients,
                        () => clients.All(client => client.Context.CloseTask.IsCompleted && client.Context.IoTask.IsCompleted), TimeSpan.FromSeconds(10)));
                }

                foreach (Client client in clients)
                {
                    await CleanupAsync(async () =>
                    {
                        await client.Context.IoTask.WaitAsync(TimeSpan.FromSeconds(10));
                        await client.Context.CloseTask.WaitAsync(TimeSpan.FromSeconds(10));
                        // Observe work even when an earlier setup/measurement failure interrupted its drain.
                        foreach (Pending pending in client.Pending)
                        {
                            try
                            {
                                await pending.Task;
                            }
                            catch (NetworkException)
                            {
                                // The run is already failed; expected close errors still need observation.
                            }
                        }
                    });
                }

                await CleanupAsync(async () =>
                {
                    if (worker != null)
                    {
                        workerStopped = worker.ShutdownAsync();
                        await workerStopped.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                });
                await CleanupAsync(async () =>
                {
                    // The relay depends on the listener endpoint. Stop delayed sends
                    // after clients join and before the listener socket is removed.
                    if (relay != null)
                    {
                        await relay.DisposeAsync();
                    }
                });
                await CleanupAsync(async () =>
                {
                    if (kcp != null)
                    {
                        await kcp.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    else
                    {
                        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await app!.StopAsync(stop.Token);
                    }
                });
                closeSeconds = Stopwatch.GetElapsedTime(closeStarted).TotalSeconds;
            }

            if (primary != null || cleanupErrors.Count != 0)
            {
                throw new AggregateException("Network load setup, execution or cleanup failed.",
                    primary == null ? cleanupErrors : new[] { primary }.Concat(cleanupErrors));
            }

            NetworkMetricsSnapshot[] final = clients.Select(client => client.Context.Metrics()).ToArray();
            NetworkKcpCompletion? kcpCompletion = kcp == null ? null : new NetworkKcpCompletion(kcp.Stopped.IsCompletedSuccessfully,
                workerStopped?.IsCompletedSuccessfully == true, final.Aggregate(0UL, (sum, value) => sum + value.Kcp.NativeOwnedBytes),
                final.Sum(value => value.Kcp.NativeReservedBytes), final.Aggregate(0UL, (sum, value) => sum +
                    value.Kcp.InputQueueCount + value.Kcp.OutputQueueCount + value.Kcp.PlaintextQueueCount + value.Kcp.Waitsnd));
            var snapshot = ServerSnapshot();
            var result = new RunResult(options, warmup, measured, postIdleProbe, closeStorm, closeSeconds, memory.OwnedBytes, snapshot.OwnedBytes,
                snapshot.Connections, final.Sum(value => value.PendingCount), final.Sum(value => value.DeadlineCount),
                final.Sum(value => value.ReliableSendCount + value.ReliableReceiveCount), closedSessions, serverErrors, kcpCompletion, relay?.Snapshot());
            return result;
        }

        private static X509Certificate2 CreateCertificate()
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
        }

        private static WebApplication CreateWssHost(WssServer server, X509Certificate2 certificate)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            builder.Services.AddSingleton(server);
            builder.Services.AddHostedService(provider => provider.GetRequiredService<WssServer>());
            WebApplication app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
            app.MapGet("/network", server.HandleAsync);
            return app;
        }

        private static async Task<NetworkConnectOptions> CreateGrantAsync(KcpListenerHost host, Uri endpoint, uint conv)
        {
            byte[] identity = RandomNumberGenerator.GetBytes(16);
            byte[] psk = RandomNumberGenerator.GetBytes(32);
            byte[] ticket = RandomNumberGenerator.GetBytes(32);
            try
            {
                long deadline = NetworkClock.GetMilliseconds() + 30000;
                await host.RegisterGrantAsync(conv, identity, psk, ticket);
                // Production catalog currently has no unreliable route.
                return NetworkConnectOptions.Kcp(endpoint, ticket, conv, identity, psk,
                    deadline, unreliableLimit: 0);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(identity);
                CryptographicOperations.ZeroMemory(psk);
                CryptographicOperations.ZeroMemory(ticket);
                throw;
            }
        }

        private static void ClearGrant(NetworkConnectOptions credentials)
        {
            CryptographicOperations.ZeroMemory(credentials.Psk);
            CryptographicOperations.ZeroMemory(credentials.PskIdentity);
            CryptographicOperations.ZeroMemory(credentials.Ticket);
        }

        private static async Task<NetworkCloseStormSample> CloseStormAsync(List<Client> clients,
            CloseStormState state, NetworkLoadOptions options)
        {
            var sample = new NetworkCloseStormSample
            {
                Mode = options.CloseMode, Connections = clients.Count,
                PlannedRequests = (long)clients.Count * options.CloseRequestsPerConnection
            };
            if (clients.Any(client => client.Context.State != NetworkClientState.Connected))
            {
                return sample;
            }

            ServerConnectionContext[] connections = state.Connections.ToArray();
            state.Active = true;
            long prepareStarted = Stopwatch.GetTimestamp();
            foreach (Client client in clients)
            {
                for (int index = 0; index < options.CloseRequestsPerConnection; index++)
                {
                    sample.AttemptedRequests++;
                    try
                    {
                        long started = Stopwatch.GetTimestamp();
                        Task<EchoResponse> task = client.Context.Request(DiagnosticsRoutes.Echo,
                            new EchoRequest { Text = client.Payload }, TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                            CancellationToken.None);
                        client.Pending.Add(new Pending(task, started));
                    }
                    catch (Exception error)
                    {
                        sample.RecordOutcome(error, pendingAtClose: false);
                    }
                }
            }

            int requiredHandlers = sample.PlannedRequests == 0 ? 0 : clients.Count;
            while (Volatile.Read(ref state.Started) < requiredHandlers &&
                clients.Sum(client => client.Pending.Count) == sample.PlannedRequests &&
                clients.All(client => client.Pending.All(pending => !pending.Task.IsCompleted)) &&
                Stopwatch.GetElapsedTime(prepareStarted).TotalSeconds < Math.Min(10, options.RequestTimeoutSeconds))
            {
                Pump(clients);
                await Task.Delay(1);
            }

            sample.PrepareSeconds = Stopwatch.GetElapsedTime(prepareStarted).TotalSeconds;
            sample.HandlersEntered = Volatile.Read(ref state.Started);
            sample.PendingAtClose = clients.Sum(client => client.Context.Metrics().PendingCount);
            sample.Prepared = connections.Length == clients.Count && sample.PendingAtClose == sample.PlannedRequests &&
                sample.AttemptedRequests == sample.PlannedRequests && sample.HandlersEntered == requiredHandlers &&
                clients.All(client => client.Context.State == NetworkClientState.Connected &&
                    client.Pending.All(pending => !pending.Task.IsCompleted));

            // One owner issues each close in sequence before joining any connection.
            long closeStarted = Stopwatch.GetTimestamp();
            if (options.CloseMode == "client")
            {
                foreach (Client client in clients)
                {
                    client.Context.BeginClose();
                }
            }
            else
            {
                foreach (ServerConnectionContext connection in connections)
                {
                    connection.RequestClose(3);
                }
            }

            sample.IssueSeconds = Stopwatch.GetElapsedTime(closeStarted).TotalSeconds;
            if (options.CloseMode == "client")
            {
                // Orderly client Close drains accepted server work; let those handlers finish.
                state.Release.TrySetResult();
            }

            var observedClients = new bool[clients.Count];
            var observedServers = new bool[connections.Length];
            while (sample.CompletedClients != clients.Count || sample.CompletedServers != connections.Length ||
                clients.Any(client => client.Pending.Count != 0))
            {
                Pump(clients);
                for (int index = 0; index < clients.Count; index++)
                {
                    Client client = clients[index];
                    if (options.CloseMode == "server" && client.Context.State != NetworkClientState.Connected)
                    {
                        client.Context.BeginClose();
                    }

                    for (int pendingIndex = client.Pending.Count - 1; pendingIndex >= 0; pendingIndex--)
                    {
                        Task<EchoResponse> task = client.Pending[pendingIndex].Task;
                        if (!task.IsCompleted)
                        {
                            continue;
                        }

                        Exception? error = null;
                        try
                        {
                            await task;
                        }
                        catch (Exception failure)
                        {
                            error = failure;
                        }

                        sample.RecordOutcome(error, sample.Prepared);
                        client.Pending.RemoveAt(pendingIndex);
                    }

                    if (!observedClients[index] && client.Context.CloseTask.IsCompleted && client.Context.IoTask.IsCompleted)
                    {
                        observedClients[index] = true;
                        sample.CompletedClients++;
                        sample.ClientCloseLatency.Record(Stopwatch.GetElapsedTime(closeStarted).TotalMilliseconds);
                        bool unexpected = options.CloseMode == "client" ? client.StateError != null :
                            client.StateError is not NetworkConnectionClosedException { ReasonCode: 3 };
                        Exception? taskError = null;
                        try
                        {
                            await client.Context.IoTask;
                            await client.Context.CloseTask;
                        }
                        catch (Exception error)
                        {
                            taskError = error;
                            unexpected = true;
                        }

                        if (unexpected)
                        {
                            sample.UnexpectedClientFaults++;
                            KcpMetricsSnapshot native = client.Context.Metrics().Kcp;
                            sample.UnexpectedClientFaultDetails.Add(new NetworkCloseFault(client.StateError?.GetType().FullName,
                                client.StateError?.StackTrace, (client.StateError as NetworkConnectionClosedException)?.ReasonCode,
                                taskError?.GetType().FullName, taskError?.StackTrace, native.NativeState, native.FatalCode, native.NativeError));
                        }
                    }
                }

                for (int index = 0; index < connections.Length; index++)
                {
                    if (!observedServers[index] && connections[index].Completion.IsCompleted)
                    {
                        observedServers[index] = true;
                        sample.CompletedServers++;
                        sample.ServerCloseLatency.Record(Stopwatch.GetElapsedTime(closeStarted).TotalMilliseconds);
                        try
                        {
                            ServerCloseInfo result = await connections[index].Completion;
                            if (result.Error != null || result.Reason != (options.CloseMode == "client" ? 1 : 3))
                            {
                                sample.UnexpectedServerResults++;
                            }
                        }
                        catch (Exception)
                        {
                            sample.UnexpectedServerResults++;
                        }
                    }
                }

                if (Stopwatch.GetElapsedTime(closeStarted).TotalSeconds >= 10)
                {
                    sample.CloseDeadlineExceeded = true;
                    break;
                }

                await Task.Delay(options.PumpIntervalMilliseconds);
            }

            sample.TotalSeconds = Stopwatch.GetElapsedTime(closeStarted).TotalSeconds;
            sample.HandlersStarted = Volatile.Read(ref state.Started);
            sample.HandlersFinished = Volatile.Read(ref state.Finished);
            sample.HandlersCanceled = Volatile.Read(ref state.Canceled);
            return sample;
        }

        private static async Task<EchoResponse> EchoAsync(ServerConnectionContext connection, EchoRequest request, CancellationToken token)
        {
            if (request.DelayMs != 0)
            {
                await Task.Delay((int)request.DelayMs, token);
            }

            return new EchoResponse { Text = request.Text };
        }

        private static void Pump(IReadOnlyList<Client> clients)
        {
            foreach (Client client in clients)
            {
                for (int count = 0; count < 128 && client.Context.PumpOne(); count++)
                {
                }
            }
        }

        private static async Task PumpUntilAsync(IReadOnlyList<Client> clients, Func<bool> completed, TimeSpan timeout)
        {
            long start = Stopwatch.GetTimestamp();
            while (!completed())
            {
                if (Stopwatch.GetElapsedTime(start) > timeout)
                {
                    throw new TimeoutException("Load harness lifecycle deadline exceeded.");
                }

                Pump(clients);
                await Task.Delay(1);
            }
        }

        private static void Observe(Client client, NetworkLoadTraffic sample, long offerStarted, int offerSeconds)
        {
            for (int index = client.Pending.Count - 1; index >= 0; index--)
            {
                Pending pending = client.Pending[index];
                if (!pending.Task.IsCompleted)
                {
                    continue;
                }

                double milliseconds = Stopwatch.GetElapsedTime(pending.Started).TotalMilliseconds;
                try
                {
                    EchoResponse response = pending.Task.GetAwaiter().GetResult();
                    if (response.Text != client.Payload)
                    {
                        sample.Mismatched++;
                        throw new InvalidOperationException("Echo payload mismatch.");
                    }

                    sample.Succeeded++;
                    sample.SuccessLatency.Record(milliseconds);
                    if (Stopwatch.GetElapsedTime(offerStarted).TotalSeconds < offerSeconds)
                    {
                        sample.CompletedDuringOffer++;
                    }
                }
                catch (Exception error)
                {
                    sample.RecordFailure(error, milliseconds);
                }

                client.Pending.RemoveAt(index);
                sample.DrainSeconds = Math.Max(sample.DrainSeconds, Stopwatch.GetElapsedTime(offerStarted).TotalSeconds - offerSeconds);
            }
        }

        private static async Task<NetworkLoadSample> MeasureAsync(List<Client> clients,
            Func<(long OwnedBytes, int Connections)> serverSnapshot,
            FrameMemoryBudget memory, NetworkLoadOptions options, int seconds, bool requireIdleHeartbeat = false)
        {
            var sample = new NetworkLoadSample
            {
                Slow = options.SlowHandlerMilliseconds != 0 ? new NetworkLoadTraffic() : null,
                BurstClientIssueLatency = options.Scenario == "burst" ? new LoadLatency() : null
            };
            int requestsPerSlot = options.RequestsPerSlot;
            long plannedSlots = options.PlannedSlots(seconds);
            long perClientPlanned = plannedSlots * requestsPerSlot;
            foreach (Client client in clients)
            {
                client.NextSlot = 0;
                client.Traffic = client.DelayMs == 0 ? sample.Normal : sample.Slow!;
                client.Traffic.Planned += perClientPlanned;
                client.Traffic.OfferSeconds = seconds;
            }

            if (seconds == 0)
            {
                return sample;
            }

            if (options.Transport == "kcp")
            {
                sample.Kcp = new NetworkKcpSample(clients.Select(client => client.Context.Metrics()).ToArray());
            }

            using var kcpObserver = sample.Kcp == null ? null : new KcpLoadObserver(sample.Kcp);

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            sample.WorkingSetStart = process.WorkingSet64;
            sample.WorkingSetSampledPeak = sample.WorkingSetStart;
            if (options.Scenario == "idle")
            {
                sample.Idle = new NetworkIdleSample(clients.Select(client => client.Context.Metrics()).ToArray(),
                    requireIdleHeartbeat, memory.OwnedBytes, serverSnapshot().OwnedBytes);
            }

            TimeSpan cpuStart = process.TotalProcessorTime;
            long allocatedStart = GC.GetTotalAllocatedBytes(false);
            int[] gcStart = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            long started = Stopwatch.GetTimestamp();
            double nextSample = 0;
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds || clients.Any(client => client.Pending.Count != 0))
            {
                Pump(clients);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                // All clients use the same burst clock sample; issuing each batch still costs owner time.
                long burstDue = options.Scenario == "burst"
                    ? NetworkLoadTraffic.DueBursts(elapsed, options.BurstIntervalMilliseconds, plannedSlots) : 0;
                foreach (Client client in clients)
                {
                    NetworkLoadTraffic traffic = client.Traffic;
                    Observe(client, traffic, started, seconds);
                    elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    if (elapsed >= seconds)
                    {
                        traffic.GeneratorLateSkipped += (plannedSlots - client.NextSlot) * requestsPerSlot;
                        client.NextSlot = plannedSlots;
                        continue;
                    }

                    long due = options.Scenario == "burst" ? burstDue :
                        NetworkLoadTraffic.DueSlots(elapsed, options.RatePerConnection, plannedSlots);
                    if (due <= client.NextSlot)
                    {
                        continue;
                    }

                    traffic.GeneratorLateSkipped += (due - client.NextSlot - 1) * requestsPerSlot;
                    client.NextSlot = due;
                    long batchStarted = Stopwatch.GetTimestamp();
                    for (int requestIndex = 0; requestIndex < requestsPerSlot; requestIndex++)
                    {
                        if (Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds)
                        {
                            traffic.GeneratorLateSkipped += requestsPerSlot - requestIndex;
                            break;
                        }

                        if (client.Pending.Count >= options.InflightPerConnection)
                        {
                            traffic.InflightLimitSkipped += requestsPerSlot - requestIndex;
                            break;
                        }

                        StartRequest(client, traffic, options);
                    }

                    sample.BurstClientIssueLatency?.Record(Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
                }

                if (elapsed >= nextSample)
                {
                    SampleResources(sample, clients, serverSnapshot, memory, process);
                    nextSample = elapsed + 0.1;
                }

                if (elapsed > seconds + options.RequestTimeoutSeconds + 5)
                {
                    throw new TimeoutException("Load requests did not reach a terminal outcome during drain.");
                }

                await Task.Delay(options.PumpIntervalMilliseconds);
            }

            // A final stalled generator interval can leave no pending work, so count remaining slots here too.
            foreach (Client client in clients)
            {
                client.Traffic.GeneratorLateSkipped += (plannedSlots - client.NextSlot) * requestsPerSlot;
            }

            sample.MergeOutcomes(sample.Normal);
            if (sample.Slow != null)
            {
                sample.MergeOutcomes(sample.Slow);
            }

            sample.OfferSeconds = seconds;
            sample.DrainSeconds = Math.Max(0, Stopwatch.GetElapsedTime(started).TotalSeconds - seconds);
            SampleResources(sample, clients, serverSnapshot, memory, process);
            sample.WorkingSetEnd = process.WorkingSet64;
            sample.CpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            sample.AllocatedBytes = GC.GetTotalAllocatedBytes(false) - allocatedStart;
            sample.GcCollections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - gcStart[index]).ToArray();
            sample.ElapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            return sample;
        }

        private static void StartRequest(Client client, NetworkLoadTraffic traffic, NetworkLoadOptions options)
        {
            traffic.Attempted++;
            long started = Stopwatch.GetTimestamp();
            try
            {
                Task<EchoResponse> task = client.Context.Request(DiagnosticsRoutes.Echo,
                    new EchoRequest { Text = client.Payload, DelayMs = client.DelayMs },
                    TimeSpan.FromSeconds(options.RequestTimeoutSeconds), CancellationToken.None);
                client.Pending.Add(new Pending(task, started));
            }
            catch (Exception error)
            {
                traffic.RecordFailure(error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        private static async Task<NetworkLoadTraffic> ProbeAsync(List<Client> clients, NetworkLoadOptions options)
        {
            var probe = new NetworkLoadTraffic { Planned = clients.Count };
            long started = Stopwatch.GetTimestamp();
            foreach (Client client in clients)
            {
                StartRequest(client, probe, options);
            }

            while (clients.Any(client => client.Pending.Count != 0))
            {
                Pump(clients);
                foreach (Client client in clients)
                {
                    Observe(client, probe, started, 0);
                }

                if (Stopwatch.GetElapsedTime(started).TotalSeconds > options.RequestTimeoutSeconds + 5)
                {
                    throw new TimeoutException("Post-idle probes did not reach a terminal outcome.");
                }

                await Task.Delay(options.PumpIntervalMilliseconds);
            }

            probe.DrainSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            return probe;
        }

        private static void SampleResources(NetworkLoadSample sample, List<Client> clients,
            Func<(long OwnedBytes, int Connections)> serverSnapshot,
            FrameMemoryBudget memory, Process process)
        {
            NetworkMetricsSnapshot[] snapshots = clients.Select(client => client.Context.Metrics()).ToArray();
            long clientBytes = memory.OwnedBytes;
            var server = serverSnapshot();
            long serverBytes = server.OwnedBytes;
            sample.ClientOwnedBytesSampledPeak = Math.Max(sample.ClientOwnedBytesSampledPeak, clientBytes);
            sample.ServerOwnedBytesSampledPeak = Math.Max(sample.ServerOwnedBytesSampledPeak, serverBytes);
            SampleQueues(sample, snapshots);
            SampleQueues(sample.Normal, snapshots.Where((_, index) => clients[index].DelayMs == 0));
            if (sample.Slow != null)
            {
                SampleQueues(sample.Slow, snapshots.Where((_, index) => clients[index].DelayMs != 0));
            }

            sample.Idle?.Record(snapshots, clients.Count(client => client.Context.State == NetworkClientState.Connected),
                server.Connections, clientBytes, serverBytes);
            sample.Kcp?.Record(snapshots);
            process.Refresh();
            sample.WorkingSetSampledPeak = Math.Max(sample.WorkingSetSampledPeak, process.WorkingSet64);
        }

        private static void SampleQueues(NetworkLoadTraffic traffic, IEnumerable<NetworkMetricsSnapshot> snapshots)
        {
            int pending = 0;
            int send = 0;
            int receive = 0;
            foreach (NetworkMetricsSnapshot snapshot in snapshots)
            {
                pending += snapshot.PendingCount;
                send += snapshot.ReliableSendCount;
                receive += snapshot.ReliableReceiveCount;
            }

            traffic.PendingSampledPeak = Math.Max(traffic.PendingSampledPeak, pending);
            traffic.SendQueueSampledPeak = Math.Max(traffic.SendQueueSampledPeak, send);
            traffic.ReceiveQueueSampledPeak = Math.Max(traffic.ReceiveQueueSampledPeak, receive);
        }
    }
}
