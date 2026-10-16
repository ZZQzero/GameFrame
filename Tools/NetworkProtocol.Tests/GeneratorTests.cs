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
            var modules = typeof(Game.Protocol.Session.LoginRequest).Assembly.GetTypes()
                .Where(type => type.Namespace != null && type.Namespace.StartsWith("Game.Protocol.", StringComparison.Ordinal) &&
                    (type.IsEnum || Attribute.IsDefined(type, typeof(MemoryPackableAttribute))))
                .GroupBy(type => type.Namespace!)
                .Select(group => new ProtocolModuleDefinition(group.Key.Split('.').Last(), group.Key,
                    group.ToArray(), group.Key == "Game.Protocol.Session" ? Routes() : Array.Empty<ProtocolRouteDefinition>())).ToArray();
            var generator = new MemoryPackProtocolGenerator();
            var first = generator.Generate(modules, includeArtifacts: true);
            var second = generator.Generate(modules.Reverse().Select(module => module with
            {
                Types = module.Types.Reverse().ToArray(), Routes = module.Routes.Reverse().ToArray()
            }), includeArtifacts: true);
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
            var exception = Assert.Throws<InvalidOperationException>(() => new MemoryPackProtocolGenerator().Generate(new[] { Module(new[] { type, typeof(ValidResponse) }, new[] { Route(type) }) }));
            StringAssert.Contains(reason, exception!.Message);
        }

        [Test]
        public void DuplicateOpcodesAreRejected()
        {
            Assert.Throws<InvalidOperationException>(() => new MemoryPackProtocolGenerator().Generate(new[] { Module(new[] { typeof(ValidRequest), typeof(DuplicateRequest), typeof(ValidResponse) }, new[] { Route(typeof(ValidRequest)), Route(typeof(DuplicateRequest)) }) }));
        }

        [Test]
        public void ValidSchemaGeneratesManifestAndCode()
        {
            var outputs = new MemoryPackProtocolGenerator().Generate(new[] { Module(new[] { typeof(ValidRequest), typeof(ValidResponse) }, new[] { Route(typeof(ValidRequest)) }) });
            Assert.AreEqual(2, outputs.Count);
        }

        private static ProtocolModuleDefinition Module(Type[] types, ProtocolRouteDefinition[] routes)
        {
            return new ProtocolModuleDefinition("Test", "GameFrame.ProtocolToolTests", types, routes);
        }

        private static ProtocolRouteDefinition Route(Type request)
        {
            return new ProtocolRouteDefinition("test.Service." + request.Name, request.Name, "fixture", 3000, "Rpc", NetDelivery.Reliable,
                request, typeof(ValidResponse));
        }

        private static ProtocolRouteDefinition[] Routes()
        {
            return new[]
            {
                new ProtocolRouteDefinition("game.protocol.session.SessionService.Login", "Login", "fixture", 1000, "Rpc", NetDelivery.Reliable,
                    typeof(Game.Protocol.Session.LoginRequest), typeof(Game.Protocol.Session.LoginResponse))
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
