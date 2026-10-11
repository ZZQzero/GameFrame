using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using GameFrame.Network.Transport;
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

            try
            {
                PumpClients();
            }
            catch (Exception error)
            {
                FailHost(error);
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
                    long started = Stopwatch.GetTimestamp();
                    int count = 0;
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
                shutdownTask = ShutdownCoreAsync();
            }

            await shutdownTask;
        }

        private async Task ShutdownCoreAsync()
        {
            stopping = true;
            var tasks = new List<Task>();
            foreach (NetworkClient client in clients.ToArray())
            {
                tasks.Add(client.DisposeAsync().AsTask());
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                stopped = true;
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
