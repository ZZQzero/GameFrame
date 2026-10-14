#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameFrame.Network
{
    internal sealed class KcpWorker
    {
        private readonly object gate = new object();
        private readonly List<KcpTransport> connections = new List<KcpTransport>(4);
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread thread;
        private readonly int capacity;
        private bool stopping;
        private Exception failure;
        internal Exception Failure
        {
            get
            {
                lock (gate)
                {
                    return failure;
                }
            }
        }

        internal KcpWorker(int capacity = 4)
        {
            this.capacity = capacity;
            thread = new Thread(Run) { IsBackground = true, Name = "GameFrame KCP" };
            thread.Start();
        }

        internal void Add(KcpTransport transport)
        {
            lock (gate)
            {
                if (stopping || failure != null)
                {
                    throw new NetworkException("KCP worker is unavailable.", failure);
                }

                if (connections.Count >= capacity)
                {
                    throw new NetworkBackpressureException("KCP worker connection capacity exceeded.");
                }

                connections.Add(transport);
                signal.Set();
            }
        }

        internal void Wake()
        {
            lock (gate)
            {
                if (!stopped.Task.IsCompleted)
                {
                    signal.Set();
                }
            }
        }

        internal Task ShutdownAsync()
        {
            lock (gate)
            {
                stopping = true;
                if (!stopped.Task.IsCompleted)
                {
                    signal.Set();
                }

                return stopped.Task;
            }
        }

        private void Run()
        {
            var active = new KcpTransport[capacity];
            int activeCount = 0;
            int cursor = 0;
            try
            {
                while (true)
                {
                    bool stop;
                    lock (gate)
                    {
                        Array.Clear(active, 0, activeCount);
                        activeCount = connections.Count;
                        connections.CopyTo(active);
                        stop = stopping;
                    }

                    if (stop)
                    {
                        break;
                    }

                    long now = NetworkClock.GetMilliseconds();
                    for (int n = 0; n < activeCount; n++)
                    {
                        KcpTransport transport = active[(cursor + n) % activeCount];
                        transport.Step(now);
                        if (transport.StoppedTask.IsCompleted)
                        {
                            lock (gate)
                            {
                                connections.Remove(transport);
                            }
                        }
                    }

                    cursor = activeCount == 0 ? 0 : (cursor + 1) % activeCount;
                    signal.WaitOne(activeCount == 0 ? Timeout.Infinite : 10);
                }
            }
            catch (Exception error)
            {
                lock (gate)
                {
                    failure = error;
                    stopping = true;
                    Array.Clear(active, 0, activeCount);
                    activeCount = connections.Count;
                    connections.CopyTo(active);
                }
            }
            finally
            {
                for (int i = 0; i < activeCount; i++)
                {
                    active[i].StopOnOwner(failure ?? new NetworkException("KCP worker shut down."));
                }

                lock (gate)
                {
                    connections.Clear();
                    if (failure == null)
                    {
                        stopped.TrySetResult(true);
                    }
                    else
                    {
                        stopped.TrySetException(failure);
                    }
                    signal.Dispose();
                }
            }
        }
    }
}
