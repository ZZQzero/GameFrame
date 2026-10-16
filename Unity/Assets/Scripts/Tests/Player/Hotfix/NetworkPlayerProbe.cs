#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Protocol;
using Game.Protocol.Diagnostics;
using GameFrame;
using GameFrame.Network;
using UnityEngine;

namespace Game.Hotfix.Validation
{
    public static class NetworkPlayerProbe
    {
        [Serializable]
        private sealed class Reply
        {
            public int[] Ports;
            public uint Conv;
            public string Identity;
            public string Psk;
            public string Ticket;
            public bool Ok;
            public bool SessionCompleted;
            public int ServerReason;
            public string ServerErrorType;
            public int HandlerCalls;
        }

        [Serializable]
        private sealed class Command
        {
            public string Operation;
            public int Index;
        }

        [Serializable]
        private sealed class Report
        {
            public bool Passed;
            public string Runtime;
            public string Platform;
            public string[] Checks;
            public long FirstGeneration;
            public long SecondGeneration;
        }

        private sealed class Probe
        {
            private readonly string directory;
            private readonly List<string> checks = new List<string>();
            private int sequence;
            private bool mainThread = true;
            private bool faulted;
            private Uri[] endpoints;

            internal Probe(string directory) => this.directory = directory;

            private static void Check(bool condition, string message)
            {
                if (!condition)
                {
                    throw new InvalidOperationException(message);
                }
            }

            private static async UniTask WaitAsync(Func<bool> ready, int milliseconds = 10000)
            {
                long deadline = NetworkClock.GetMilliseconds() + milliseconds;
                while (!ready())
                {
                    if (NetworkClock.GetMilliseconds() >= deadline)
                    {
                        throw new TimeoutException("Verification condition timed out.");
                    }

                    await UniTask.Delay(20, ignoreTimeScale: true);
                }
            }

            private async UniTask<Reply> CommandAsync(string operation, int index)
            {
                string stem = Path.Combine(directory, (++sequence).ToString("D6"));
                File.WriteAllText(Path.Combine(directory, "sequence.txt"), sequence.ToString());
                File.WriteAllText(stem + ".tmp", JsonUtility.ToJson(new Command { Operation = operation, Index = index }));
                File.Move(stem + ".tmp", stem + ".command.json");
                string path = stem + ".result.json";
                await WaitAsync(() => File.Exists(path));
                Reply reply = JsonUtility.FromJson<Reply>(File.ReadAllText(path));
                File.Delete(path);
                if (operation == "revoke")
                {
                    Check(reply.Ok, "Grant revocation failed.");
                }

                return reply;
            }

            private static byte[] Decode(string text)
            {
                byte[] bytes = new byte[text.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
                }

                return bytes;
            }

            private async UniTask ConnectAsync(NetworkClient client, int index)
            {
                Reply grant = await CommandAsync("grant", index);
                byte[] identity = Decode(grant.Identity);
                byte[] psk = Decode(grant.Psk);
                byte[] ticket = Decode(grant.Ticket);
                grant.Identity = grant.Psk = grant.Ticket = null;
                try
                {
                    await client.ConnectAsync(NetworkConnectOptions.Kcp(endpoints[index], ticket, grant.Conv,
                        identity, psk, NetworkClock.GetMilliseconds() + 30000, unreliableLimit: 0));
                }
                finally
                {
                    Array.Clear(identity, 0, identity.Length);
                    Array.Clear(psk, 0, psk.Length);
                    Array.Clear(ticket, 0, ticket.Length);
                }

                Check(PlayerLoopHelper.IsMainThread && client.State == NetworkClientState.Connected, "Connect delivery failed.");
                Check(client.GetMetrics().Kcp.NativeOwnedBytes > 0, "Native allocation was not observed.");
            }

            private static async UniTask EchoAsync(NetworkClient client, string text)
            {
                EchoResponse response = await client.RequestAsync(DiagnosticsRoutes.Echo, new EchoRequest { Text = text },
                    TimeSpan.FromSeconds(3));
                Check(response.Text == text && PlayerLoopHelper.IsMainThread, "Echo serialization or delivery failed.");
            }

            private static void Released(NetworkClient client)
            {
                NetworkMetricsSnapshot metrics = client.GetMetrics();
                Check(client.State == NetworkClientState.Idle && metrics.OwnedBytes == 0 &&
                    metrics.PendingCount == 0 && metrics.DeadlineCount == 0 &&
                    metrics.ReliableSendCount == 0 && metrics.ReliableReceiveCount == 0 &&
                    metrics.Kcp.NativeOwnedBytes == 0 && metrics.Kcp.NativeReservedBytes == 0 &&
                    metrics.Kcp.Waitsnd == 0 && metrics.Kcp.InputQueueCount == 0 &&
                    metrics.Kcp.OutputQueueCount == 0 && metrics.Kcp.PlaintextQueueCount == 0,
                    "Client resources remain after Close.");
            }

            private async UniTask ServerClosedAsync(int index)
            {
                long deadline = NetworkClock.GetMilliseconds() + 10000;
                Reply status;
                do
                {
                    status = await CommandAsync("status", index);
                    Check(NetworkClock.GetMilliseconds() < deadline, "Server close timed out.");
                    await UniTask.Delay(20, ignoreTimeScale: true);
                }
                while (!status.SessionCompleted);

                Check(status.ServerReason == 1 && string.IsNullOrEmpty(status.ServerErrorType), "Server close was abnormal.");
            }

            private async UniTask HandlerReachedAsync(int calls)
            {
                long deadline = NetworkClock.GetMilliseconds() + 10000;
                while ((await CommandAsync("status", 0)).HandlerCalls < calls)
                {
                    Check(NetworkClock.GetMilliseconds() < deadline, "Server handler was not reached.");
                    await UniTask.Delay(20, ignoreTimeScale: true);
                }
            }

