#nullable enable
using System;
using System.Linq;
using GameFrame.Network;
using GameFrame.ProtocolTools;
using MemoryPack;
using NUnit.Framework;

namespace GameFrame.ProtocolToolTests
{
    public sealed class GeneratorTests
    {
        [Test]
        public void GenerationIsDeterministicAcrossTypeInputOrder()
        {
            Type[] types = typeof(Game.Protocol.LoginRequest).Assembly.GetTypes()
                .Where(type => type.Namespace == "Game.Protocol" &&
                    (type.IsEnum || Attribute.IsDefined(type, typeof(MemoryPackableAttribute)))).ToArray();
            var generator = new MemoryPackProtocolGenerator();
            var first = generator.Generate(types, Routes(), includeArtifacts: true);
            var second = generator.Generate(types.Reverse(), Routes().Reverse(), includeArtifacts: true);
            foreach (var item in first)
            {
                CollectionAssert.AreEqual(item.Value, second[item.Key]);
                Assert.IsFalse(item.Value.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
            }
        }

        [TestCase(typeof(NullableRequest), "Nullable<T>")]
        [TestCase(typeof(UnboundedRequest), "NetStringLimit")]
        [TestCase(typeof(RecursiveRequest), "Recursive DTO")]
        public void InvalidSchemasFailWithExplicitReason(Type type, string reason)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => new MemoryPackProtocolGenerator().Generate(new[] { type }, new[] { Route(type) }));
            StringAssert.Contains(reason, exception!.Message);
        }

        [Test]
        public void DuplicateOpcodesAreRejected()
        {
            Assert.Throws<InvalidOperationException>(() => new MemoryPackProtocolGenerator().Generate(new[] { typeof(ValidRequest), typeof(DuplicateRequest) }, new[] { Route(typeof(ValidRequest)), Route(typeof(DuplicateRequest)) }));
        }

        [Test]
        public void ValidSchemaGeneratesManifestAndCode()
        {
            var outputs = new MemoryPackProtocolGenerator().Generate(new[] { typeof(ValidRequest) }, new[] { Route(typeof(ValidRequest)) });
            Assert.AreEqual(1, outputs.Count);
        }

        private static ProtocolRouteDefinition Route(Type request)
        {
            return new ProtocolRouteDefinition(request.Name, 3000, "Rpc", NetDelivery.Reliable,
                request, typeof(ValidResponse), 64, 64);
        }

        private static ProtocolRouteDefinition[] Routes()
        {
            return new[]
            {
                new ProtocolRouteDefinition("Login", 1000, "Rpc", NetDelivery.Reliable,
                    typeof(Game.Protocol.LoginRequest), typeof(Game.Protocol.LoginResponse), 2048, 256)
            };
        }

    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class ValidRequest
    {
        [MemoryPackOrder(0)]
        public uint Value { get; set; }
    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class ValidResponse
    {
    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class DuplicateRequest
    {
    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class NullableRequest
    {
        [MemoryPackOrder(0)]
        public int? Value { get; set; }
    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class UnboundedRequest
    {
        [MemoryPackOrder(0)]
        public string Value { get; set; } = string.Empty;
    }

    [MemoryPackable(SerializeLayout.Explicit)]
    public sealed partial class RecursiveRequest
    {
        [MemoryPackOrder(0)]
        public RecursiveRequest? Value { get; set; }
    }
}
