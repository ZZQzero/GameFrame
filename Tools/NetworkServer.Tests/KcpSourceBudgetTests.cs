using System.Net;
using GameFrame.Server.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed class KcpSourceBudgetTests
    {
        [Test]
        public void PortsCannotBypassPacketAndByteBudgets()
        {
            var packets = new KcpListenerHost.SourceBudget();
            for (int i = 0; i < 2048; i++)
            {
                Assert.That(packets.TryAccept(IPAddress.Loopback, 1, 1000), Is.True);
            }

            Assert.That(packets.TryAccept(IPAddress.Loopback, 1, 1999), Is.False);
            Assert.That(packets.TryAccept(IPAddress.Loopback, 1, 2000), Is.True);
            var bytes = new KcpListenerHost.SourceBudget();
            for (int i = 0; i < 1747; i++)
            {
                Assert.That(bytes.TryAccept(IPAddress.Loopback, 1200, 1000), Is.True);
            }

            Assert.That(bytes.TryAccept(IPAddress.Loopback, 1200, 1000), Is.False);
            Assert.That(bytes.TryAccept(IPAddress.IPv6Loopback, 1200, 1000), Is.True);
        }

        [Test]
        public void UnknownSourceFloodKeepsTableBoundedWithoutEvictingExistingSource()
        {
            var budget = new KcpListenerHost.SourceBudget();
            for (int i = 0; i < 1024; i++)
            {
                Assert.That(budget.TryAccept(new IPAddress(new byte[] { 10, 0, (byte)(i / 256), (byte)i }), 1200, 1000), Is.True);
            }

            Assert.That(budget.TryAccept(IPAddress.Loopback, 1200, 1000), Is.False);
            Assert.That(budget.Count, Is.EqualTo(1024));
            Assert.That(budget.TryAccept(new IPAddress(new byte[] { 10, 0, 0, 0 }), 1200, 1000), Is.True);
            Assert.That(budget.TryAccept(IPAddress.Loopback, 1200, 2000), Is.True);
            Assert.That(budget.Count, Is.EqualTo(1));
        }
    }
}
