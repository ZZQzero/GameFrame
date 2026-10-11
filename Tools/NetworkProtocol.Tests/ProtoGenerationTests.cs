using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameFrame.ProtocolTools;
using NUnit.Framework;

namespace GameFrame.ProtocolToolTests
{
    public sealed class ProtoGenerationTests
    {
        [Test]
        public void ProtoGeneratesMemoryPackAndSharedDtoCanHaveTwoRoutes()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }", Service +
                "rpc Again(Request) returns (Response) { option (game.net.opcode) = 3001; " +
                "option (game.net.route_kind) = ROUTE_KIND_RPC; option (game.net.delivery) = DELIVERY_RELIABLE; " +
                "option (game.net.max_body) = 64; option (game.net.response_max_body) = 64; }");
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root);
            string messages = Text(outputs, "GameNetMessages.cs");
            StringAssert.Contains("[MemoryPackable(SerializeLayout.Explicit)]", messages);
            StringAssert.Contains("[MemoryPackOrder(0)]", messages);
            StringAssert.DoesNotContain("ProtoContract", messages);
            string routes = Text(outputs, "GameNetProtocol.cs");
            StringAssert.Contains("NetRpc<Request, Response> Call", routes);
            StringAssert.Contains("NetRpc<Request, Response> Again", routes);
            Assert.AreEqual(2, outputs.Count);
        }

        [Test]
        public void CommentsWhitespaceAndDeclarationOrderDoNotChangeOutputs()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; string name = 2 " +
                "[(game.net.max_utf16)=16, (game.net.max_utf8)=64]; }");
            var first = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            fixture.Write("message Request { /* comment */ string name = 2 [(game.net.max_utf8)=64, " +
                "(game.net.max_utf16)=16]; uint32 value = 1; }");
            var second = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            foreach (var item in first)
            {
                CollectionAssert.AreEqual(item.Value, second[item.Key], item.Key);
            }
        }

        [Test]
        public void HashIncludesConstraintsButGoldenBytesDoNotIncludeRouteMetadata()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            var first = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            fixture.Write("message Request { uint32 value = 1; }", Service.Replace("3000", "3001"));
            var second = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            Assert.AreNotEqual(Text(first, "protocol.sha256"), Text(second, "protocol.sha256"));
            Assert.AreEqual(Text(first, "golden-vectors.json"), Text(second, "golden-vectors.json"));
        }

        [TestCase("message Request { uint32 value = 2; }", "contiguous")]
        [TestCase("message Request { uint32 value = 1; uint32 value = 2; }", "already in use")]
        [TestCase("message Request { string value = 1; }", "max_utf16")]
        [TestCase("message Request { bytes value = 1; }", "max_count")]
        [TestCase("message Request { uint32 value = 1 [(game.net.nullable)=true]; }", "Nullable<T>")]
        [TestCase("message Request { uint32 value = 1 [(game.net.scalar)=\"int16\"]; }", "scalar")]
        [TestCase("message Request { Request value = 1; }", "Recursive DTO")]
        [TestCase("message Request { map<string, uint32> value = 1; }", "unsupported")]
        [TestCase("message Request { oneof value { uint32 a = 1; } }", "unsupported")]
        [TestCase("message Request { message Child {} Child value = 1; }", "unsupported")]
        [TestCase("message Request { Missing value = 1; }", "Missing")]
        [TestCase("message Request { uint32 value = 1 [(game.net.typo)=true]; }", "typo")]
        [TestCase("message Request { uint32 value = 1 invalid; }", "error")]
        [TestCase("message Request { repeated bytes value = 1 [(game.net.max_count)=2]; }", "jagged")]
        [TestCase("enum State { NONE = 0; } message Request { State value = 1; }", "enum")]
        [TestCase("message Request { bytes value = 1 [(game.net.max_count)=513]; }", "V1 limits")]
        public void InvalidProtoNeverSilentlyDropsFields(string messages, string reason)
        {
            using var fixture = new Fixture(messages);
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains(reason.ToLowerInvariant(), error!.Message.ToLowerInvariant());
        }

        [Test]
        public void CrossFileImportsAndNullableReferenceOptionsAreResolved()
        {
            using var fixture = new Fixture("message Request { Child child = 1 [(game.net.nullable)=true]; " +
                "repeated Child children = 2 [(game.net.max_count)=2, (game.net.element_nullable)=true]; }");
            File.WriteAllText(Path.Combine(fixture.Root, "child.proto"), Header + "message Child { uint32 value = 1; }");
            fixture.Write("import \"child.proto\"; message Request { Child child = 1 [(game.net.nullable)=true]; " +
                "repeated Child children = 2 [(game.net.max_count)=2, (game.net.element_nullable)=true]; }");
            string messages = Text(ProtoProtocolGenerator.Generate(fixture.Root), "GameNetMessages.cs");
            StringAssert.Contains("global::Game.Protocol.Child? Child", messages);
            StringAssert.Contains("global::Game.Protocol.Child?[] Children", messages);
        }

        [Test]
        public void MissingImportIsRejectedEvenWhenTheOtherFileWasDiscovered()
        {
            using var fixture = new Fixture("message Request { Child child = 1; }");
            File.WriteAllText(Path.Combine(fixture.Root, "child.proto"), Header + "message Child { uint32 value = 1; }");
            Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
        }

        [Test]
        public void DuplicateOpcodeAcrossServicesIsRejected()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            File.AppendAllText(Path.Combine(fixture.Root, "test.proto"), " service OtherService { " +
                Service.Replace("Call", "OtherCall") + " }");
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains("Duplicate opcode", error!.Message);
        }

        [Test]
        public void FullGeneratedCodeIsCompiledBeforeWriting()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }", Service.Replace("Call", "GameNetRoutes"));
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains("compilation failed", error!.Message);
        }

        [Test]
        public void DefaultGenerationWritesOnlyCodeAndMatchesArtifactMode()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            var code = ProtoProtocolGenerator.Generate(fixture.Root);
            var artifacts = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            Assert.AreEqual(2, code.Count);
            Assert.AreEqual(5, artifacts.Count);
            foreach (var output in code)
            {
                Assert.IsTrue(output.Key.EndsWith(".cs", StringComparison.Ordinal));
                CollectionAssert.AreEqual(output.Value, artifacts[output.Key], output.Key);
            }

            Assert.AreEqual(3, artifacts.Keys.Count(path => path.StartsWith("Protocol/Generated/", StringComparison.Ordinal)));
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, code, false));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "Protocol/Generated")));
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, code, true));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CheckDetectsDriftAndUnexpectedFilesWithoutWriting(bool includeArtifacts)
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts);
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, outputs, false));
            string path = Path.Combine(fixture.Root, outputs.Keys.First());
            File.WriteAllText(path, "altered");
            Assert.AreEqual(1, Program.WriteOutputs(fixture.Root, outputs, true));
            Assert.AreEqual("altered", File.ReadAllText(path));
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, outputs, false));
            string extra = Path.Combine(fixture.Root, "Unity/Assets/Scripts/Hotfix/Network/Generated/Obsolete.cs");
            File.WriteAllText(extra, "obsolete");
            Assert.AreEqual(1, Program.WriteOutputs(fixture.Root, outputs, true));
            Assert.AreEqual("obsolete", File.ReadAllText(extra));
        }

        private static string Text(IReadOnlyDictionary<string, byte[]> outputs, string name)
        {
            return Encoding.UTF8.GetString(outputs.Single(item => item.Key.EndsWith("/" + name, StringComparison.Ordinal)).Value);
        }

        private const string Header = "syntax = \"proto3\"; package test.protocol; option csharp_namespace = \"Game.Protocol\"; " +
            "import \"Common/net_options.proto\"; import \"google/protobuf/empty.proto\"; ";
        private const string Service = "rpc Call(Request) returns (Response) { option (game.net.opcode) = 3000; " +
            "option (game.net.route_kind) = ROUTE_KIND_RPC; option (game.net.delivery) = DELIVERY_RELIABLE; " +
            "option (game.net.max_body) = 64; option (game.net.response_max_body) = 64; }";

        private sealed class Fixture : IDisposable
        {
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "gameframe-proto-test-" + Guid.NewGuid().ToString("N"));

            public Fixture(string messages, string service = Service)
            {
                Directory.CreateDirectory(Path.Combine(Root, "Common"));
                var repo = new DirectoryInfo(AppContext.BaseDirectory);
                while (repo != null && !File.Exists(Path.Combine(repo.FullName, "Protocol/Common/net_options.proto")))
                {
                    repo = repo.Parent;
                }

                File.Copy(Path.Combine(repo!.FullName, "Protocol/Common/net_options.proto"), Path.Combine(Root, "Common/net_options.proto"));
                Write(messages, service);
            }

            public void Write(string messages, string service = Service)
            {
                File.WriteAllText(Path.Combine(Root, "test.proto"), Header + messages +
                    " message Response {} service TestService { " + service + " }");
            }

            public void Dispose()
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
