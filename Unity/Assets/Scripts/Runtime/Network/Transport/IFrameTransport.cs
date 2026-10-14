#nullable disable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GameFrame.Network
{
    internal interface IFrameTransport : IDisposable
    {
        Task StoppedTask { get; }
        bool OwnsClock { get; }
        Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);
        Task SendAsync(OwnedFrame frame, CancellationToken cancellationToken, Func<bool> canSend = null);
        Task<OwnedFrame> ReceiveAsync(FrameMemoryBudget budget, int reliableLimit, int unreliableLimit,
            FrameDirection direction, bool handshaking, CancellationToken cancellationToken,
            Action<FrameHeader, NetDelivery> validateHeader, FrameMemoryBudget unreliableBudget = null);
        Task CloseOutputAsync(CancellationToken cancellationToken);
        void Abort();
    }
}
