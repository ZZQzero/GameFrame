#if UNITY_EDITOR || GAMEFRAME_NETWORK_PERFORMANCE
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Protocol;
using GameFrame;
using GameFrame.Network;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Game.Hotfix.Validation
{
    public static class NetworkPerformanceProbe
    {
        [Serializable]
        private sealed class Settings
        {
            public string Transport;
            public string Scenario;
            public int Connections;
            public int DurationSeconds;
            public int WarmupSeconds;
            public int RequestsPerSecond;
            public int MaxInFlight;
            public int PendingLimit;
            public int TextLength;
            public bool Unicode;
            public bool Push;
            public int PushMoves;
        }

        [Serializable]
        private sealed class Grant
        {
            public uint Conv;
            public string Identity;
            public string Psk;
            public string Ticket;
        }

        [Serializable]
        private sealed class Ready
        {
            public string Endpoint;
            public Grant[] Grants;
        }

        [Serializable]
        private sealed class Distribution
        {
            public int Count;
            public double MeanMilliseconds;
            public double P95Milliseconds;
            public double P99Milliseconds;
            public double MaximumMilliseconds;

            internal static Distribution From(double[] samples, int count)
            {
                var result = new Distribution { Count = count };
                if (count == 0)
                {
                    return result;
                }
                Array.Sort(samples, 0, count);
                double total = 0;
                for (int i = 0; i < count; i++)
                {
                    total += samples[i];
                }
                result.MeanMilliseconds = total / count;
                result.P95Milliseconds = samples[(int)Math.Ceiling(count * 0.95) - 1];
                result.P99Milliseconds = samples[(int)Math.Ceiling(count * 0.99) - 1];
                result.MaximumMilliseconds = samples[count - 1];
                return result;
            }
        }

        [Serializable]
        private sealed class Duration
        {
            public long Count;
            public double TotalMilliseconds;
            public double MeanMilliseconds;
            public double LifetimeMaximumMilliseconds;

            internal Duration(NetworkDurationSnapshot before, NetworkDurationSnapshot after)
            {
                Count = after.Count - before.Count;
                TotalMilliseconds = after.TotalMilliseconds - before.TotalMilliseconds;
                MeanMilliseconds = Count == 0 ? 0 : TotalMilliseconds / Count;
                LifetimeMaximumMilliseconds = after.MaximumMilliseconds;
            }
        }

        [Serializable]
        private sealed class MemorySample
        {
            public double Seconds;
            public long OwnedBytes;
            public long ManagedHeapBytes;
            public int Pending;
            public int SendQueue;
            public int ReceiveQueue;
            public ulong NativeOwnedBytes;
        }

        [Serializable]
        private sealed class Report
        {
            public bool Passed;
            public string Runtime;
            public string RequestCounterScope;
            public Settings Configuration;
            public double MeasuredSeconds;
            public long Offered;
            public long HarnessLimited;
            public long Successful;
            public long Backpressure;
            public long Timeouts;
            public long Cancellations;
            public long Pushes;
            public double SuccessfulPerSecond;
            public Distribution FrameNetwork;
            public Distribution FrameInterval;
            public Distribution RpcSuccess;
            public Duration Pump;
            public Duration Outbound;
            public Duration OutboundOutsidePump;
            public Duration Dispatch;
            public Duration PushDecoding;
            public Duration PushHandlers;
            public bool MainThreadAllocatedBytesAvailable;
            public long MainThreadAllocatedBytes;
            public double MainThreadAllocatedBytesPerSecond;
            public int[] GcCollections;
            public MemorySample[] Memory;
            public int PeakPending;
            public int PeakSendQueue;
            public int PeakReceiveQueue;
            public long LifetimeLateResponses;
            public long PeakReservedBytes;
            public long ReleasedOwnedBytes;
        }

        private sealed class Probe
        {
            private readonly string directory;
            private readonly Settings settings;
            private readonly double[] frameNetwork = new double[65536];
            private readonly double[] frameInterval = new double[65536];
            private readonly double[] rpc = new double[100000];
            private readonly List<MemorySample> memory = new List<MemorySample>(122);
            private readonly List<IDisposable> subscriptions = new List<IDisposable>();
            private NetworkClient[] clients;
            private ulong[] versions;
            private EchoRequest request;
            private Exception failure;
            private int inFlight;
            private int frameCount;
            private int rpcCount;
            private long successful;
            private long backpressure;
            private long timeouts;
            private long cancellations;
            private long pushes;
            private long offered;
            private long limited;
            private int peakPending;
            private int peakSend;
            private int peakReceive;
            private bool measuring;

            internal Probe(string directory)
            {
                this.directory = directory;
                settings = JsonUtility.FromJson<Settings>(File.ReadAllText(Path.Combine(directory, "performance.json")));
            }

            private static void Check(bool condition, string message)
            {
                if (!condition)
                {
                    throw new InvalidOperationException(message);
                }
            }

            private static double Milliseconds(long started) =>
                (Stopwatch.GetTimestamp() - started) * (1000.0 / Stopwatch.Frequency);

            private async UniTask RequestAsync(NetworkClient client, bool measure)
            {
                inFlight++;
                long started = Stopwatch.GetTimestamp();
                CancellationTokenSource cancellation = settings.Scenario == "cancel" ? new CancellationTokenSource() : null;
                try
                {
                    cancellation?.CancelAfter(20);
                    EchoResponse response = await client.RequestAsync(GameNetRoutes.Echo, request,
                        TimeSpan.FromMilliseconds(settings.Scenario == "timeout" ? 30 : 5000), cancellation?.Token ?? default);
                    Check(response.Text == request.Text && PlayerLoopHelper.IsMainThread, "Echo data/thread mismatch.");
                    if (measure)
                    {
                        Check(rpcCount < rpc.Length, "RPC sample capacity exceeded.");
                        rpc[rpcCount++] = Milliseconds(started);
                        successful++;
                    }
                }
                catch (NetworkBackpressureException) when (settings.Scenario == "backpressure")
                {
                    if (measure)
                    {
                        backpressure++;
                    }
                }
                catch (NetworkRequestTimeoutException) when (settings.Scenario == "timeout")
                {
                    if (measure)
                    {
                        timeouts++;
                    }
                }
                catch (OperationCanceledException error) when (cancellation != null &&
                    cancellation.IsCancellationRequested && error.CancellationToken == cancellation.Token)
                {
                    if (measure)
                    {
                        cancellations++;
                    }
                }
                catch (Exception error)
                {
                    failure = failure ?? error;
                }
                finally
                {
                    cancellation?.Dispose();
                    inFlight--;
                }
            }

            private MemorySample SampleMemory(double seconds)
            {
                var sample = new MemorySample
                {
                    Seconds = seconds, OwnedBytes = GameRuntime.Network.GetMetrics().OwnedBytes,
                    ManagedHeapBytes = GC.GetTotalMemory(false)
                };
                foreach (NetworkClient client in clients)
                {
                    NetworkMetricsSnapshot value = client.GetMetrics();
                    sample.Pending += value.PendingCount;
                    sample.SendQueue += value.ReliableSendCount;
                    sample.ReceiveQueue += value.ReliableReceiveCount;
                    sample.NativeOwnedBytes += value.Kcp.NativeOwnedBytes;
                }
                return sample;
            }

            private async UniTask WindowAsync(int seconds, bool measure)
            {
                long started = Stopwatch.GetTimestamp();
                long previous = started;
                long scheduled = 0;
                double lastMemory = -1;
                NetworkRuntimeMetricsSnapshot previousMetrics = GameRuntime.Network.GetMetrics();
                measuring = measure;
                while (Milliseconds(started) < seconds * 1000.0)
                {
                    if (failure != null)
                    {
                        throw new InvalidOperationException("Performance workload failed.", failure);
                    }
                    double elapsed = Milliseconds(started) / 1000;
                    long due = (long)(elapsed * settings.RequestsPerSecond) - scheduled;
                    scheduled += due;
                    if (measure)
                    {
                        offered += due;
                        limited += Math.Max(0, due - 128);
                    }
                    for (int i = 0; i < Math.Min(128, due); i++)
                    {
                        if (inFlight >= settings.MaxInFlight)
                        {
                            if (measure)
                            {
                                limited++;
                            }
                        }
                        else
                        {
                            RequestAsync(clients[(scheduled - due + i) % clients.Length], measure).Forget();
                        }
                    }
                    await UniTask.NextFrame(PlayerLoopTiming.LastPostLateUpdate);
                    if (measure)
                    {
                        long now = Stopwatch.GetTimestamp();
                        var metrics = GameRuntime.Network.GetMetrics();
                        Check(frameCount < frameNetwork.Length, "Frame sample capacity exceeded.");
                        frameNetwork[frameCount] = metrics.Pump.TotalMilliseconds - previousMetrics.Pump.TotalMilliseconds +
                            metrics.OutboundOutsidePump.TotalMilliseconds - previousMetrics.OutboundOutsidePump.TotalMilliseconds;
                        frameInterval[frameCount++] = (now - previous) * (1000.0 / Stopwatch.Frequency);
                        previous = now;
                        previousMetrics = metrics;
                        foreach (NetworkClient client in clients)
                        {
                            Check(client.State == NetworkClientState.Connected, "Connection failed during workload.");
                            var value = client.GetMetrics();
                            peakPending = Math.Max(peakPending, value.PendingCount);
                            peakSend = Math.Max(peakSend, value.ReliableSendCount);
                            peakReceive = Math.Max(peakReceive, value.ReliableReceiveCount);
                        }
                        if (elapsed - lastMemory >= 1)
                        {
                            memory.Add(SampleMemory(elapsed));
                            lastMemory = elapsed;
                        }
                    }
                }
                measuring = false;
            }

            private async UniTask DrainAsync()
            {
                long started = Stopwatch.GetTimestamp();
                while (inFlight != 0)
                {
                    Check(Milliseconds(started) < 15000, "Workload did not drain.");
                    await UniTask.Yield();
                }
                await UniTask.Delay(200, ignoreTimeScale: true);
                if (failure != null)
                {
                    throw new InvalidOperationException("Performance workload failed.", failure);
                }
            }

            private static bool AllocationCounterAvailable()
            {
                try
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var probe = new byte[4096];
                    long after = GC.GetAllocatedBytesForCurrentThread();
                    GC.KeepAlive(probe);
                    return after > before;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            }

            internal async UniTask RunAsync()
            {
                Check(settings.Connections >= 1 && settings.Connections <= 4 && settings.DurationSeconds >= 1 &&
                    settings.DurationSeconds <= 120 && settings.WarmupSeconds >= 1 && settings.WarmupSeconds <= 30 &&
                    settings.MaxInFlight >= 1 && settings.MaxInFlight <= 512 && settings.PendingLimit >= 1 && settings.PendingLimit <= 512 &&
                    settings.RequestsPerSecond >= 0 && settings.RequestsPerSecond <= 10000 && settings.TextLength >= 0 &&
                    settings.TextLength <= 128 && settings.PushMoves >= 0 && settings.PushMoves <= 225, "Invalid workload bounds.");
                Check((long)settings.RequestsPerSecond * settings.DurationSeconds <= rpc.Length, "RPC sample capacity exceeded by configuration.");
                Check(settings.Transport == "kcp" || settings.Transport == "wss", "Invalid transport.");
                Check(Array.IndexOf(new[] { "idle", "echo", "timeout", "cancel", "backpressure" }, settings.Scenario) >= 0, "Invalid scenario.");
                Check(!Debug.isDebugBuild, "Use the dedicated Release performance build.");
                var ready = JsonUtility.FromJson<Ready>(File.ReadAllText(Path.Combine(directory, "ready.json")));
                File.Delete(Path.Combine(directory, "ready.json"));
                var endpoint = new Uri(ready.Endpoint);
                request = new EchoRequest
                {
                    Text = new string(settings.Unicode ? '棋' : 'x', settings.TextLength),
                    DelayMs = settings.Scenario == "timeout" || settings.Scenario == "cancel" ? 100U : 0U
                };
                await GameRuntime.InitAsync(new GameRuntimeConfig
                {
                    EnableTimer = false, EnableUI = false, NetworkOptions = new NetworkRuntimeOptions()
                });
                clients = new NetworkClient[settings.Connections];
                versions = new ulong[clients.Length];
                try
                {
                    Application.targetFrameRate = 60;
                    QualitySettings.vSyncCount = 0;
                    for (int i = 0; i < clients.Length; i++)
                    {
                        int index = i;
                        clients[i] = GameRuntime.Network.CreateClient(new NetworkClientOptions
                        {
                            TrustedUdpEndpoints = settings.Transport == "kcp" ? new[] { endpoint } : Array.Empty<Uri>(),
                            TrustedWebSocketEndpoints = settings.Transport == "wss" ? new[] { endpoint } : Array.Empty<Uri>(),
                            PendingCount = settings.PendingLimit
                        }, GameNetProtocol.Catalog);
                        subscriptions.Add(clients[i].Subscribe(GameNetRoutes.GomokuRoomSnapshot, message =>
                        {
                            Check(PlayerLoopHelper.IsMainThread && message.Version == ++versions[index] &&
                                message.Moves.Length == settings.PushMoves, "Push sequence, shape or thread mismatch.");
                            if (measuring)
                            {
                                pushes++;
                            }
                        }));
                        if (settings.Transport == "kcp")
                        {
                            Grant grant = ready.Grants[i];
                            byte[] identity = ConvertHex(grant.Identity);
                            byte[] psk = ConvertHex(grant.Psk);
                            byte[] ticket = ConvertHex(grant.Ticket);
                            grant.Identity = grant.Psk = grant.Ticket = null;
                            try
                            {
                                await clients[i].ConnectAsync(NetworkConnectOptions.Kcp(endpoint, ticket, grant.Conv,
                                    identity, psk, NetworkClock.GetMilliseconds() + 30000, unreliableLimit: 0));
                            }
                            finally
                            {
                                Array.Clear(identity, 0, identity.Length);
                                Array.Clear(psk, 0, psk.Length);
                                Array.Clear(ticket, 0, ticket.Length);
                            }
                        }
                        else
                        {
                            await clients[i].ConnectAsync(NetworkConnectOptions.WebSocket(endpoint));
                        }
                    }
                    await WindowAsync(settings.WarmupSeconds, false);
                    await DrainAsync();
                    bool allocationAvailable = AllocationCounterAvailable();
                    File.WriteAllText(Path.Combine(directory, "measuring"), "window begins");
                    var before = GameRuntime.Network.GetMetrics();
                    var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                    long allocated = allocationAvailable ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    long started = Stopwatch.GetTimestamp();
                    await WindowAsync(settings.DurationSeconds, true);
                    double seconds = Milliseconds(started) / 1000;
                    long allocatedBytes = allocationAvailable ? GC.GetAllocatedBytesForCurrentThread() - allocated : 0;
                    for (int i = 0; i < collections.Length; i++)
                    {
                        collections[i] = GC.CollectionCount(i) - collections[i];
                    }
                    var after = GameRuntime.Network.GetMetrics();
                    File.WriteAllText(Path.Combine(directory, "measured"), "window ended");
                    memory.Add(SampleMemory(seconds));
                    await DrainAsync();
                    long late = 0;
                    foreach (NetworkClient client in clients)
                    {
                        late += client.GetMetrics().LateResponses;
                        // A window-outside Echo proves idle/failure workloads can still make progress.
                        EchoResponse echo = await client.RequestAsync(GameNetRoutes.Echo, new EchoRequest { Text = "probe" }, TimeSpan.FromSeconds(5));
                        Check(echo.Text == "probe", "Post-window probe failed.");
                        await client.CloseAsync();
                        var closed = client.GetMetrics();
                        Check(closed.OwnedBytes == 0 && closed.PendingCount == 0 && closed.DeadlineCount == 0 &&
                            closed.Kcp.NativeOwnedBytes == 0 && closed.Kcp.NativeReservedBytes == 0, "Client resources retained.");
                        await client.DisposeAsync();
                    }
                    NetworkRuntime runtime = GameRuntime.Network;
                    await GameRuntime.ShutdownAsync();
                    var final = runtime.GetMetrics();
                    Check(final.OwnedBytes == 0 && final.ClientCount == 0, "Host resources retained.");
                    Check(settings.Scenario == "idle" || successful + timeouts + cancellations > 0, "Workload did not execute.");
                    Check(settings.Scenario != "backpressure" || backpressure > 0, "Backpressure was not exercised.");
                    Check(settings.Scenario != "timeout" || timeouts > 0, "Deadline was not exercised.");
                    Check(settings.Scenario != "cancel" || cancellations > 0, "Cancellation was not exercised.");
                    var report = new Report
                    {
                        Passed = true, Runtime = "Release IL2CPP + HybridCLR", Configuration = settings,
                        RequestCounterScope = "Requests offered during measurement, including their completions during drain; Pushes counts measurement deliveries only.",
                        MeasuredSeconds = seconds, Offered = offered, HarnessLimited = limited, Successful = successful,
                        Backpressure = backpressure, Timeouts = timeouts, Cancellations = cancellations, Pushes = pushes,
                        SuccessfulPerSecond = successful / seconds, FrameNetwork = Distribution.From(frameNetwork, frameCount),
                        FrameInterval = Distribution.From(frameInterval, frameCount), RpcSuccess = Distribution.From(rpc, rpcCount),
                        Pump = new Duration(before.Pump, after.Pump), Outbound = new Duration(before.Outbound, after.Outbound),
                        OutboundOutsidePump = new Duration(before.OutboundOutsidePump, after.OutboundOutsidePump),
                        Dispatch = new Duration(before.Dispatch, after.Dispatch), PushDecoding = new Duration(before.PushDecoding, after.PushDecoding),
                        PushHandlers = new Duration(before.PushHandlers, after.PushHandlers),
                        MainThreadAllocatedBytesAvailable = allocationAvailable, MainThreadAllocatedBytes = allocatedBytes,
                        MainThreadAllocatedBytesPerSecond = allocatedBytes / seconds, GcCollections = collections,
                        Memory = memory.ToArray(), PeakPending = peakPending, PeakSendQueue = peakSend, PeakReceiveQueue = peakReceive,
                        LifetimeLateResponses = late, PeakReservedBytes = after.PeakReservedBytes, ReleasedOwnedBytes = final.OwnedBytes
                    };
                    File.WriteAllText(Path.Combine(directory, "player-result.json"), JsonUtility.ToJson(report, true));
                }
                finally
                {
                    foreach (IDisposable subscription in subscriptions)
                    {
                        subscription.Dispose();
                    }
                    try
                    {
                        foreach (NetworkClient client in clients)
                        {
                            if (client != null)
                            {
                                await client.DisposeAsync();
                            }
                        }
                    }
                    finally
                    {
                        await GameRuntime.ShutdownAsync();
                    }
                }
            }

            private static byte[] ConvertHex(string value)
            {
                var bytes = new byte[value.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
                }
                return bytes;
            }
        }

        public static Task RunAsync(string directory) => new Probe(directory).RunAsync().AsTask();
    }
}
#endif
