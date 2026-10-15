using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace GameFrame.Network
{
    public sealed class NetworkClient : IAsyncDisposable
    {
        private sealed class Subscription : IDisposable
        {
            private Action remove;

            public Subscription(Action remove)
            {
                this.remove = remove;
            }

            public void Dispose()
            {
                NetworkRuntime.RequireMainThread();
                Action action = remove;
                remove = null;
                action?.Invoke();
            }
        }

        private readonly NetworkRuntime runtime;
        private readonly NetworkClientOptions options;
        private readonly NetProtocolCatalog catalog;
        private readonly Dictionary<uint, Action<OwnedFrame>> pushes = new Dictionary<uint, Action<OwnedFrame>>();
        private readonly List<Action<NetworkStateChange>> stateObservers = new List<Action<NetworkStateChange>>();
        private readonly List<Action<NetworkMessageFault>> faultObservers = new List<Action<NetworkMessageFault>>();
        private ConnectionContext context;
        private long generation;
        private NetworkMetricsSnapshot lastMetrics;
        private bool disposed;
        private Task closeTask;
        private Task disposeTask;
        private Exception hostFailure;

        public NetworkClientState State
        {
            get
            {
                NetworkRuntime.RequireMainThread();
                return disposed ? NetworkClientState.Disposed : context?.State ??
                    (hostFailure == null ? NetworkClientState.Idle : NetworkClientState.Faulted);
            }
        }

        public long Generation
        {
            get
            {
                NetworkRuntime.RequireMainThread();
                return generation;
            }
        }

        internal NetworkClient(NetworkRuntime runtime, NetworkClientOptions options, NetProtocolCatalog catalog)
        {
            this.runtime = runtime;
            this.options = options;
            this.catalog = catalog;
        }

        public async UniTask ConnectAsync(NetworkConnectOptions connect, CancellationToken cancellationToken = default)
        {
            RequireAlive();
            runtime.RequireAvailable();
            if (context != null)
            {
                throw new InvalidOperationException("Await CloseAsync before reconnecting.");
            }

            if (connect == null)
            {
                throw new ArgumentNullException(nameof(connect));
            }

            connect.Validate(options, catalog, NetworkClock.GetMilliseconds());
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException("The browser WebSocket adapter has not been installed.");
#else
            if (generation == long.MaxValue)
            {
                throw new NetworkException("Connection generation exhausted.");
            }

            context = new ConnectionContext(++generation, options, catalog, connect, runtime.Memory, cancellationToken,
                kcpWorker: connect.Transport == NetworkTransportKind.Kcp ? runtime.GetKcpWorker() : null);
            ConnectionContext started = context;
            started.StateChanged = (state, error) => Notify(stateObservers, new NetworkStateChange(state, started.Generation, error));
            started.MessageFault = (opcode, error) => Notify(faultObservers, new NetworkMessageFault(opcode, started.Generation, error));
            started.Push = (opcode, frame) =>
            {
                if (ReferenceEquals(context, started) && pushes.TryGetValue(opcode, out Action<OwnedFrame> handler))
                {
                    handler(frame);
                }
            };
            Notify(stateObservers, new NetworkStateChange(NetworkClientState.Connecting, generation, null));
            started.Start();
            await started.ConnectTask;
#endif
        }

        public async UniTask<TResponse> RequestAsync<TRequest, TResponse>(NetRpc<TRequest, TResponse> route,
            TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
            where TRequest : class where TResponse : class
        {
            NetworkRuntime.RequireMainThread();
            long started = Stopwatch.GetTimestamp();
            bool outsidePump = !runtime.IsPumping;
            bool admitted = false;
            Task<TResponse> response;
            try
            {
                RequireConnected();
                response = context.Request(route, request, timeout, cancellationToken);
                admitted = true;
            }
            finally
            {
                runtime.RecordOutbound(started, outsidePump, admitted);
            }

            return await response;
        }

        public UniTask SendAsync<T>(NetSend<T> route, T message, CancellationToken cancellationToken = default) where T : class
        {
            NetworkRuntime.RequireMainThread();
            long started = Stopwatch.GetTimestamp();
            bool outsidePump = !runtime.IsPumping;
            bool admitted = false;
            try
            {
                RequireConnected();
                context.Send(route, message, cancellationToken);
                admitted = true;
                return UniTask.CompletedTask;
            }
            finally
            {
                runtime.RecordOutbound(started, outsidePump, admitted);
            }
        }

        public bool TrySendUnreliable<T>(NetUnreliable<T> route, T message) where T : class
        {
            NetworkRuntime.RequireMainThread();
            long started = Stopwatch.GetTimestamp();
            bool outsidePump = !runtime.IsPumping;
            bool admitted = false;
            try
            {
                RequireConnected();
                catalog.RequireRegistered(route);
                admitted = context.TrySendUnreliable(route, message);
                return admitted;
            }
            finally
            {
                runtime.RecordOutbound(started, outsidePump, admitted);
            }
        }

        public IDisposable Subscribe<T>(NetPush<T> route, Action<T> handler) where T : class
        {
            RequireAlive();
            catalog.RequireRegistered(route);
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (pushes.ContainsKey(route.Opcode))
            {
                throw new InvalidOperationException($"Opcode {route.Opcode} already has a handler.");
            }

            Action<OwnedFrame> registered = frame =>
            {
                long started = Stopwatch.GetTimestamp();
                bool success = false;
                T message;
                try
                {
                    message = route.Codec.DecodeValidated(frame.Body);
                    success = true;
                }
                finally
                {
                    runtime.RecordPushDecoding(started, success);
                }

                started = Stopwatch.GetTimestamp();
                success = false;
                try
                {
                    handler(message);
                    success = true;
                }
                finally
                {
                    runtime.RecordPushHandler(started, success);
                }
            };
            pushes.Add(route.Opcode, registered);
            return new Subscription(() =>
            {
                if (pushes.TryGetValue(route.Opcode, out Action<OwnedFrame> current) && ReferenceEquals(current, registered))
                {
                    pushes.Remove(route.Opcode);
                }
            });
        }

        public IDisposable ObserveState(Action<NetworkStateChange> handler) => Observe(stateObservers, handler);
        public IDisposable ObserveMessageFault(Action<NetworkMessageFault> handler) => Observe(faultObservers, handler);

        private IDisposable Observe<T>(List<Action<T>> observers, Action<T> handler)
        {
            RequireAlive();
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (observers.Count >= 64)
            {
                throw new NetworkBackpressureException("Observer capacity exceeded.");
            }

            // Use a distinct registration delegate even if the same callback is registered twice.
            Action<T> registration = value => handler(value);
            observers.Add(registration);
            return new Subscription(() => observers.Remove(registration));
        }

        private static void Notify<T>(List<Action<T>> observers, T value)
        {
            foreach (Action<T> observer in observers.ToArray())
            {
                try
                {
                    observer(value);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    break;
                }
            }
        }

        public NetworkMetricsSnapshot GetMetrics()
        {
            RequireAlive();
            return context?.Metrics() ?? lastMetrics;
        }

        public async UniTask CloseAsync()
        {
            NetworkRuntime.RequireMainThread();
            if (disposed || context == null)
            {
                return;
            }

            Task task = closeTask;
            if (task == null)
            {
                ConnectionContext closing = context;
                var completion = new TaskCompletionSource<bool>();
                task = completion.Task;
                closeTask = task;
                _ = FinishCloseAsync(closing, completion);
                runtime.EnsureFaultCleanup();
            }

            await task;
        }

        private async Task FinishCloseAsync(ConnectionContext closing, TaskCompletionSource<bool> completion)
        {
            Exception failure = null;
            try
            {
                // Publish the shared task before BeginClose can synchronously reenter CloseAsync.
                closing.BeginClose();
                await closing.CloseTask;
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                if (closing.CloseTask.IsCompleted && ReferenceEquals(context, closing) && ReferenceEquals(closeTask, completion.Task))
                {
                    lastMetrics = closing.Metrics();
                    context = null;
                    closeTask = null;
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

        public ValueTask DisposeAsync()
        {
            NetworkRuntime.RequireMainThread();
            if (disposeTask == null)
            {
                var completion = new TaskCompletionSource<bool>();
                disposeTask = completion.Task;
                _ = FinishDisposeAsync(completion);
            }

            return new ValueTask(disposeTask);
        }

        private async Task FinishDisposeAsync(TaskCompletionSource<bool> completion)
        {
            Exception failure = null;
            try
            {
                await CloseAsync();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                disposed = true;
                pushes.Clear();
                Notify(stateObservers, new NetworkStateChange(NetworkClientState.Disposed, generation, null));
                stateObservers.Clear();
                faultObservers.Clear();
                runtime.Remove(this);
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

        internal bool PumpOne()
        {
            long started = Stopwatch.GetTimestamp();
            bool worked = true;
            try
            {
                worked = context?.PumpOne() ?? false;
                return worked;
            }
            finally
            {
                if (worked)
                {
                    runtime.RecordDispatch(started);
                }
            }
        }

        internal bool HostCleanupComplete => context == null || context.HostCleanupComplete;

        internal void FailHost(Exception error)
        {
            hostFailure = error;
            if (context != null)
            {
                context.FailHost(error);
            }
            else
            {
                Notify(stateObservers, new NetworkStateChange(NetworkClientState.Faulted, generation, error));
            }
        }

        private void RequireAlive()
        {
            NetworkRuntime.RequireMainThread();
            if (disposed || disposeTask != null)
            {
                throw new ObjectDisposedException(nameof(NetworkClient));
            }
        }

        private void RequireConnected()
        {
            RequireAlive();
            if (context == null || context.State != NetworkClientState.Connected)
            {
                throw new InvalidOperationException("Network client is not Connected.");
            }
        }
    }
}
