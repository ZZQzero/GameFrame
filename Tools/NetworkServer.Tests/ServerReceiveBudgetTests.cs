using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Server.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class ServerReceiveBudgetTests
    {
        [TestCase(33280)]
        [TestCase(33823)]
        public void ReceiveBudgetBelowExpansionPeakIsRejected(int capacity)
        {
            Assert.Throws<ArgumentException>(() => new WssServerOptions { ReceiveQueueBytes = capacity }.Snapshot());
        }

        [Test]
        public async Task MinimumReceiveBudgetAcceptsMaximumFrameAndReleasesBothLeases()
        {
            WssServerOptions options = new WssServerOptions { ReceiveQueueBytes = 33824 }.Snapshot();
            var memory = new FrameMemoryBudget(options.ReceiveQueueBytes);
            using var socket = new ConnectionRaceTests.ControlledSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] bytes = new byte[32768];
            FrameCodec.WriteHeader(bytes, new FrameHeader(FrameKind.Notify, 1000, 0, 32736));
            bytes.AsSpan(32).Fill(0xa5);
            socket.Enqueue(bytes);
            using (OwnedFrame frame = (await WebSocketFrameTransport.ReceiveAsync(socket, memory, 32768,
                FrameDirection.ClientToServer, false, timeout.Token))!)
            {
                Assert.That(frame.Length, Is.EqualTo(32768));
                Assert.That(frame.Bytes.AsSpan(0, frame.Length).SequenceEqual(bytes), Is.True);
                Assert.That(memory.OwnedBytes, Is.EqualTo(33280));
                Assert.That(memory.PeakReservedBytes, Is.EqualTo(33824));
            }

            Assert.That(memory.OwnedBytes, Is.Zero);
        }
    }
}
