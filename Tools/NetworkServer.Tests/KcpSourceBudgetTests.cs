using Game.Protocol.Diagnostics;
using System.Net;
using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed class KcpSourceBudgetTests
    {
        [TestCase(nameof(KcpServerOptions.MaxConnections), 0)]
        [TestCase(nameof(KcpServerOptions.MaxConnections), 65)]
        [TestCase(nameof(KcpServerOptions.MaxHandshakes), 0)]
        [TestCase(nameof(KcpServerOptions.MaxHandshakes), 17)]
        [TestCase(nameof(KcpServerOptions.MaxGrants), 0)]
        [TestCase(nameof(KcpServerOptions.MaxGrants), 257)]
        [TestCase(nameof(KcpServerOptions.NativeBytes), 65535)]
        [TestCase(nameof(KcpServerOptions.NativeBytes), 256 * 1024 * 1024 + 1)]
        public void InvalidNativeCapacityFailsBeforeListenerStarts(string property, int value)
        {
            var options = new KcpServerOptions();
            typeof(KcpServerOptions).GetProperty(property)!.SetValue(options, value);
            var application = ServerCloseTests.Application((_, _, _) => Task.FromResult(new Game.Protocol.Diagnostics.EchoResponse()));
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new KcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0), application, options))!;
            Assert.That(error.ParamName, Is.EqualTo(property));
        }

        [TestCase(1, 1, 1, 65536)]
        [TestCase(64, 16, 256, 256 * 1024 * 1024)]
        public void NativeCapacityBoundariesAreAccepted(int connections, int handshakes, int grants, int bytes)
        {
            var options = new KcpServerOptions
            {
                MaxConnections = connections,
                MaxHandshakes = handshakes,
                MaxGrants = grants,
                NativeBytes = bytes,
                ConnectionNativeBytes = 65536
            };
            Assert.DoesNotThrow(() => options.Snapshot());
        }

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
