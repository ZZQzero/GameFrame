using System.Reflection;
using System.Text;
using GameFrame.Network;
using Google.Protobuf.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using ProtoBuf;

namespace GameFrame.ProtocolTools
{
    /// <summary>Proto is authoring input only; compiled output uses MemoryPack's own generator.</summary>
    public static class ProtoProtocolGenerator
    {
        private const string OutputDirectory = "Unity/Assets/Scripts/Hotfix/Network/Generated/";

        public static IReadOnlyDictionary<string, byte[]> Generate(string protoRoot, bool includeArtifacts = false)
        {
            ProtoSchema schema = Load(protoRoot);
            var messages = schema.Modules.Keys.ToDictionary(module => module,
                module => GenerateMessages(schema, module), StringComparer.Ordinal);
            string scratch = Path.Combine(Path.GetTempPath(), "gameframe-protocol-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                Assembly assembly = CompileMessages(messages.Values.Select(source => MessageHeader + source), scratch);
                Type Resolve(string name) => assembly.GetType(schema.Messages[name].CsharpName, true)!;
                var modules = schema.Modules.Keys.Select(module => new ProtocolModuleDefinition(module, schema.Modules[module].Namespace,
                    schema.Messages.Values.Where(model => model.Module == module).Select(model => Resolve(model.Name))
                        .Concat(schema.Enums.Values.Where(model => model.Module == module)
                            .Select(model => assembly.GetType(model.CsharpName, true)!)).ToArray(),
                    schema.Routes.Where(route => route.Module == module).Select(route => new ProtocolRouteDefinition(route.Name,
                        route.MemberName, route.Source, route.Opcode, route.Kind, route.Delivery, Resolve(route.Request),
                        route.Response == null ? null : Resolve(route.Response))).ToArray()));
                var generator = new MemoryPackProtocolGenerator();
                var outputs = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var item in generator.Generate(modules, includeArtifacts))
                {
                    outputs.Add(item.Key, item.Value);
                }

                foreach (var module in messages)
                {
                    string path = OutputDirectory + module.Key + "Messages.g.cs";
                    outputs[path] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(outputs[path]) + "\n" + module.Value);
                }

                CompileMessages(outputs.Where(item => item.Key.EndsWith(".cs", StringComparison.Ordinal))
                    .Select(item => Encoding.UTF8.GetString(item.Value)), scratch);
                generator.ValidateSerializationInfo(scratch);
                return outputs;
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }

        internal static ProtoSchema Load(string protoRoot)
        {
            if (!Directory.Exists(protoRoot))
            {
                throw new DirectoryNotFoundException($"Protocol root does not exist: {protoRoot}");
            }

            var descriptors = new FileDescriptorSet();
            descriptors.AddImportPath(protoRoot);
            foreach (string path in Directory.EnumerateFiles(protoRoot, "*.proto", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(protoRoot, path).Replace('\\', '/');
                if (relative.StartsWith("Generated/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                descriptors.Add(relative, true);
            }

            descriptors.Process();
            var errors = descriptors.GetErrors().Where(error => error.IsError ||
                !error.Message.StartsWith("import not used: ", StringComparison.Ordinal)).ToArray();
            if (errors.Length != 0)
            {
                throw new InvalidOperationException("Proto parsing failed:\n" + string.Join("\n", errors.Select(error => error.ToString())));
            }

            var result = new ProtoSchema();
            var businessFiles = descriptors.Files.Where(file => !file.Name.StartsWith("google/", StringComparison.Ordinal) &&
                file.Name != "Common/net_options.proto").ToArray();
            foreach (FileDescriptorProto file in businessFiles)
            {
                if (file.Syntax != "proto3" || string.IsNullOrEmpty(file.Package))
                {
                    throw new InvalidOperationException($"{file.Name}: explicit proto3 syntax and package are required.");
                }

                string space = file.Options?.CsharpNamespace ?? string.Empty;
                RequireNamespace(space);
                string module = file.Name.Split('/')[0];
                if (!file.Name.Contains('/') || module.Equals("Generated", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{file.Name}: business Proto must belong to a module directory.");
                }

                RequireIdentifier(module);
                if (space != "Game.Protocol." + module)
                {
                    throw new InvalidOperationException($"{file.Name}: module {module} requires csharp_namespace Game.Protocol.{module}.");
                }

                if (result.Modules.Values.Any(value => value.Name != module && value.Package == file.Package))
                {
                    throw new InvalidOperationException($"{file.Name}: package {file.Package} is already owned by another module.");
                }

                if (result.Modules.TryGetValue(module, out ProtoModule? existingModule))
                {
                    if (existingModule.Name != module || existingModule.Package != file.Package || existingModule.Namespace != space)
                    {
                        throw new InvalidOperationException($"{file.Name}: conflicting module name, package or namespace for {module}.");
                    }
                }
                else
                {
                    result.Modules.Add(module, new ProtoModule(module, file.Package, space));
                }

                if (file.Extensions.Count != 0)
                {
                    throw new InvalidOperationException($"{file.Name}: business extensions are unsupported.");
                }

                foreach (EnumDescriptorProto definition in file.EnumTypes)
                {
                    RequireIdentifier(definition.Name);
                    if (definition.Options?.AllowAlias == true || definition.Values.Count == 0 || definition.Values[0].Number != 0)
                    {
                        throw new InvalidOperationException($"{definition.Name}: enum requires a zero first value and forbids aliases.");
                    }

                    string fullName = "." + file.Package + "." + definition.Name;
                    var model = new ProtoEnum(space + "." + definition.Name, module);
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    var numbers = new HashSet<int>();
                    foreach (EnumValueDescriptorProto value in definition.Values)
                    {
                        string name = PascalCase(value.Name);
                        RequireIdentifier(name);
                        if (!names.Add(name) || !numbers.Add(value.Number) || name == definition.Name)
                        {
                            throw new InvalidOperationException($"{definition.Name}: enum names and values must be unique.");
                        }

                        model.Values.Add(new ProtoEnumValue(name, value.Number));
                    }

                    if (!result.Enums.TryAdd(fullName, model))
                    {
                        throw new InvalidOperationException($"Duplicate enum: {fullName}.");
                    }
                }

                foreach (DescriptorProto message in file.MessageTypes)
                {
                    RequireIdentifier(message.Name);
                    if (message.NestedTypes.Count != 0 || message.EnumTypes.Count != 0 || message.OneofDecls.Count != 0 ||
                        message.Extensions.Count != 0 || message.ExtensionRanges.Count != 0)
                    {
                        throw new InvalidOperationException($"{message.Name}: nested messages, map, oneof and extensions are unsupported.");
                    }

                    string fullName = "." + file.Package + "." + message.Name;
                    var model = new ProtoMessage(fullName, space + "." + message.Name, module);
                    if (!result.Messages.TryAdd(fullName, model) ||
                        result.Messages.Values.Count(value => value.CsharpName == model.CsharpName) != 1)
                    {
                        throw new InvalidOperationException($"Duplicate message/C# type: {fullName}.");
                    }

                    int number = 1;
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (FieldDescriptorProto field in message.Fields.OrderBy(field => field.Number))
                    {
                        string name = PascalCase(field.Name);
                        RequireIdentifier(name);
                        if (field.Number != number++ || !names.Add(name) || name == message.Name)
                        {
                            throw new InvalidOperationException($"{message.Name}: field numbers must be contiguous from 1 and C# names unique.");
                        }

                        model.Fields.Add(ReadField(field, name));
                    }
                }
            }

            string[] csharpNames = result.Messages.Values.Select(model => model.CsharpName)
                .Concat(result.Enums.Values.Select(model => model.CsharpName)).ToArray();
            if (csharpNames.Distinct(StringComparer.Ordinal).Count() != csharpNames.Length)
            {
                throw new InvalidOperationException("Duplicate generated C# type name.");
            }

            foreach (ProtoMessage message in result.Messages.Values)
            {
                foreach (ProtoField field in message.Fields)
                {
                    if (field.MessageType != null && !result.Messages.ContainsKey(field.MessageType))
                    {
                        throw new InvalidOperationException($"{message.Name}.{field.Name}: unknown business message {field.MessageType}.");
                    }

                    if (field.EnumType != null && !result.Enums.ContainsKey(field.EnumType))
                    {
                        throw new InvalidOperationException($"{message.Name}.{field.Name}: unknown business enum {field.EnumType}.");
                    }
                }
            }

            foreach (FileDescriptorProto file in businessFiles)
            {
                foreach (ServiceDescriptorProto service in file.Services)
                {
                    foreach (MethodDescriptorProto method in service.Methods)
                    {
                        RequireIdentifier(method.Name);
                        if (method.ClientStreaming || method.ServerStreaming)
                        {
                            throw new InvalidOperationException($"{method.Name}: streaming RPC is unsupported.");
                        }

                        string name = file.Package + "." + service.Name + "." + method.Name;
                        var options = ReadRoute(method.Options, file.Name + ": " + name);
                        uint opcode = options.Opcode;
                        uint kind = options.Kind;
                        uint delivery = options.Delivery;
                        if (opcode < 1000 || (delivery != 1 && delivery != 2))
                        {
                            throw new InvalidOperationException($"{file.Name}: {name}: route requires opcode >= 1000 and an explicit valid delivery.");
                        }

                        string kindName = kind switch
                        {
                            1 => "Rpc",
                            2 => "Push",
                            3 => "Notify",
                            _ => throw new InvalidOperationException($"{file.Name}: {name}: route requires an explicit valid kind, got {kind}.")
                        };
                        if (!result.Messages.ContainsKey(method.InputType) ||
                            (kind == 1 ? !result.Messages.ContainsKey(method.OutputType) : method.OutputType != ".google.protobuf.Empty"))
                        {
                            throw new InvalidOperationException($"{method.Name}: invalid input/response type; Push/Notify returns google.protobuf.Empty.");
                        }

                        result.Routes.Add(new ProtoRoute(file.Name.Split('/')[0], name, method.Name, file.Name,
                            opcode, kindName, (NetDelivery)delivery, method.InputType,
                            kind == 1 ? method.OutputType : null));
                    }
                }
            }

            ValidateModuleDependencies(result);
            return result;
        }

        private static void ValidateModuleDependencies(ProtoSchema schema)
        {
            var dependencies = schema.Modules.Keys.ToDictionary(module => module,
                _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            void Add(string owner, string target)
            {
                if (owner != target)
                {
                    dependencies[owner].Add(target);
                }
            }

            foreach (ProtoMessage message in schema.Messages.Values)
            {
                foreach (ProtoField field in message.Fields)
                {
                    if (field.MessageType != null)
                    {
                        Add(message.Module, schema.Messages[field.MessageType].Module);
                    }

                    if (field.EnumType != null)
                    {
                        Add(message.Module, schema.Enums[field.EnumType].Module);
                    }
                }
            }

            foreach (ProtoRoute route in schema.Routes)
            {
                Add(route.Module, schema.Messages[route.Request].Module);
                if (route.Response != null)
                {
                    Add(route.Module, schema.Messages[route.Response].Module);
                }
            }

            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string module)
            {
                if (visited.Contains(module))
                {
                    return;
                }

                if (!visiting.Add(module))
                {
                    throw new InvalidOperationException($"Protocol module dependency cycle at {module}.");
                }

                foreach (string target in dependencies[module].OrderBy(value => value, StringComparer.Ordinal))
                {
                    Visit(target);
                }

                visiting.Remove(module);
                visited.Add(module);
            }

            foreach (string module in schema.Modules.Keys)
            {
                Visit(module);
            }
        }

        private static ProtoField ReadField(FieldDescriptorProto field, string name)
        {
            bool repeated = field.label == FieldDescriptorProto.Label.LabelRepeated;
            bool bytes = field.type == FieldDescriptorProto.Type.TypeBytes;
            bool message = field.type == FieldDescriptorProto.Type.TypeMessage;
            string? messageType = message ? field.TypeName : null;
            string scalar = Get<string>(field.Options, 51106) ?? string.Empty;
            string core = field.type switch
            {
                FieldDescriptorProto.Type.TypeBool => "bool",
                FieldDescriptorProto.Type.TypeInt32 or FieldDescriptorProto.Type.TypeSint32 or FieldDescriptorProto.Type.TypeSfixed32 => "int",
                FieldDescriptorProto.Type.TypeUint32 or FieldDescriptorProto.Type.TypeFixed32 => "uint",
                FieldDescriptorProto.Type.TypeInt64 or FieldDescriptorProto.Type.TypeSint64 or FieldDescriptorProto.Type.TypeSfixed64 => "long",
                FieldDescriptorProto.Type.TypeUint64 or FieldDescriptorProto.Type.TypeFixed64 => "ulong",
                FieldDescriptorProto.Type.TypeFloat => "float",
                FieldDescriptorProto.Type.TypeDouble => "double",
                FieldDescriptorProto.Type.TypeString => "string",
                FieldDescriptorProto.Type.TypeBytes => "byte",
                FieldDescriptorProto.Type.TypeMessage => string.Empty,
                FieldDescriptorProto.Type.TypeEnum => "enum",
                _ => throw new InvalidOperationException($"{field.Name}: unsupported field type {field.type}.")
            };
            if (scalar.Length != 0)
            {
                core = (core, scalar) switch
                {
                    ("int", "int8") => "sbyte",
                    ("int", "int16") => "short",
                    ("uint", "uint8") => "byte",
                    ("uint", "uint16") => "ushort",
                    _ => throw new InvalidOperationException($"{field.Name}: invalid narrow scalar {scalar}.")
                };
            }

            bool nullable = Get<bool>(field.Options, 51104) || field.Proto3Optional;
            bool elementNullable = Get<bool>(field.Options, 51105);
            bool array = repeated || bytes;
            if ((nullable && !array && !message && core != "string") ||
                (elementNullable && (!repeated || (!message && core != "string"))) || (bytes && repeated))
            {
                throw new InvalidOperationException($"{field.Name}: Nullable<T>/jagged arrays are forbidden; nullable applies to references only.");
            }

            int? count = array ? checked((int)Required<uint>(field.Options, 51103, "max_count")) : null;
            int? utf16 = core == "string" ? checked((int)Required<uint>(field.Options, 51101, "max_utf16")) : null;
            int? utf8 = core == "string" ? checked((int)Required<uint>(field.Options, 51102, "max_utf8")) : null;
            if (count > NetContractLimits.MaxArrayCount || utf16 > NetContractLimits.MaxStringUtf16Units ||
                utf8 > NetContractLimits.MaxStringUtf8Bytes)
            {
                throw new InvalidOperationException($"{field.Name}: declared field length exceeds V1 limits.");
            }

            if ((!array && Has<uint>(field.Options, 51103)) ||
                (core != "string" && (Has<uint>(field.Options, 51101) || Has<uint>(field.Options, 51102))))
            {
                throw new InvalidOperationException($"{field.Name}: length options do not apply to this field type.");
            }

            return new ProtoField(name, field.Number - 1, core, messageType,
                field.type == FieldDescriptorProto.Type.TypeEnum ? field.TypeName : null,
                array, nullable, elementNullable, count, utf16, utf8);
        }

        private const string MessageHeader = "// Generated by DotNet/NetworkProtocol. Do not edit.\n" +
            "#nullable enable\nusing System;\nusing GameFrame.Network;\nusing MemoryPack;\n";

        private static string GenerateMessages(ProtoSchema schema, string module)
        {
            var code = new StringBuilder();
            void Line(string value = "") => code.Append(value).Append('\n');
            IEnumerable<string> spaces = schema.Messages.Values.Where(model => model.Module == module).Select(model => model.CsharpName)
                .Concat(schema.Enums.Values.Where(model => model.Module == module).Select(model => model.CsharpName))
                .Select(name => name[..name.LastIndexOf('.')]).Distinct().OrderBy(name => name, StringComparer.Ordinal);
            foreach (string space in spaces)
            {
                Line();
                Line("namespace " + space);
                Line("{");
                foreach (ProtoEnum definition in schema.Enums.Values.Where(model => model.Module == module && model.CsharpName[..model.CsharpName.LastIndexOf('.')] == space)
                    .OrderBy(model => model.CsharpName, StringComparer.Ordinal))
                {
                    Line($"    public enum {definition.CsharpName[(definition.CsharpName.LastIndexOf('.') + 1)..]} : int");
                    Line("    {");
                    foreach (ProtoEnumValue value in definition.Values.OrderBy(value => value.Number))
                    {
                        Line($"        {value.Name} = {value.Number},");
                    }

                    Line("    }");
                    Line();
                }

                foreach (ProtoMessage message in schema.Messages.Values.Where(model => model.Module == module && model.CsharpName[..model.CsharpName.LastIndexOf('.')] == space)
                    .OrderBy(value => value.CsharpName, StringComparer.Ordinal))
                {
                    string name = message.CsharpName[(message.CsharpName.LastIndexOf('.') + 1)..];
                    Line("    [MemoryPackable(SerializeLayout.Explicit)]");
                    Line($"    public sealed partial class {name}");
                    Line("    {");
                    foreach (ProtoField field in message.Fields)
                    {
                        if (field.MaxCount.HasValue)
                        {
                            Line($"        [NetArrayLimit({field.MaxCount})]");
                        }

                        if (field.MaxUtf16.HasValue)
                        {
                            Line($"        [NetStringLimit({field.MaxUtf16}, {field.MaxUtf8})]");
                        }

                        Line($"        [MemoryPackOrder({field.Order})]");
                        string core = field.MessageType == null ? field.Core : "global::" + schema.Messages[field.MessageType].CsharpName;
                        if (field.EnumType != null)
                        {
                            core = "global::" + schema.Enums[field.EnumType].CsharpName;
                        }
                        string type = core + (field.Array ? (field.ElementNullable ? "?" : "") + "[]" : "") +
                            (field.Nullable ? "?" : "");
                        string initializer = field.Nullable ? "" : field.Array ? $" = Array.Empty<{core}{(field.ElementNullable ? "?" : "")}>();"
                            : core == "string" ? " = string.Empty;" : field.MessageType != null ? $" = new {core}();" : "";
                        Line($"        public {type} {field.Name} {{ get; set; }}{initializer}");
                        Line();
                    }

                    Line("    }");
                    Line();
                }

                Line("}");
            }

            return code.ToString();
        }

        internal static Assembly CompileMessages(IEnumerable<string> sources, string serializationInfoDirectory)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: new[]
            {
                "NET", "NET10_0", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER",
                "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER"
            });
            var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(NetProtocolCatalog).Assembly.Location).Append(typeof(MemoryPack.MemoryPackSerializer).Assembly.Location)
                .Distinct(StringComparer.Ordinal);
            var compilation = CSharpCompilation.Create("GameFrame.Protocol.Validation." + Guid.NewGuid().ToString("N"),
                sources.Select(source => CSharpSyntaxTree.ParseText(source, parseOptions)),
                paths.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                    nullableContextOptions: NullableContextOptions.Enable));
            var generator = new MemoryPack.Generator.MemoryPackGenerator();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() },
                parseOptions: parseOptions, optionsProvider: new GeneratorOptions(serializationInfoDirectory));
            driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation generated, out var diagnostics);
            using var output = new MemoryStream();
            var result = generated.Emit(output);
            if (!result.Success || diagnostics.Any(value => value.Severity == DiagnosticSeverity.Error))
            {
                throw new InvalidOperationException("Generated MemoryPack DTO compilation failed:\n" +
                    string.Join("\n", diagnostics.Concat(result.Diagnostics).Where(value => value.Severity == DiagnosticSeverity.Error)));
            }

            return Assembly.Load(output.ToArray());
        }

        private static T Required<T>(IExtensible? options, int tag, string name)
        {
            if (options == null || !Extensible.TryGetValue(options, tag, out T value))
            {
                throw new InvalidOperationException($"Missing required option game.net.{name}.");
            }

            return value;
        }

        private static T Get<T>(IExtensible? options, int tag) => options == null ? default! : Extensible.GetValue<T>(options, tag);
        private static bool Has<T>(IExtensible? options, int tag) => options != null && Extensible.TryGetValue(options, tag, out T _);
        private static string PascalCase(string name) => string.Concat(name.Split('_').Select(part => part.Length == 0 ? "" : char.ToUpperInvariant(part[0]) + part[1..]));

        private static void RequireIdentifier(string name)
        {
            if (!SyntaxFacts.IsValidIdentifier(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            {
                throw new InvalidOperationException($"Invalid generated C# identifier: {name}.");
            }
        }

        private static void RequireNamespace(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException("csharp_namespace must be explicitly declared.");
            }

            foreach (string part in name.Split('.'))
            {
                RequireIdentifier(part);
            }
        }

        internal sealed class ProtoSchema
        {
            public SortedDictionary<string, ProtoModule> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);
            public SortedDictionary<string, ProtoMessage> Messages { get; } = new(StringComparer.Ordinal);
            public SortedDictionary<string, ProtoEnum> Enums { get; } = new(StringComparer.Ordinal);
            public List<ProtoRoute> Routes { get; } = new();
        }

        internal sealed record ProtoModule(string Name, string Package, string Namespace);

        internal sealed record ProtoMessage(string Name, string CsharpName, string Module)
        {
            public List<ProtoField> Fields { get; } = new();
        }

        internal sealed record ProtoEnum(string CsharpName, string Module)
        {
            public List<ProtoEnumValue> Values { get; } = new();
        }

        internal sealed record ProtoEnumValue(string Name, int Number);
        internal sealed record ProtoField(string Name, int Order, string Core, string? MessageType, string? EnumType, bool Array,
            bool Nullable, bool ElementNullable, int? MaxCount, int? MaxUtf16, int? MaxUtf8);
        internal sealed record ProtoRoute(string Module, string Name, string MemberName, string Source, uint Opcode,
            string Kind, NetDelivery Delivery, string Request, string? Response);

        private static (uint Opcode, uint Kind, uint Delivery) ReadRoute(MethodOptions? options, string source)
        {
            // The descriptor parser resolves the aggregate to a protobuf extension payload.
            // Read it with protobuf-net's primitives, without another IDL parser or runtime model.
            byte[][] values = options == null ? Array.Empty<byte[]>() : Extensible.GetValues<byte[]>(options, 51006).ToArray();
            if (values.Length != 1)
            {
                throw new InvalidOperationException($"{source}: exactly one game.net.route option is required.");
            }

            uint opcode = 0;
            uint kind = 0;
            uint delivery = 0;
            int seen = 0;
            var reader = ProtoReader.State.Create(values[0].AsMemory(), null, null);
            try
            {
                int field;
                while ((field = reader.ReadFieldHeader()) != 0)
                {
                    if (field < 1 || field > 3 || reader.WireType != WireType.Varint || (seen & (1 << field)) != 0)
                    {
                        throw new InvalidOperationException($"{source}: unknown, duplicate or invalid route field {field}.");
                    }

                    seen |= 1 << field;
                    uint value = reader.ReadUInt32();
                    switch (field)
                    {
                        case 1:
                            opcode = value;
                            break;
                        case 2:
                            kind = value;
                            break;
                        case 3:
                            delivery = value;
                            break;
                    }
                }
            }
            finally
            {
                reader.Dispose();
            }

            if (seen != 14)
            {
                throw new InvalidOperationException($"{source}: route requires explicit opcode, kind and delivery.");
            }

            return (opcode, kind, delivery);
        }

        private sealed class GeneratorOptions : AnalyzerConfigOptionsProvider
        {
            private readonly AnalyzerConfigOptions options;
            public GeneratorOptions(string directory) => options = new SerializationOptions(directory);
            public override AnalyzerConfigOptions GlobalOptions => options;
            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => options;
            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => options;

            private sealed class SerializationOptions : AnalyzerConfigOptions
            {
                private readonly string directory;
                public SerializationOptions(string directory) => this.directory = directory;
                public override bool TryGetValue(string key, out string value)
                {
                    value = directory;
                    return key == "build_property.MemoryPackGenerator_SerializationInfoOutputDirectory";
                }
            }
        }
    }
}