            internal async UniTask RunAsync()
            {
                Reply ready = JsonUtility.FromJson<Reply>(File.ReadAllText(Path.Combine(directory, "ready.json")));
                endpoints = new Uri[ready.Ports.Length];
                for (int i = 0; i < endpoints.Length; i++)
                {
                    endpoints[i] = new Uri("udp://127.0.0.1:" + ready.Ports[i]);
                }

                await GameRuntime.InitAsync(new GameRuntimeConfig
                {
                    EnableTimer = false, EnableUI = false, NetworkOptions = new NetworkRuntimeOptions()
                });
                NetworkClient client = null;
                IDisposable observer = null;
                try
                {
                    client = GameRuntime.Network.CreateClient(new NetworkClientOptions { TrustedUdpEndpoints = endpoints }, GameNetProtocol.Catalog);
                    observer = client.ObserveState(change =>
                    {
                        mainThread &= PlayerLoopHelper.IsMainThread;
                        faulted |= change.State == NetworkClientState.Faulted;
                    });
                    await ConnectAsync(client, 0);
                    long first = client.Generation;
                    checks.Add("DTLS + GFNP Hello + native allocation");
                    await EchoAsync(client, "五子棋 IL2CPP / UTF-8 ♟");
                    checks.Add("MemoryPack UTF-8 Echo on main thread");

                    using (var cancellation = new CancellationTokenSource())
                    {
                        Task<EchoResponse> request = client.RequestAsync(DiagnosticsRoutes.Echo,
                            new EchoRequest { Text = "cancel", DelayMs = 800 }, TimeSpan.FromSeconds(3), cancellation.Token).AsTask();
                        await HandlerReachedAsync(2);
                        cancellation.Cancel();
                        try
                        {
                            await request;
                            throw new InvalidOperationException("Request cancellation unexpectedly succeeded.");
                        }
                        catch (OperationCanceledException error)
                        {
                            Check(error.CancellationToken == cancellation.Token && PlayerLoopHelper.IsMainThread, "Caller cancellation token changed.");
                        }
                    }

                    await WaitAsync(() => client.GetMetrics().LateResponses == 1);
                    await EchoAsync(client, "after-cancel");
                    checks.Add("Caller cancellation + late response isolation + subsequent Echo");

                    try
                    {
                        await client.RequestAsync(DiagnosticsRoutes.Echo, new EchoRequest { Text = "deadline", DelayMs = 800 },
                            TimeSpan.FromMilliseconds(200));
                        throw new InvalidOperationException("Request deadline unexpectedly succeeded.");
                    }
                    catch (NetworkRequestTimeoutException)
                    {
                        Check(PlayerLoopHelper.IsMainThread, "Deadline completed off main thread.");
                    }

                    await WaitAsync(() => client.GetMetrics().LateResponses == 2);
                    await EchoAsync(client, "after-deadline");
                    checks.Add("RPC deadline + late response isolation + subsequent Echo");

                    Task<EchoResponse> pending = client.RequestAsync(DiagnosticsRoutes.Echo,
                        new EchoRequest { Text = "close-pending", DelayMs = 5000 }, TimeSpan.FromSeconds(10)).AsTask();
                    await HandlerReachedAsync(6);
                    await client.CloseAsync();
                    try
                    {
                        await pending;
                        throw new InvalidOperationException("Close left a successful pending request.");
                    }
                    catch (NetworkException error)
                    {
                        Check(error.GetType() == typeof(NetworkException) && PlayerLoopHelper.IsMainThread,
                            "Explicit Close did not deliver the local network termination on main thread.");
                    }

                    Released(client);
                    await ServerClosedAsync(0);
                    await CommandAsync("revoke", 0);
                    checks.Add("Close with pending RPC + client/native queues released + server Close1");
                    await ConnectAsync(client, 2);
                    long second = client.Generation;
                    Check(second == first + 1 && client.GetMetrics().RequestCancellations == 0 &&
                        client.GetMetrics().RequestTimeouts == 0, "New generation inherited old state.");
                    await EchoAsync(client, "new-generation");
                    await client.CloseAsync();
                    Released(client);
                    await ServerClosedAsync(2);
                    await CommandAsync("revoke", 2);
                    Check(mainThread && !faulted, "State notification threading or fault invariant failed.");
                    checks.Add("Same client reconnect with fresh grant + generation isolation + second Close1");
                    observer.Dispose();
                    observer = null;
                    await client.DisposeAsync();
                    Check(client.State == NetworkClientState.Disposed, "Client did not dispose.");
                    await GameRuntime.ShutdownAsync();
                    Check(!GameRuntime.IsInited, "Runtime shutdown failed.");
                    checks.Add("Dispose + runtime/worker shutdown");
                    File.WriteAllText(Path.Combine(directory, "player-result.json"), JsonUtility.ToJson(new Report
                    {
                        Passed = true, Runtime = "IL2CPP + HybridCLR", Platform = Application.platform.ToString(),
                        Checks = checks.ToArray(), FirstGeneration = first, SecondGeneration = second
                    }, true));
                }
                finally
                {
                    observer?.Dispose();
                    try
                    {
                        if (client != null)
                        {
                            await client.DisposeAsync();
                        }
                    }
                    finally
                    {
                        await GameRuntime.ShutdownAsync();
                    }
                }
            }
        }

        // Reflection is confined to the standalone verification bootstrap, never Launch.
        public static Task RunAsync(string directory) => new Probe(directory).RunAsync().AsTask();
    }
}
#endif
