#nullable enable
using System;
using System.Buffers;
using System.Reflection;
using System.Text.Json;
using GameFrame.Network;
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
                "rpc Again(Request) returns (Response) { option (game.net.route) = { opcode: 3001 " +
                "kind: ROUTE_KIND_RPC delivery: DELIVERY_RELIABLE " +
                "}; }");
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root);
            string messages = Text(outputs, "TestMessages.g.cs");
            StringAssert.Contains("[MemoryPackable(SerializeLayout.Explicit)]", messages);
            StringAssert.Contains("[MemoryPackOrder(0)]", messages);
            StringAssert.DoesNotContain("ProtoContract", messages);
            string routes = Text(outputs, "TestMessages.g.cs");
            StringAssert.Contains("NetRpc<global::Game.Protocol.Test.Request, global::Game.Protocol.Test.Response> Call", routes);
            StringAssert.Contains("NetRpc<global::Game.Protocol.Test.Request, global::Game.Protocol.Test.Response> Again", routes);
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
        [TestCase("enum State { None = 0; Ready = 1; Alias = 1; option allow_alias = true; } message Request { State value = 1; }", "aliases")]
        [TestCase("enum State { None = 0; snake_case = 1; SnakeCase = 2; } message Request { State value = 1; }", "unique")]
        [TestCase("enum State { None = 0; } message Request { State value = 1 [(game.net.scalar)=\"int16\"]; }", "scalar")]
        [TestCase("enum State { None = 0; } message Request { State value = 1 [(game.net.nullable)=true]; }", "Nullable<T>")]
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
            File.WriteAllText(Path.Combine(fixture.Root, "Test/child.proto"), Header + "message Child { uint32 value = 1; }");
            fixture.Write("import \"Test/child.proto\"; message Request { Child child = 1 [(game.net.nullable)=true]; " +
                "repeated Child children = 2 [(game.net.max_count)=2, (game.net.element_nullable)=true]; }");
            string messages = Text(ProtoProtocolGenerator.Generate(fixture.Root), "TestMessages.g.cs");
            StringAssert.Contains("global::Game.Protocol.Test.Child? Child", messages);
            StringAssert.Contains("global::Game.Protocol.Test.Child?[] Children", messages);
        }

        [Test]
        public void EnumScalarAndArrayUseFixedInt32BytesAndKnownValueValidators()
        {
            using var fixture = new Fixture("enum State { None = 0; Ready = 7; Error = -9; } " +
                "message Request { State state = 1; repeated State states = 2 [(game.net.max_count)=2]; }");
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            StringAssert.Contains("public enum State : int", Text(outputs, "TestMessages.g.cs"));
            string codecs = Text(outputs, "TestMessages.g.cs");
            StringAssert.Contains("TestCodecs.ReadEnumState(ref reader)", codecs);
            StringAssert.Contains("TestCodecs.CheckEnumState(element)", codecs);
            StringAssert.Contains("Unknown enum value", codecs);
            string vectors = Text(outputs, "golden-vectors.json");
            StringAssert.Contains("02f7ffffff02000000f7fffffff7ffffff", vectors);
            StringAssert.Contains("0207000000020000000700000007000000", vectors);
        }

        [Test]
        public void EnumDefinitionChangesHashEvenWhenEncodedValuesAreUnchanged()
        {
            const string proto = "enum State { None = 0; Ready = 7; } message Request { State value = 1; }";
            using var fixture = new Fixture(proto);
            var first = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            fixture.Write(proto.Replace("Ready", "Available"));
            var second = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            Assert.AreNotEqual(Text(first, "protocol.sha256"), Text(second, "protocol.sha256"));
            Assert.AreEqual(Text(first, "golden-vectors.json"), Text(second, "golden-vectors.json"));
        }

        [Test]
        public void MissingImportIsRejectedEvenWhenTheOtherFileWasDiscovered()
        {
            using var fixture = new Fixture("message Request { Child child = 1; }");
            File.WriteAllText(Path.Combine(fixture.Root, "Test/child.proto"), Header + "message Child { uint32 value = 1; }");
            Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
        }

        [Test]
        public void DuplicateOpcodeAcrossServicesIsRejected()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            File.AppendAllText(Path.Combine(fixture.Root, "Test/test.proto"), " service OtherService { " +
                Service.Replace("Call", "OtherCall") + " }");
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains("Duplicate opcode", error!.Message);
        }

        [Test]
        public void FullGeneratedCodeIsCompiledBeforeWriting()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }", Service.Replace("Call", "TestRoutes"));
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

        [TestCase(false, "Obsolete.cs")]
        [TestCase(true, "Obsolete.cs")]
        [TestCase(false, "Old/Obsolete.cs")]
        [TestCase(true, "Old/Obsolete.cs")]
        public void CheckDetectsDriftAndUnexpectedFilesWithoutWriting(bool includeArtifacts, string stalePath)
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts);
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, outputs, false));
            string path = Path.Combine(fixture.Root, outputs.Keys.First());
            File.WriteAllText(path, "altered");
            Assert.AreEqual(1, Program.WriteOutputs(fixture.Root, outputs, true));
            Assert.AreEqual("altered", File.ReadAllText(path));
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, outputs, false));
            string extra = Path.Combine(fixture.Root, "Unity/Assets/Scripts/Hotfix/Network/Generated", stalePath);
            Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
            File.WriteAllText(extra, "obsolete");
            Assert.AreEqual(1, Program.WriteOutputs(fixture.Root, outputs, true));
            Assert.AreEqual("obsolete", File.ReadAllText(extra));
        }

        [Test]
        public void DataOnlyModuleOwnsItsTypesAndValidatorsOnce()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            fixture.WriteFile("Common/shared.proto", Header.Replace("test.protocol", "common.protocol").Replace("Game.Protocol.Test", "Game.Protocol.Common") +
                "enum SharedCode { None = 0; Ready = 1; } message Child { SharedCode code = 1; }");
            fixture.Write("import \"Common/shared.proto\"; message Request { common.protocol.Child child = 1; }");
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            string common = Text(outputs, "CommonMessages.g.cs");
            string business = Text(outputs, "TestMessages.g.cs");
            StringAssert.Contains("public sealed partial class Child", common);
            StringAssert.Contains("internal static void ReadObjectChild", common);
            StringAssert.Contains("internal static void CheckEnumSharedCode", common);
            StringAssert.DoesNotContain("public sealed partial class Child", business);
            StringAssert.DoesNotContain("internal static void ReadObjectChild", business);
            StringAssert.Contains("ReadObjectChild(ref reader, false, depth + 1)", business);
            StringAssert.Contains("CheckObjectChild(value.@Child, false, depth + 1, ref nodes)", business);
            StringAssert.DoesNotContain("CatalogHolder", common);
            StringAssert.DoesNotContain("CatalogHolder", business);
            StringAssert.Contains("global::Game.Protocol.Test.TestRoutes.Call", Text(outputs, "GameNetProtocol.cs"));
        }

        [Test]
        public void AddingChangingAndRemovingAnotherModulePreservesUnrelatedFileAndTimestamp()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            var baseline = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, baseline, false));
            string businessPath = Path.Combine(fixture.Root, baseline.Keys.Single(path => path.EndsWith("TestMessages.g.cs", StringComparison.Ordinal)));
            var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(businessPath, timestamp);
            string chat = Header.Replace("test.protocol", "chat.protocol").Replace("Game.Protocol.Test", "Game.Protocol.AChat") +
                "message ChatRequest { uint32 value = 1; } message ChatResponse {} service ChatService { " +
                Service.Replace("Call", "SendChat").Replace("3000", "3001")
                    .Replace("(Request)", "(ChatRequest)").Replace("(Response)", "(ChatResponse)") + " }";
            fixture.WriteFile("AChat/chat.proto", chat);
            var added = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            CollectionAssert.AreEqual(Text(baseline, "TestMessages.g.cs"), Text(added, "TestMessages.g.cs"));
            Assert.AreNotEqual(Text(baseline, "protocol.sha256"), Text(added, "protocol.sha256"));
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, added, false));
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(businessPath));

            fixture.WriteFile("AChat/chat.proto", chat.Replace("uint32 value", "uint64 value"));
            var changed = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            Assert.AreEqual(Text(added, "TestMessages.g.cs"), Text(changed, "TestMessages.g.cs"));
            Assert.AreNotEqual(Text(added, "AChatMessages.g.cs"), Text(changed, "AChatMessages.g.cs"));
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, changed, false));
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(businessPath));

            File.Delete(Path.Combine(fixture.Root, "AChat/chat.proto"));
            var removed = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            foreach (var item in baseline)
            {
                CollectionAssert.AreEqual(item.Value, removed[item.Key], item.Key);
            }

            string obsolete = Path.Combine(fixture.Root, "Unity/Assets/Scripts/Hotfix/Network/Generated/AChatMessages.g.cs");
            File.WriteAllText(obsolete + ".meta", "test-owned-meta");
            Assert.AreEqual(1, Program.WriteOutputs(fixture.Root, removed, true));
            Assert.IsTrue(File.Exists(obsolete));
            Assert.AreEqual("test-owned-meta", File.ReadAllText(obsolete + ".meta"));
            File.Delete(obsolete);
            File.Delete(obsolete + ".meta");
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, removed, false));
            Assert.AreEqual(0, Program.WriteOutputs(fixture.Root, removed, true));
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(businessPath));
        }

        [TestCase("root.proto", "module directory")]
        [TestCase("Bad-Module/extra.proto", "identifier")]
        public void UnownedOrInvalidModuleIsRejected(string path, string reason)
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            fixture.WriteFile(path, Header + "message Extra {}");
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains(reason, error!.Message);
        }

        [TestCase("package")]
        [TestCase("namespace")]
        public void ConflictingModuleDeclarationsAreRejected(string field)
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            string header = field == "package" ? Header.Replace("test.protocol", "other.protocol")
                : Header.Replace("Game.Protocol", "Other.Protocol");
            fixture.WriteFile("Test/extra.proto", header + "message Extra {}");
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains(field == "package" ? "conflicting module" : "requires csharp_namespace", error!.Message);
        }

        [Test]
        public void ModuleNamesCannotCollideOnCaseInsensitiveFileSystems()
        {
            // Exercise the normalized model: this host cannot create two directories differing only in case.
            var modules = new[]
            {
                new ProtocolModuleDefinition("Test", "Game.Protocol.Test", Array.Empty<Type>(), Array.Empty<ProtocolRouteDefinition>()),
                new ProtocolModuleDefinition("test", "Game.Protocol.test", Array.Empty<Type>(), Array.Empty<ProtocolRouteDefinition>())
            };
            var error = Assert.Throws<InvalidOperationException>(() => new MemoryPackProtocolGenerator().Generate(modules));
            StringAssert.Contains("Duplicate module name", error!.Message);
        }

        [Test]
        public void ModuleCyclesAreRejectedWithoutRecursiveDtoTypes()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            fixture.WriteFile("Common/leaf.proto", Header.Replace("test.protocol", "common.protocol").Replace("Game.Protocol.Test", "Game.Protocol.Common") + "message Leaf {}");
            fixture.WriteFile("Other/other.proto", Header.Replace("test.protocol", "other.protocol").Replace("Game.Protocol.Test", "Game.Protocol.Other") +
                "import \"Common/leaf.proto\"; message Other { common.protocol.Leaf leaf = 1; }");
            fixture.WriteFile("Common/parent.proto", Header.Replace("test.protocol", "common.protocol").Replace("Game.Protocol.Test", "Game.Protocol.Common") +
                "import \"Other/other.proto\"; message Parent { other.protocol.Other other = 1; }");
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains("module dependency cycle", error!.Message);
        }

        [Test]
        public void DevelopmentArtifactsAreExcludedFromProtoDiscovery()
        {
            using var fixture = new Fixture("message Request { uint32 value = 1; }");
            var first = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            fixture.WriteFile("Generated/invalid.proto", "This is not a protocol.");
            var second = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            foreach (var item in first)
            {
                CollectionAssert.AreEqual(item.Value, second[item.Key], item.Key);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SameNamesAcrossModulesRoundTripAndCatalogUsesTheSameInstances(bool routeFirst)
        {
            using var fixture = new Fixture("enum State { None = 0; Ready = 7; } message EnumState {} message Request { State state = 1; EnumState child = 2; }");
            fixture.WriteFile("Other/other.proto", Header.Replace("test.protocol", "other.protocol")
                .Replace("Game.Protocol.Test", "Game.Protocol.Other") +
                "enum State { None = 0; Done = 9; } message Request { State state = 1; } message Response {} " +
                "service OtherService { " + Service.Replace("3000", "3001") + " }");
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            Assembly assembly = Compile(outputs, fixture.Root);
            Type catalogType = assembly.GetType("Game.Protocol.GameNetProtocol", true)!;
            NetProtocolCatalog? catalog = routeFirst ? null : (NetProtocolCatalog)catalogType.GetProperty("Catalog")!.GetValue(null)!;
            foreach (var item in new[] { (Module: "Test", Value: 7), (Module: "Other", Value: 9) })
            {
                string space = "Game.Protocol." + item.Module;
                Type requestType = assembly.GetType(space + ".Request", true)!;
                Type responseType = assembly.GetType(space + ".Response", true)!;
                var route = (NetRoute)assembly.GetType(space + "." + item.Module + "Routes", true)!.GetField("Call")!.GetValue(null)!;
                catalog ??= (NetProtocolCatalog)catalogType.GetProperty("Catalog")!.GetValue(null)!;
                catalog.RequireRegistered(route);
                object request = Activator.CreateInstance(requestType)!;
                PropertyInfo state = requestType.GetProperty("State")!;
                state.SetValue(request, Enum.ToObject(state.PropertyType, item.Value));
                object decoded = typeof(ProtoGenerationTests).GetMethod(nameof(RoundTrip), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(requestType, responseType).Invoke(null, new[] { route, request })!;
                Assert.AreEqual(item.Value, Convert.ToInt32(state.GetValue(decoded)));
            }

            Assert.AreEqual(2, catalog!.RouteCount);
            using JsonDocument manifest = JsonDocument.Parse(Text(outputs, "protocol.manifest.json"));
            CollectionAssert.AreEqual(new[] { "test.protocol.TestService.Call", "other.protocol.OtherService.Call" },
                manifest.RootElement.GetProperty("routes").EnumerateArray().Select(route => route.GetProperty("name").GetString()));
            StringAssert.DoesNotContain("GameNetRoutes", Text(outputs, "GameNetProtocol.cs"));
        }

        [Test]
        public void SameRouteMemberWithinOneModuleIsRejectedWithBothServiceIdentities()
        {
            using var fixture = new Fixture("message Request {}", Service);
            File.AppendAllText(Path.Combine(fixture.Root, "Test/test.proto"),
                " service SecondService { " + Service.Replace("3000", "3001") + " }");
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains("duplicate route member Call", error!.Message);
            StringAssert.Contains("Test/test.proto", error.Message);
            StringAssert.Contains("module Test", error.Message);
            StringAssert.Contains("test.protocol.SecondService.Call", error.Message);
            StringAssert.Contains("test.protocol.TestService.Call", error.Message);
        }

        [Test]
        public void NamespaceMustMatchTheModuleDirectory()
        {
            using var fixture = new Fixture("message Request {}");
            string path = Path.Combine(fixture.Root, "Test/test.proto");
            File.WriteAllText(path, File.ReadAllText(path).Replace("Game.Protocol.Test", "Game.Protocol.Wrong"));
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains("Test/test.proto", error!.Message);
            StringAssert.Contains("requires csharp_namespace Game.Protocol.Test", error.Message);
        }

        [Test]
        public void CrossModuleObjectAndWireValidationShareTheirDepthAndNodeBudgets()
        {
            using var fixture = new Fixture("message Request {}");
            fixture.WriteFile("Common/shared.proto", Header.Replace("test.protocol", "common.protocol")
                .Replace("Game.Protocol.Test", "Game.Protocol.Common") +
                "enum Code { None = 0; Ready = 7; } message Child { Code code = 1; }");
            fixture.Write("import \"Common/shared.proto\"; message Request { " +
                "repeated common.protocol.Child children = 1 [(game.net.max_count)=512]; }",
                Service);
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root);
            Assembly assembly = Compile(outputs, fixture.Root);
            Type requestType = assembly.GetType("Game.Protocol.Test.Request", true)!;
            Type childType = assembly.GetType("Game.Protocol.Common.Child", true)!;
            var route = (NetRoute)assembly.GetType("Game.Protocol.Test.TestRoutes", true)!.GetField("Call")!.GetValue(null)!;
            // Exercise the generated internal methods with an already consumed budget.
            // Resetting either budget at the module boundary would incorrectly accept this child.
            MethodInfo check = assembly.GetType("Game.Protocol.Test.TestCodecs", true)!.GetMethod("CheckObjectRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
            object request = Activator.CreateInstance(requestType)!;
            Array children = Array.CreateInstance(childType, 1);
            children.SetValue(Activator.CreateInstance(childType), 0);
            requestType.GetProperty("Children")!.SetValue(request, children);
            foreach (var budget in new[] { (Depth: 1, Nodes: NetContractLimits.MaxDtoNodes - 1), (Depth: NetContractLimits.MaxObjectDepth, Nodes: 0) })
            {
                TargetInvocationException error = Assert.Throws<TargetInvocationException>(() =>
                    check.Invoke(null, new object[] { request, false, budget.Depth, budget.Nodes }))!;
                Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
            }

            // The wire validator must carry one reader through five common-type arrays.
            fixture.Write("import \"Common/shared.proto\"; message Request { " +
                string.Join(" ", Enumerable.Range(1, 5).Select(i =>
                    $"repeated common.protocol.Child children{i} = {i} [(game.net.max_count)=512];")) + " }",
                Service);
            assembly = Compile(ProtoProtocolGenerator.Generate(fixture.Root), fixture.Root);
            requestType = assembly.GetType("Game.Protocol.Test.Request", true)!;
            childType = assembly.GetType("Game.Protocol.Common.Child", true)!;
            request = Activator.CreateInstance(requestType)!;
            foreach (PropertyInfo property in requestType.GetProperties())
            {
                children = Array.CreateInstance(childType, 410);
                for (int i = 0; i < children.Length; i++)
                {
                    children.SetValue(Activator.CreateInstance(childType), i);
                }
                property.SetValue(request, children);
            }

            route = (NetRoute)assembly.GetType("Game.Protocol.Test.TestRoutes", true)!.GetField("Call")!.GetValue(null)!;
            MethodInfo validate = typeof(ProtoGenerationTests).GetMethod(nameof(RejectNodeOverflow), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(requestType, assembly.GetType("Game.Protocol.Test.Response", true)!);
            validate.Invoke(null, new[] { route, request });
        }

        [TestCase(0, 0)]
        [TestCase(0, 32)]
        [TestCase(32, 0)]
        [TestCase(1, 1)]
        [TestCase(1, 2)]
        [TestCase(1, 3)]
        [TestCase(2, 4)]
        [TestCase(8, 8)]
        [TestCase(16, 32)]
        [TestCase(256, 5)]
        [TestCase(256, 1024)]
        public void StringBudgetsCoverActualUtf8BytesAndContiguousWriterRequests(int units, int bytes)
        {
            using var fixture = new Fixture($"message Request {{ string text = 1 [(game.net.max_utf16)={units}, (game.net.max_utf8)={bytes}]; }}");
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            Assembly assembly = Compile(outputs, fixture.Root);
            Type requestType = assembly.GetType("Game.Protocol.Test.Request", true)!;
            object request = Activator.CreateInstance(requestType)!;
            object route = assembly.GetType("Game.Protocol.Test.TestRoutes", true)!.GetField("Call")!.GetValue(null)!;
            MethodInfo check = typeof(ProtoGenerationTests).GetMethod(nameof(CheckBoundedRpc), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(requestType, assembly.GetType("Game.Protocol.Test.Response", true)!);
            foreach (string text in ProtocolSamples.StringBoundaries(units, bytes).Distinct())
            {
                requestType.GetProperty("Text")!.SetValue(request, text);
                check.Invoke(null, new[] { route, request, Activator.CreateInstance(assembly.GetType("Game.Protocol.Test.Response", true)!) });
            }

            using JsonDocument manifest = JsonDocument.Parse(Text(outputs, "protocol.manifest.json"));
            Assert.AreEqual(((NetRoute)route).MaxBodyBytes, manifest.RootElement.GetProperty("routes")[0].GetProperty("maxBodyBytes").GetInt32());
            // Eight ASCII units encode in 17 bytes, but WriteUtf8 requests 36 including the object header.
            if (units == 8 && bytes == 8)
            {
                Assert.AreEqual(36, ((NetRoute)route).MaxBodyBytes);
            }
        }

        [Test]
        public void NestedArraysNullableValuesAndScalarWidthsFitTheGeneratedBudget()
        {
            using var fixture = new Fixture("message Request {}");
            fixture.WriteFile("Common/leaf.proto", Header.Replace("test.protocol", "common.protocol").Replace("Game.Protocol.Test", "Game.Protocol.Common") +
                "enum Code { None = 0; Ready = 7; } message Leaf { string text = 1 [(game.net.max_utf16)=8, (game.net.max_utf8)=8]; Code code = 2; }");
            fixture.Write("import \"Common/leaf.proto\"; message Request { " +
                "repeated common.protocol.Leaf children = 1 [(game.net.max_count)=2, (game.net.element_nullable)=true]; " +
                "common.protocol.Leaf child = 2 [(game.net.nullable)=true]; " +
                "repeated string texts = 3 [(game.net.max_count)=3, (game.net.max_utf16)=8, (game.net.max_utf8)=16, (game.net.element_nullable)=true]; " +
                "bytes data = 4 [(game.net.max_count)=512, (game.net.nullable)=true]; " +
                "repeated uint64 empty = 5 [(game.net.max_count)=0]; " +
                "int32 small = 6 [(game.net.scalar)=\"int8\"]; uint32 short_value = 7 [(game.net.scalar)=\"uint16\"]; " +
                "float real = 8; double wide = 9; bool flag = 10; repeated int32 values = 11 [(game.net.max_count)=4]; }");
            Assembly assembly = Compile(ProtoProtocolGenerator.Generate(fixture.Root), fixture.Root);
            Type requestType = assembly.GetType("Game.Protocol.Test.Request", true)!;
            Type responseType = assembly.GetType("Game.Protocol.Test.Response", true)!;
            object route = assembly.GetType("Game.Protocol.Test.TestRoutes", true)!.GetField("Call")!.GetValue(null)!;
            MethodInfo check = typeof(ProtoGenerationTests).GetMethod(nameof(CheckBoundedRpc), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(requestType, responseType);
            foreach (string mode in new[] { "default", "normal", "maximum" })
            {
                object request = ProtocolSamples.Create(requestType, mode);
                check.Invoke(null, new[] { route, request, Activator.CreateInstance(responseType) });
                requestType.GetProperty("Child")!.SetValue(request, null);
                requestType.GetProperty("Data")!.SetValue(request, null);
                requestType.GetProperty("Children")!.SetValue(request, Array.CreateInstance(assembly.GetType("Game.Protocol.Common.Leaf", true)!, 2));
                requestType.GetProperty("Texts")!.SetValue(request, new string?[] { null, "", "😀a" });
                check.Invoke(null, new[] { route, request, Activator.CreateInstance(responseType) });
            }
        }

        [TestCase("DELIVERY_UNRELIABLE", 139, 1, 1118)]
        [TestCase("DELIVERY_RELIABLE", 503, 7, 32736)]
        public void CalculatedBudgetsEnforceBothChannelEdges(string delivery, int lastCount, int scalars, int expected)
        {
            string prefix = delivery == "DELIVERY_RELIABLE" ? string.Join(" ", Enumerable.Range(1, 7).Select(i =>
                $"repeated uint64 block{i} = {i} [(game.net.max_count)=512];")) : "";
            int number = delivery == "DELIVERY_RELIABLE" ? 8 : 1;
            string messages = "message Request { " + prefix +
                $" repeated uint64 last = {number++} [(game.net.max_count)={lastCount}];";
            foreach (var scalar in scalars == 7 ? new[] { (Type: "uint32", Narrow: ""), (Type: "uint32", Narrow: "uint16"), (Type: "uint32", Narrow: "uint8") }
                : new[] { (Type: "uint32", Narrow: "uint8") })
            {
                messages += $" {scalar.Type} value{number} = {number++} " +
                    (scalar.Narrow.Length == 0 ? ";" : $"[(game.net.scalar)=\"{scalar.Narrow}\"];");
            }
            messages += " }";
            string service = $"rpc Call(Request) returns (google.protobuf.Empty) {{ option (game.net.route) = {{ opcode: 3000 kind: ROUTE_KIND_CLIENT_SEND delivery: {delivery} }}; }}";
            using var fixture = new Fixture(messages, service);
            var outputs = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            using JsonDocument manifest = JsonDocument.Parse(Text(outputs, "protocol.manifest.json"));
            Assert.AreEqual(expected, manifest.RootElement.GetProperty("routes")[0].GetProperty("maxBodyBytes").GetInt32());
            fixture.Write(messages[..^1] + $" uint32 overflow = {number} [(game.net.scalar)=\"uint8\"]; }}", service);
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains("calculated body budget " + (expected + 1), error!.Message);
            StringAssert.Contains("Test/test.proto", error.Message);
            StringAssert.Contains("test.protocol.TestService.Call", error.Message);
        }

        [Test]
        public void NestedSizeOverflowIsReportedBeforeProducingOutputs()
        {
            string messages = "message Leaf { bytes data = 1 [(game.net.max_count)=512]; }";
            for (int i = 1; i <= 7; i++)
            {
                messages += $" message Level{i} {{ repeated {(i == 1 ? "Leaf" : "Level" + (i - 1))} children = 1 [(game.net.max_count)=512]; }}";
            }
            using var fixture = new Fixture(messages + " message Request { Level7 value = 1; }");
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains("overflows Int64", error!.Message);
        }

        [TestCase("", "route")]
        [TestCase("option (game.net.opcode)=3000;", "opcode")]
        [TestCase("option (game.net.max_body)=64;", "max_body")]
        [TestCase("option (game.net.response_max_body)=64;", "response_max_body")]
        [TestCase("option (game.net.route)={opcode:3000 kind:ROUTE_KIND_RPC};", "explicit")]
        [TestCase("option (game.net.route)={opcode:3000 kind:ROUTE_KIND_RPC delivery:DELIVERY_UNSPECIFIED};", "delivery")]
        [TestCase("option (game.net.route)={opcode:3000 kind:ROUTE_KIND_UNSPECIFIED delivery:DELIVERY_RELIABLE};", "kind")]
        [TestCase("option (game.net.route)={opcode:999 kind:ROUTE_KIND_RPC delivery:DELIVERY_RELIABLE};", "opcode")]
        [TestCase("option (game.net.route)={opcode:3000 opcode:3001 kind:ROUTE_KIND_RPC delivery:DELIVERY_RELIABLE};", "duplicate")]
        [TestCase("option (game.net.route)={opcode:3000 kind:ROUTE_KIND_RPC delivery:DELIVERY_RELIABLE typo:1};", "typo")]
        [TestCase("option (game.net.route)={opcode:3000 kind:ROUTE_KIND_RPC delivery:DELIVERY_RELIABLE}; option (game.net.route)={opcode:3001 kind:ROUTE_KIND_RPC delivery:DELIVERY_RELIABLE};", "route")]
        [TestCase("option (game.net.route)={opcode:3000 kind:ROUTE_KIND_RPC delivery:DELIVERY_UNRELIABLE};", "reliable")]
        [TestCase("option (game.net.route)={opcode:3000 kind:ROUTE_KIND_SERVER_PUSH delivery:DELIVERY_RELIABLE};", "Empty")]
        public void InvalidAggregateRoutesAndRetiredOptionsFailExplicitly(string options, string reason)
        {
            using var fixture = new Fixture("message Request {}", "rpc Call(Request) returns (Response) { " + options + " }");
            var error = Assert.Throws<InvalidOperationException>(() => ProtoProtocolGenerator.Generate(fixture.Root));
            StringAssert.Contains(reason.ToLowerInvariant(), error!.Message.ToLowerInvariant());
        }

        [Test]
        public void AggregateMemberOrderAndCommentsDoNotChangeGeneratedOutputs()
        {
            using var fixture = new Fixture("message Request {}");
            var first = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            fixture.Write("message Request {}", Service.Replace("opcode: 3000 kind: ROUTE_KIND_RPC delivery: DELIVERY_RELIABLE",
                "delivery: DELIVERY_RELIABLE /* order is irrelevant */ kind: ROUTE_KIND_RPC opcode: 3000"));
            var second = ProtoProtocolGenerator.Generate(fixture.Root, includeArtifacts: true);
            foreach (var item in first)
            {
                CollectionAssert.AreEqual(item.Value, second[item.Key], item.Key);
            }
        }

        private static void CheckBoundedRpc<TRequest, TResponse>(object descriptor, object request, object response)
            where TRequest : class where TResponse : class
        {
            var route = (NetRpc<TRequest, TResponse>)descriptor;
            CheckBounded(route.RequestCodec, (TRequest)request, route.MaxBodyBytes);
            CheckBounded(route.ResponseCodec, (TResponse)response, route.ResponseMaxBodyBytes);
        }

        private static void CheckBounded<T>(NetMessageCodec<T> codec, T value, int limit) where T : class
        {
            var memory = new FrameMemoryBudget(32 + limit + FrameMemoryBudget.FrameRecordReservation);
            using (OwnedFrame frame = memory.Allocate(32 + limit))
            {
                var writer = new BoundedFrameWriter(frame);
                codec.Encode(writer, value);
                writer.Complete(FrameKind.Request, 3000, 1);
                Assert.That(writer.BodyLength, Is.LessThanOrEqualTo(limit));
                CollectionAssert.AreEqual(ProtocolSamples.Serialize<T>(value), frame.Body.ToArray());
                Assert.That(codec.Decode(frame.Body), Is.Not.Null);
            }
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        private static Assembly Compile(IReadOnlyDictionary<string, byte[]> outputs, string directory)
        {
            return ProtoProtocolGenerator.CompileMessages(outputs.Where(item => item.Key.EndsWith(".cs", StringComparison.Ordinal))
                .Select(item => Encoding.UTF8.GetString(item.Value)), directory);
        }

        private static object RoundTrip<TRequest, TResponse>(object descriptor, object request)
            where TRequest : class where TResponse : class
        {
            var route = (NetRpc<TRequest, TResponse>)descriptor;
            var writer = new ArrayBufferWriter<byte>();
            route.RequestCodec.Encode(writer, (TRequest)request);
            return route.RequestCodec.Decode(writer.WrittenSpan);
        }

        private static void RejectNodeOverflow<TRequest, TResponse>(object descriptor, object request)
            where TRequest : class where TResponse : class
        {
            var route = (NetRpc<TRequest, TResponse>)descriptor;
            var writer = new ArrayBufferWriter<byte>();
            Assert.Throws<ArgumentException>(() => route.RequestCodec.Encode(writer, (TRequest)request));
            byte[] bytes = ProtocolSamples.Serialize<TRequest>(request);
            Assert.Throws<NetworkProtocolException>(() => route.RequestCodec.Decode(bytes));
        }

        private static string Text(IReadOnlyDictionary<string, byte[]> outputs, string name)
        {
            return Encoding.UTF8.GetString(outputs.Single(item => item.Key.EndsWith("/" + name, StringComparison.Ordinal)).Value);
        }

        private const string Header = "syntax = \"proto3\"; package test.protocol; option csharp_namespace = \"Game.Protocol.Test\"; " +
            "import \"Common/net_options.proto\"; import \"google/protobuf/empty.proto\"; ";
        private const string Service = "rpc Call(Request) returns (Response) { option (game.net.route) = { opcode: 3000 " +
            "kind: ROUTE_KIND_RPC delivery: DELIVERY_RELIABLE " +
            "}; }";

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
                Directory.CreateDirectory(Path.Combine(Root, "Test"));
                Write(messages, service);
            }

            public void Write(string messages, string service = Service)
            {
                File.WriteAllText(Path.Combine(Root, "Test/test.proto"), Header + messages +
                    " message Response {} service TestService { " + service + " }");
            }

            public void WriteFile(string path, string contents)
            {
                string destination = Path.Combine(Root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, contents);
            }

            public void Dispose()
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
