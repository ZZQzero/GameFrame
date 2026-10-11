using System;
using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.ProtocolToolTests
{
    public sealed class NetworkOptionsTests
    {
        [Test]
        public void OptionsSnapshotRejectsInvalidLimitsAndIsIndependent()
        {
            var options = new NetworkRuntimeOptions();
            var snapshot = options.Snapshot();
            options.MaxClients = 0;
            Assert.AreEqual(4, snapshot.MaxClients);
            Assert.Throws<ArgumentException>(() => options.Snapshot());
            var client = new NetworkClientOptions { BusinessBurstBytes = 2048, UnreliableBurstBytes = 256 };
            client.Snapshot().ValidateKcpNegotiation(2048, 256);
            Assert.Throws<ArgumentException>(() => client.ValidateKcpNegotiation(2049, 256));
        }
    }
}
