using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace GameFrame.Network
{
    public sealed class NetworkRuntime
    {
        private readonly NetworkRuntimeOptions options;
        private readonly List<NetworkClient> clients = new List<NetworkClient>();
        private readonly NetworkRuntimeDriver driver;
        private bool stopping;
        private bool stopped;
        private int nextClient;
        private Task shutdownTask;
        private Exception failure;
        private bool faultCleanupRunning;
        private KcpWorker kcpWorker;
        private int pumpDepth;
        private NetworkDurationSnapshot pumpDuration;
        private NetworkDurationSnapshot outboundDuration;
        private NetworkDurationSnapshot outboundOutsidePumpDuration;
        private NetworkDurationSnapshot dispatchDuration;
        private NetworkDurationSnapshot pushDecodingDuration;
        private NetworkDurationSnapshot pushHandlerDuration;
        private long outboundRejected;
        private long pushDecodeFailures;
        private long pushHandlerFailures;

        internal bool IsPumping => pumpDepth != 0;

        public NetworkRuntimeMetricsSnapshot GetMetrics()
        {
            RequireMainThread();
            return new NetworkRuntimeMetricsSnapshot(pumpDuration, outboundDuration, outboundOutsidePumpDuration,
                dispatchDuration, pushDecodingDuration, pushHandlerDuration, outboundRejected,
                pushDecodeFailures, pushHandlerFailures, clients.Count, Memory.OwnedBytes, Memory.PeakReservedBytes);
        }

        private static double ElapsedMilliseconds(long started) =>
            (Stopwatch.GetTimestamp() - started) * (1000.0 / Stopwatch.Frequency);

        internal void RecordOutbound(long started, bool outsidePump, bool admitted)
        {
            double elapsed = ElapsedMilliseconds(started);
            outboundDuration = outboundDuration.Add(elapsed);
            if (outsidePump)
            {
                outboundOutsidePumpDuration = outboundOutsidePumpDuration.Add(elapsed);
            }
            if (!admitted)
            {
                outboundRejected++;
            }
        }

        internal void RecordDispatch(long started)
        {
            dispatchDuration = dispatchDuration.Add(ElapsedMilliseconds(started));
        }

        internal void RecordPushDecoding(long started, bool success)
        {
            pushDecodingDuration = pushDecodingDuration.Add(ElapsedMilliseconds(started));
            if (!success)
            {
                pushDecodeFailures++;
            }
        }

        internal void RecordPushHandler(long started, bool success)
        {
            pushHandlerDuration = pushHandlerDuration.Add(ElapsedMilliseconds(started));
            if (!success)
            {
                pushHandlerFailures++;
            }
        }

        private long BeginPump()
        {
            pumpDepth++;
            return Stopwatch.GetTimestamp();
        }

        private void EndPump(long started)
        {
            // Fault cleanup may start synchronously inside an already running Pump.
            if (--pumpDepth == 0)
            {
                pumpDuration = pumpDuration.Add(ElapsedMilliseconds(started));
            }
        }

        internal KcpWorker GetKcpWorker()
        {
            RequireMainThread();
            RequireAvailable();
            return kcpWorker ?? (kcpWorker = new KcpWorker(options.MaxClients));
        }

        internal FrameMemoryBudget Memory { get; }

        internal NetworkRuntime(Transform root, NetworkRuntimeOptions options)
        {
            RequireMainThread();
            this.options = options.Snapshot();
            Memory = new FrameMemoryBudget(this.options.MaxOwnedBytes);
            var driverObject = new GameObject("[Network]");
            driverObject.transform.SetParent(root, false);
            driver = driverObject.AddComponent<NetworkRuntimeDriver>();
            driver.Initialize(this);
        }

        public NetworkClient CreateClient(NetworkClientOptions clientOptions, NetProtocolCatalog protocol)
        {
            RequireMainThread();
            RequireAvailable();

            if (clientOptions == null || protocol == null)
            {
                throw new ArgumentNullException(clientOptions == null ? nameof(clientOptions) : nameof(protocol));
            }

            if (clients.Count >= options.MaxClients)
            {
                throw new NetworkBackpressureException("Network runtime client capacity exceeded.");
            }

            var client = new NetworkClient(this, clientOptions.Snapshot(), protocol);
            clients.Add(client);
            return client;
        }

        internal void Pump()
        {
            if (failure != null || stopped)
            {
                return;
            }

            long started = BeginPump();
            try
            {
                Exception workerFailure = kcpWorker?.Failure;
                if (workerFailure != null)
                {
                    FailHost(workerFailure);
                    return;
                }

                PumpClients();
            }
            catch (Exception error)
            {
                FailHost(error);
            }
            finally
            {
                EndPump(started);
            }
        }

        private void PumpClients()
        {
            long started = Stopwatch.GetTimestamp();
            double budgetTicks = options.PumpBudget.TotalSeconds * Stopwatch.Frequency;
            int count = 0;
            int emptyRounds = 0;
            while (clients.Count != 0 && count < options.MaxMessagesPerPump &&
                Stopwatch.GetTimestamp() - started < budgetTicks && emptyRounds < clients.Count)
            {
                if (nextClient >= clients.Count)
                {
                    nextClient = 0;
                }

                NetworkClient client = clients[nextClient++];
                bool worked = false;
                for (int i = 0; i < 16 && count < options.MaxMessagesPerPump &&
                    Stopwatch.GetTimestamp() - started < budgetTicks; i++)
                {
                    if (!client.PumpOne())
                    {
                        break;
                    }

                    worked = true;
                    count++;
                }

                emptyRounds = worked ? 0 : emptyRounds + 1;
            }
        }

        internal void RequireAvailable()
        {
            if (failure != null)
            {
                throw new NetworkException("Network host has failed. Shut it down before creating a new host.", failure);
            }

            if (stopping || stopped)
            {
                throw new InvalidOperationException("Network runtime is shutting down.");
            }
        }

        internal void FailHost(Exception error)
        {
            RequireMainThread();
            if (stopped || failure != null)
            {
                return;
            }

            failure = error;
            // Notifications can dispose a client and mutate the host collection.
            foreach (NetworkClient client in clients.ToArray())
            {
                client.FailHost(error);
            }

            EnsureFaultCleanup();
            UnityEngine.Debug.LogException(error);
        }

        internal void EnsureFaultCleanup()
        {
            if (failure != null && !faultCleanupRunning && !stopped)
            {
                faultCleanupRunning = true;
                PumpFaultCleanupAsync().Forget();
            }
        }

        private async UniTask PumpFaultCleanupAsync()
        {
            try
            {
                bool complete;
                do
                {
                    complete = true;
                    long started = BeginPump();
                    int count = 0;
                    try
                    {
                        foreach (NetworkClient client in clients.ToArray())
                        {
                            for (int i = 0; i < 16 && count < options.MaxMessagesPerPump &&
                                Stopwatch.GetTimestamp() - started < options.PumpBudget.TotalSeconds * Stopwatch.Frequency; i++)
                            {
                                if (!client.PumpOne())
                                {
                                    break;
                                }

                                count++;
                            }

                            complete &= client.HostCleanupComplete;
                        }
                    }
                    finally
                    {
                        EndPump(started);
                    }

                    if (!complete)
                    {
                        // PlayerLoop cleanup remains available if the owned Runner/root was destroyed.
                        await UniTask.Yield(PlayerLoopTiming.Update);
                    }
                }
                while (!complete);
            }
            finally
            {
                faultCleanupRunning = false;
            }
        }

        public async UniTask ShutdownAsync()
        {
            RequireMainThread();
            if (shutdownTask == null)
            {
                var completion = new TaskCompletionSource<bool>();
                shutdownTask = completion.Task;
                _ = ShutdownCoreAsync(completion);
            }

            await shutdownTask;
        }

        private async Task ShutdownCoreAsync(TaskCompletionSource<bool> completion)
        {
            stopping = true;
            Exception failure = null;
            try
            {
                var tasks = new List<Task>();
                foreach (NetworkClient client in clients.ToArray())
                {
                    tasks.Add(client.DisposeAsync().AsTask());
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception error)
            {
                failure = error;
            }

            try
            {
                if (kcpWorker != null)
                {
                    await kcpWorker.ShutdownAsync();
                }
            }
            catch (Exception error)
            {
                if (failure == null)
                {
                    failure = error;
                }
                else
                {
                    UnityEngine.Debug.LogException(error);
                }
            }
            finally
            {
                stopped = true;
                try
                {
                    if (driver != null)
                    {
                        if (Application.isPlaying)
                        {
                            UnityEngine.Object.Destroy(driver.gameObject);
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(driver.gameObject);
                        }
                    }
                }
                catch (Exception error)
                {
                    if (failure == null)
                    {
                        failure = error;
                    }
                    else
                    {
                        UnityEngine.Debug.LogException(error);
                    }
                }
            }

            if (failure == null)
            {
                completion.TrySetResult(true);
            }
            else
            {
                completion.TrySetException(failure);
            }
        }

        internal void Remove(NetworkClient client)
        {
            clients.Remove(client);
        }

        internal static void RequireMainThread()
        {
            if (!PlayerLoopHelper.IsMainThread)
            {
                throw new InvalidOperationException("Network business APIs require the Unity main thread.");
            }
        }
    }

    [DisallowMultipleComponent]
    internal sealed class NetworkRuntimeDriver : MonoBehaviour
    {
        private NetworkRuntime runtime;
        private bool quitting;

        internal void Initialize(NetworkRuntime runtime)
        {
            this.runtime = runtime;
        }

        private void Update()
        {
            runtime?.Pump();
        }

        private void OnApplicationQuit()
        {
            quitting = true;
        }

        private void OnDisable()
        {
            if (!quitting)
            {
                runtime?.FailHost(new NetworkException("Network driver was disabled or destroyed before shutdown completed."));
            }
        }
    }
}
