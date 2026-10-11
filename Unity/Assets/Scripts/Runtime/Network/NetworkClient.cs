using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using GameFrame.Network.Connection;
using GameFrame.Network.Transport;
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
            if (connect.Transport != NetworkTransportKind.WebSocket)
            {
                throw new PlatformNotSupportedException("KCP/DTLS transport has not been installed.");
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException("The browser WebSocket adapter has not been installed.");
#else
            if (generation == long.MaxValue)
            {
                throw new NetworkException("Connection generation exhausted.");
            }

            context = new ConnectionContext(++generation, options, catalog, connect, runtime.Memory, cancellationToken);
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
            RequireConnected();
            return await context.Request(route, request, timeout, cancellationToken);
        }

        public UniTask SendAsync<T>(NetSend<T> route, T message, CancellationToken cancellationToken = default) where T : class
        {
            RequireConnected();
            context.Send(route, message, cancellationToken);
            return UniTask.CompletedTask;
        }

        public bool TrySendUnreliable<T>(NetUnreliable<T> route, T message) where T : class
        {
            RequireConnected();
            catalog.RequireRegistered(route);
            throw new PlatformNotSupportedException("WebSocket connections do not carry unreliable routes.");
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

            Action<OwnedFrame> registered = frame => handler(route.Codec.Decode(frame.Body));
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
            RequireAlive();
            if (context == null)
            {
                return;
            }

            if (closeTask == null)
            {
                ConnectionContext closing = context;
                closing.BeginClose();
                closeTask = FinishCloseAsync(closing);
                runtime.EnsureFaultCleanup();
            }

            await closeTask;
        }

        private async Task FinishCloseAsync(ConnectionContext closing)
        {
            await closing.CloseTask;
            lastMetrics = closing.Metrics();
            context = null;
            closeTask = null;
        }

        public ValueTask DisposeAsync()
        {
            NetworkRuntime.RequireMainThread();
            if (disposed)
            {
                return default;
            }

            if (disposeTask == null)
            {
                disposeTask = FinishDisposeAsync();
            }

            return new ValueTask(disposeTask);
        }

        private async Task FinishDisposeAsync()
        {
            await CloseAsync();
            disposed = true;
            pushes.Clear();
            Notify(stateObservers, new NetworkStateChange(NetworkClientState.Disposed, generation, null));
            stateObservers.Clear();
            faultObservers.Clear();
            runtime.Remove(this);
        }

        internal bool PumpOne() => context?.PumpOne() ?? false;

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
