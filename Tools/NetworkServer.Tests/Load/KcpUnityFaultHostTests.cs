using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Game.Protocol;
using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    // An explicit companion process for Unity's public-client PlayMode tests.
    // Control files never enter GFNP or the production application.
    public sealed class KcpUnityFaultHostTests
    {
        private sealed class Command
        {
            public string Operation { get; set; } = "";
            public int Index { get; set; }
            public bool Enabled { get; set; }
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

        private static void Publish(string path, object value)
        {
            string temporary = path + ".tmp";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(temporary, options))
                {
                    stream.Write(bytes);
                }

                File.Move(temporary, path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        [Test]
        [Explicit("Companion host; run from NetworkKcpFaultTests with the documented opt-in environment.")]
        public async Task ServeUnityFaults()
        {
            string directory = Path.GetFullPath(TestContext.Parameters.Get("HostDirectory", ""));
            Assert.That(Directory.Exists(directory), Is.True);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            KcpTestLibrary.Load();
            var grants = new Dictionary<int, ulong>();
            var indices = new ConcurrentDictionary<ulong, int>();
            var sessions = new ConcurrentDictionary<int, ServerConnectionContext>();
            var closed = new ConcurrentDictionary<int, ServerCloseInfo>();
            var calls = new ConcurrentDictionary<int, int>();
            using var factoryRelease = new ManualResetEventSlim(false);
            bool holdHello = false;
            bool factoryEntered = false;
            KcpLoadRelay? relay = null;
            var application = new ServerApplication(GameNetProtocol.Catalog, connection =>
            {
                int index = indices[connection.GrantId];
                sessions[index] = connection;
                if (index == 0 && Volatile.Read(ref holdHello))
                {
                    relay!.SetBlackhole(0, true, 1);
                    Volatile.Write(ref factoryEntered, true);
                    if (!factoryRelease.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Unity test did not release its factory barrier.");
                    }
                }

                return new Session(info => closed[index] = info);
            });
            application.Rpc(GameNetRoutes.Echo, async (connection, request, cancellation) =>
            {
                int index = indices[connection.GrantId];
                calls.AddOrUpdate(index, 1, (_, count) => count + 1);
                await Task.Delay((int)request.DelayMs, cancellation);
                return new EchoResponse { Text = request.Text };
            });
            foreach (NetRoute route in GameNetProtocol.Catalog.Routes)
            {
                if (route.Kind != NetRouteKind.Push && route != GameNetRoutes.Echo)
                {
                    application.Reject(route);
                }
            }

            var host = new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), application);
            bool stopped = false;
            try
            {
                await host.Started.WaitAsync(TimeSpan.FromSeconds(5));
                relay = new KcpLoadRelay(host.LocalEndpoint!, 3, "direct", 1);
                Publish(Path.Combine(directory, "ready.json"), new
                {
                    Ports = Enumerable.Range(0, 3).Select(index => relay.Endpoint(index).Port).ToArray()
                });
                long started = Stopwatch.GetTimestamp();
                for (int sequence = 1; ; sequence++)
                {
                    string path = Path.Combine(directory, $"{sequence:D6}.command.json");
                    while (!File.Exists(path))
                    {
                        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(3))
                        {
                            throw new TimeoutException("Unity companion host lifetime exceeded.");
                        }

                        await Task.Delay(10);
                    }

                    Command command = JsonSerializer.Deserialize<Command>(await File.ReadAllTextAsync(path))!;
                    File.Delete(path);
                    Assert.That(command.Index, Is.InRange(0, 2));
                    object response;
                    switch (command.Operation)
                    {
                        case "grant":
                        {
                            Assert.That(grants.ContainsKey(command.Index), Is.False);
                            byte[] identity = RandomNumberGenerator.GetBytes(16);
                            byte[] psk = RandomNumberGenerator.GetBytes(32);
                            byte[] ticket = RandomNumberGenerator.GetBytes(32);
                            try
                            {
                                uint conv = (uint)command.Index + 1;
                                ulong grant = await host.RegisterGrantAsync(conv, identity, psk, ticket);
                                grants.Add(command.Index, grant);
                                indices[grant] = command.Index;
                                response = new
                                {
                                    Conv = conv, Identity = Convert.ToHexString(identity),
                                    Psk = Convert.ToHexString(psk), Ticket = Convert.ToHexString(ticket)
                                };
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(identity);
                                CryptographicOperations.ZeroMemory(psk);
                                CryptographicOperations.ZeroMemory(ticket);
                            }

                            break;
                        }
                        case "cut":
                            relay.SetBlackhole(command.Index, command.Enabled);
                            response = new { Ok = true };
                            break;
                        case "hold":
                            relay.SetHold(command.Index, 1, command.Enabled);
                            response = new { Ok = true };
                            break;
                        case "arm-hello":
                            Volatile.Write(ref holdHello, true);
                            response = new { Ok = true };
                            break;
                        case "release-hello":
                            factoryRelease.Set();
                            response = new { Ok = true };
                            break;
                        case "revoke":
                            await host.RevokeGrantAsync(grants[command.Index]);
                            if (sessions.TryGetValue(command.Index, out ServerConnectionContext? revoked))
                            {
                                await revoked.Completion.WaitAsync(TimeSpan.FromSeconds(8));
                            }

                            response = new { Ok = true };
                            break;
                        case "status":
                        {
                            KcpRelayReport snapshot = relay.Snapshot();
                            closed.TryGetValue(command.Index, out ServerCloseInfo outcome);
                            response = new
                            {
                                FactoryEntered = Volatile.Read(ref factoryEntered),
                                SessionCompleted = sessions.TryGetValue(command.Index, out ServerConnectionContext? session) && session.Completion.IsCompleted,
                                ServerReason = outcome.Reason,
                                ServerErrorType = outcome.Error?.GetType().FullName,
                                HandlerCalls = calls.GetValueOrDefault(command.Index),
                                snapshot.ClientToServerFaultDrops, snapshot.ServerToClientFaultDrops,
                                snapshot.HeldPackets, snapshot.ReleasedHeldPackets, snapshot.QueuePackets,
                                snapshot.AccountingValid, snapshot.QueueOverflowDrops
                            };
                            break;
                        }
                        case "stop":
                        {
                            factoryRelease.Set();
                            await relay.DisposeAsync();
                            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                            stopped = true;
                            KcpRelayReport final = relay.Snapshot();
                            bool released = final.Released && final.AccountingValid && final.QueueOverflowDrops == 0 &&
                                host.OwnedBytes == 0 && sessions.Count == closed.Count &&
                                sessions.Values.All(session => session.Completion.IsCompletedSuccessfully);
                            bool normal = closed.All(pair => pair.Key == 0 || (pair.Value.Reason == 1 && pair.Value.Error == null));
                            Publish(Path.Combine(directory, $"{sequence:D6}.result.json"), new
                            {
                                Ok = released && normal, ResourcesReleased = released, NormalSessionsClosed = normal,
                                final.HeldPackets, final.ReleasedHeldPackets, final.AccountingValid
                            });
                            Assert.That(released && normal, Is.True, "Unity companion resource or control/new-session close invariant failed.");
                            return;
                        }
                        default:
                            throw new ArgumentException("Unknown Unity test host operation.");
                    }

                    Publish(Path.Combine(directory, $"{sequence:D6}.result.json"), response);
                }
            }
            finally
            {
                factoryRelease.Set();
                if (!stopped)
                {
                    try
                    {
                        if (relay != null)
                        {
                            await relay.DisposeAsync();
                        }
                    }
                    finally
                    {
                        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
        }
    }
}
