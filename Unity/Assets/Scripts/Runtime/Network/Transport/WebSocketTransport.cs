#nullable disable
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace GameFrame.Network
{
    internal sealed class WebSocketTransport : IFrameTransport
    {
        private readonly WebSocket socket;
        private readonly Func<Uri, CancellationToken, Task> connect;
        public Task StoppedTask => Task.CompletedTask;
        public bool OwnsClock => false;

        internal WebSocketTransport(WebSocket testSocket = null, Func<Uri, CancellationToken, Task> testConnect = null)
        {
            socket = testSocket ?? new ClientWebSocket();
            if (socket is ClientWebSocket client)
            {
                client.Options.KeepAliveInterval = TimeSpan.Zero;
            }

            connect = testConnect ?? ((ClientWebSocket)socket).ConnectAsync;
        }

        public Task ConnectAsync(Uri endpoint, CancellationToken token) => connect(endpoint, token);

        public async Task SendAsync(OwnedFrame frame, CancellationToken token, Func<bool> canSend = null)
        {
            if (canSend != null && !canSend())
            {
                return;
            }

            await WebSocketFrameTransport.SendAsync(socket, frame, token).ConfigureAwait(false);
            frame.AcceptedByTransport = true;
        }

        public Task<OwnedFrame> ReceiveAsync(FrameMemoryBudget budget, int reliableLimit, int unreliableLimit,
            FrameDirection direction, bool handshaking, CancellationToken token, Action<FrameHeader, NetDelivery> validateHeader, FrameMemoryBudget unreliableBudget = null)
        {
            return WebSocketFrameTransport.ReceiveAsync(socket, budget, reliableLimit, direction, handshaking, token,
                header => validateHeader?.Invoke(header, NetDelivery.Reliable));
        }

        public Task CloseOutputAsync(CancellationToken token) => socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
        public void Abort() => socket.Abort();
        public void Dispose() => socket.Dispose();
    }
}
