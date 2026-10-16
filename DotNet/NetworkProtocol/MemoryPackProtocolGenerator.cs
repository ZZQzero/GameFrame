using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameFrame.Network;
using MemoryPack;
using Microsoft.CodeAnalysis.CSharp;

namespace GameFrame.ProtocolTools
{
    public sealed class MemoryPackProtocolGenerator
    {
        private const string GeneratedDirectory = "Unity/Assets/Scripts/Hotfix/Network/Generated/";
        private const string ArtifactDirectory = "Protocol/Generated/";
        private readonly NullabilityInfoContext nullability = new NullabilityInfoContext();
        private readonly SortedDictionary<string, Schema> schemas = new SortedDictionary<string, Schema>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, Type> enums = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        private readonly Dictionary<Type, Schema> byType = new Dictionary<Type, Schema>();
        private readonly HashSet<Type> visiting = new HashSet<Type>();
        private readonly List<Route> routes = new List<Route>();
        private readonly Dictionary<Type, ProtocolModuleDefinition> typeModules = new Dictionary<Type, ProtocolModuleDefinition>();

        public IReadOnlyDictionary<string, byte[]> Generate(IEnumerable<ProtocolModuleDefinition> input, bool includeArtifacts = false)
        {
            schemas.Clear();
            enums.Clear();
            byType.Clear();
            visiting.Clear();
            routes.Clear();
            ProtocolModuleDefinition[] modules = input.OrderBy(module => module.Name, StringComparer.Ordinal).ToArray();
            typeModules.Clear();
            var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProtocolModuleDefinition module in modules)
            {
                if (!moduleNames.Add(module.Name))
                {
                    throw new InvalidOperationException($"Duplicate module name: {module.Name}.");
                }

                RequireIdentifier(module.Name);
                foreach (string part in module.Namespace.Split('.'))
                {
                    RequireIdentifier(part);
                }

                foreach (Type type in module.Types)
                {
                    if (!typeModules.TryAdd(type, module))
                    {
                        throw new InvalidOperationException($"Duplicate type ownership: {type.FullName}.");
                    }
                }

                var memberNames = new Dictionary<string, ProtocolRouteDefinition>(StringComparer.Ordinal);
                foreach (ProtocolRouteDefinition route in module.Routes)
                {
                    RequireIdentifier(route.MemberName);
                    if (!memberNames.TryAdd(route.MemberName, route))
                    {
                        ProtocolRouteDefinition previous = memberNames[route.MemberName];
                        throw new InvalidOperationException($"{route.Source}: module {module.Name}, {route.Name}: duplicate route member {route.MemberName}; " +
                            $"conflicts with {previous.Name} ({previous.Source}).");
                    }
                }
            }

            foreach (Type type in typeModules.Keys.OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (type.IsEnum)
                {
                    AnalyzeEnum(type);
                }
                else
                {
                    Analyze(type);
                }
            }

            foreach (ProtocolModuleDefinition module in modules)
            {
                foreach (ProtocolRouteDefinition definition in module.Routes)
                {
                    if (definition.Kind == "Rpc")
                    {
                        if (definition.Delivery != NetDelivery.Reliable || definition.Response == null)
                        {
                            throw new InvalidOperationException("RPC requires a reliable channel and a response DTO.");
                        }
                    }
                    else if ((definition.Kind != "Push" && definition.Kind != "Notify") ||
                        definition.Response != null)
                    {
                        throw new InvalidOperationException("Push/Notify requires no response DTO.");
                    }

                    Schema request = Analyze(definition.Request);
                    Schema? response = definition.Response == null ? null : Analyze(definition.Response);
                    var route = new Route(module, definition.Name, definition.MemberName, definition.Source, definition.Opcode, definition.Kind, definition.Delivery,
                        request, response, RouteBudget(module, definition, request),
                        response == null ? 0 : RouteBudget(module, definition, response));
                    if (routes.Any(existing => existing.Opcode == route.Opcode || existing.Name == route.Name))
                    {
                        Route duplicate = routes.First(existing => existing.Opcode == route.Opcode || existing.Name == route.Name);
                        throw new InvalidOperationException($"{route.Source}: module {module.Name}, {route.Name}: Duplicate opcode or route name " +
                            $"{route.Opcode}; conflicts with {duplicate.Name} ({duplicate.Source}, module {duplicate.Module.Name}).");
                    }

                    routes.Add(route);
                }
            }

            if (routes.Count == 0)
            {
                throw new InvalidOperationException("Protocol has no routes.");
            }

            routes.Sort((a, b) => a.Opcode.CompareTo(b.Opcode));
            foreach (Type type in byType.Keys.Concat(enums.Values))
            {
                if (!typeModules.ContainsKey(type))
                {
                    throw new InvalidOperationException($"Missing module ownership for {type.FullName}.");
                }
            }

            byte[] manifest = Manifest();
            byte[] hash = SHA256.HashData(manifest);
            var outputs = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [GeneratedDirectory + "GameNetProtocol.cs"] = Encoding.UTF8.GetBytes(CatalogCode(hash))
            };

            foreach (ProtocolModuleDefinition module in modules)
            {
                outputs.Add(GeneratedDirectory + module.Name + "Messages.g.cs", Encoding.UTF8.GetBytes(ModuleCode(module,
                    schemas.Values.Where(schema => typeModules[schema.Type] == module).ToArray(),
                    enums.Values.Where(type => typeModules[type] == module).ToArray(),
                    routes.Where(route => route.Module == module).ToArray())));
            }

            if (includeArtifacts)
            {
                outputs.Add(ArtifactDirectory + "protocol.manifest.json", manifest);
                outputs.Add(ArtifactDirectory + "protocol.sha256", Encoding.UTF8.GetBytes(Convert.ToHexString(hash).ToLowerInvariant() + "\n"));
                outputs.Add(ArtifactDirectory + "golden-vectors.json", GoldenVectors());
            }

            return outputs;
        }

        public void ValidateSerializationInfo(string directory)
        {
            foreach (Schema schema in schemas.Values)
            {
                string path = Path.Combine(directory, schema.Type.FullName + ".txt");
                string[] lines = File.ReadAllLines(path);
                string expectedHeader = "GenerateType Object " + schema.Type.FullName;
                string[] actualMembers = lines.Skip(2).Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => line[(line.LastIndexOf(' ') + 1)..]).ToArray();
                if (lines.Length < 2 || lines[0] != expectedHeader || lines[1] != "---" ||
                    !actualMembers.SequenceEqual(schema.Fields.Select(field => field.Name)))
                {
                    throw new InvalidOperationException($"MemoryPack serialization-info disagrees with analyzed members: {schema.Type.FullName}.");
                }
            }
        }

        private Schema Analyze(Type type)
        {
            if (visiting.Contains(type))
            {
                throw new InvalidOperationException($"Recursive DTO {type.FullName} is forbidden.");
            }

            if (byType.TryGetValue(type, out Schema? existing))
            {
                return existing;
            }

            var attribute = type.GetCustomAttribute<MemoryPackableAttribute>();
            if (!type.IsClass || type.IsAbstract || type.IsGenericType || type.IsNested || attribute == null ||
                attribute.GenerateType != GenerateType.Object || attribute.SerializeLayout != SerializeLayout.Explicit)
            {
                throw new InvalidOperationException($"{type.FullName} must be an explicit-layout MemoryPack DTO class.");
            }

            visiting.Add(type);
            if (type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Any(method => method.GetCustomAttributes().Any(value => value.GetType().Name.StartsWith("MemoryPackOn", StringComparison.Ordinal))))
            {
                throw new InvalidOperationException($"MemoryPack lifecycle callbacks are forbidden on protocol DTO {type.FullName}.");
            }

            var fields = new List<Field>();
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Type memberType;
                NullabilityInfo nullable;
                if (member is PropertyInfo property && property.GetIndexParameters().Length == 0 &&
                    (property.GetMethod?.IsPublic == true || property.IsDefined(typeof(MemoryPackIncludeAttribute))))
                {
                    if (property.GetMethod?.IsPublic != true)
                    {
                        throw new InvalidOperationException($"Included member {member.Name} must be publicly readable by the generated validator.");
                    }

                    memberType = property.PropertyType;
                    nullable = nullability.Create(property);
                }
                else if (member is FieldInfo field && (field.IsPublic || field.IsDefined(typeof(MemoryPackIncludeAttribute))))
                {
                    if (!field.IsPublic)
                    {
                        throw new InvalidOperationException($"Included member {member.Name} must be publicly readable by the generated validator.");
                    }

                    memberType = field.FieldType;
                    nullable = nullability.Create(field);
                }
                else
                {
                    continue;
                }

                if (member.IsDefined(typeof(MemoryPackIgnoreAttribute)))
                {
                    continue;
                }

                var order = member.GetCustomAttribute<MemoryPackOrderAttribute>();
                if (order == null)
                {
                    throw new InvalidOperationException($"Explicit MemoryPackOrder is required on {type.Name}.{member.Name}.");
                }

                if (member.GetCustomAttributes().Any(value => IsCustomFormatter(value.GetType())))
                {
                    throw new InvalidOperationException($"Custom formatters are forbidden on {type.Name}.{member.Name}.");
                }

                fields.Add(AnalyzeField(member, memberType, nullable, order.Order));
            }

            fields.Sort((a, b) => a.Order.CompareTo(b.Order));
            if (fields.Count > 249 || fields.Where((field, index) => field.Order != index).Any())
            {
                throw new InvalidOperationException($"{type.FullName} must have unique contiguous member orders starting at zero (at most 249 members).");
            }

            var schema = new Schema(type, fields);
            visiting.Remove(type);
            byType.Add(type, schema);
            if (schemas.ContainsKey(type.FullName!))
            {
                throw new InvalidOperationException($"Duplicate protocol type name {type.FullName}.");
            }

            schemas.Add(type.FullName!, schema);
            if (Depth(schema) > NetContractLimits.MaxObjectDepth)
            {
                throw new InvalidOperationException($"{type.FullName} exceeds the DTO depth budget.");
            }

            try
            {
                schema.Bounds = CalculateBounds(schema);
            }
            catch (OverflowException error)
            {
                throw new InvalidOperationException($"{type.FullName}: calculated MemoryPack size overflows Int64.", error);
            }

            return schema;
        }

        private Field AnalyzeField(MemberInfo member, Type type, NullabilityInfo nullable, int order)
        {
            if (Nullable.GetUnderlyingType(type) != null)
            {
                throw new InvalidOperationException($"Nullable<T> is forbidden: {member.DeclaringType!.Name}.{member.Name}. Use explicit HasValue/Value or a nullable DTO.");
            }

            if (!type.IsValueType && nullable.ReadState == NullabilityState.Unknown)
            {
                throw new InvalidOperationException($"Nullability must be explicit: {member.DeclaringType!.Name}.{member.Name}. Enable nullable annotations.");
            }

            var result = new Field(member.Name, order, type, nullable.ReadState == NullabilityState.Nullable);
            Type valueType = type;
            if (type.IsArray)
            {
                var array = member.GetCustomAttribute<NetArrayLimitAttribute>()
                    ?? throw new InvalidOperationException($"NetArrayLimit is required on {member.Name}.");
                if (type.GetArrayRank() != 1 || type.GetElementType()!.IsArray)
                {
                    throw new InvalidOperationException($"Only one-dimensional non-jagged arrays are supported: {member.Name}.");
                }

                result.ArrayLimit = array.MaxCount;
                result.ElementNullable = nullable.ElementType!.ReadState == NullabilityState.Nullable;
                valueType = type.GetElementType()!;
            }

            if (valueType == typeof(string))
            {
                var text = member.GetCustomAttribute<NetStringLimitAttribute>()
                    ?? throw new InvalidOperationException($"NetStringLimit is required on {member.Name}.");
                result.StringUtf16 = text.MaxUtf16Units;
                result.StringUtf8 = text.MaxUtf8Bytes;
            }
            else if (!PrimitiveBytes.ContainsKey(valueType))
            {
                if (valueType.IsEnum)
                {
                    AnalyzeEnum(valueType);
                }
                else
                {
                    result.Child = Analyze(valueType);
                }
            }

            return result;
        }

        private void AnalyzeEnum(Type type)
        {
            if (Enum.GetUnderlyingType(type) != typeof(int) || type.IsNested || type.IsDefined(typeof(FlagsAttribute)) ||
                !Enum.IsDefined(type, 0))
            {
                throw new InvalidOperationException($"{type.FullName}: enum must be top-level int32, declare zero and forbid flags.");
            }

            int[] values = Enum.GetValues(type).Cast<object>().Select(value => Convert.ToInt32(value)).ToArray();
            if (values.Distinct().Count() != values.Length)
            {
                throw new InvalidOperationException($"{type.FullName}: enum aliases are forbidden.");
            }

            enums[type.FullName!] = type;
        }

        private static int Depth(Schema schema)
        {
            return 1 + schema.Fields.Select(field => field.Child == null ? 0 : Depth(field.Child)).DefaultIfEmpty(0).Max();
        }

        private static int RouteBudget(ProtocolModuleDefinition module, ProtocolRouteDefinition route, Schema schema)
        {
            long capacity = schema.Bounds.Capacity;
            try
            {
                int result = checked((int)capacity);
                NetContractLimits.ValidateRoute(route.Opcode, route.Delivery, result);
                return result;
            }
            catch (Exception error) when (error is OverflowException || error is ArgumentOutOfRangeException)
            {
                throw new InvalidOperationException($"{route.Source}: module {module.Name}, {route.Name}, opcode {route.Opcode}: " +
                    $"calculated body budget {capacity} for {schema.Type.FullName} violates the {route.Delivery} channel contract.", error);
            }
        }

        private static EncodingBounds CalculateBounds(Schema schema)
        {
            // Object/Explicit: one member-count byte, then fields in serialization order.
            // Capacity also covers transient contiguous requests made by MemoryPack's writer.
            checked
            {
                long length = 1;
                long capacity = 1;
                foreach (Field field in schema.Fields)
                {
                    Type type = field.Type.IsArray ? field.Type.GetElementType()! : field.Type;
                    EncodingBounds value;
                    if (type == typeof(string))
                    {
                        long units = Math.Min(field.StringUtf16!.Value, field.StringUtf8!.Value);
                        long bytes = Math.Min(field.StringUtf8.Value, 3L * field.StringUtf16.Value);
                        // Null/empty strings use four bytes. Nonempty UTF-8 has two Int32 headers.
                        // WriteUtf8 requests (actual UTF-16 length + 1) * 3 + 8 before encoding.
                        value = bytes == 0 ? new EncodingBounds(4, 4)
                            : new EncodingBounds(8 + bytes, 8 + (units + 1) * 3);
                    }
                    else if (type.IsEnum || PrimitiveBytes.ContainsKey(type))
                    {
                        int bytes = type.IsEnum ? 4 : PrimitiveBytes[type];
                        value = new EncodingBounds(bytes, bytes);
                    }
                    else
                    {
                        value = field.Child!.Bounds;
                    }

                    if (field.ArrayLimit.HasValue)
                    {
                        int count = field.ArrayLimit.Value;
                        value = new EncodingBounds(4 + count * value.Length,
                            count == 0 ? 4 : 4 + (count - 1) * value.Length + value.Capacity);
                    }

                    capacity = Math.Max(capacity, length + value.Capacity);
                    length += value.Length;
                }

                return new EncodingBounds(length, Math.Max(length, capacity));
            }
        }

        private byte[] GoldenVectors()
        {
            var vectors = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (Schema schema in schemas.Values)
            {
                foreach (string mode in new[] { "default", "normal", "maximum" })
                {
                    object sample = ProtocolSamples.Create(schema.Type, mode);
                    byte[] bytes = (byte[])typeof(ProtocolSamples).GetMethod(nameof(ProtocolSamples.Serialize))!
                        .MakeGenericMethod(schema.Type).Invoke(null, new[] { sample })!;
                    vectors.Add(schema.Type.FullName + "/" + mode, Convert.ToHexString(bytes).ToLowerInvariant());
                }
            }

            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        private byte[] Manifest()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("controlVersion", 1);
                writer.WriteString("endianness", "little");
                writer.WriteStartArray("enums");
                foreach (Type type in enums.Values)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", type.FullName);
                    writer.WriteString("underlyingType", "System.Int32");
                    writer.WriteStartArray("values");
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                        .OrderBy(field => (int)field.GetRawConstantValue()!))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", field.Name);
                        writer.WriteNumber("value", (int)field.GetRawConstantValue()!);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartObject("limits");
                writer.WriteNumber("arrayCount", NetContractLimits.MaxArrayCount);
                writer.WriteNumber("dtoNodes", NetContractLimits.MaxDtoNodes);
                writer.WriteNumber("objectDepth", NetContractLimits.MaxObjectDepth);
                writer.WriteNumber("stringUtf16Units", NetContractLimits.MaxStringUtf16Units);
                writer.WriteNumber("stringUtf8Bytes", NetContractLimits.MaxStringUtf8Bytes);
                writer.WriteEndObject();
                writer.WriteString("memoryPackVersion", "1.21.4");
                writer.WriteStartArray("routes");
                foreach (Route route in routes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("delivery", route.Delivery.ToString());
                    writer.WriteString("kind", route.Kind);
                    writer.WriteNumber("maxBodyBytes", route.MaxBody);
                    writer.WriteNumber("opcode", route.Opcode);
                    writer.WriteString("name", route.Name);
                    if (route.Response != null)
                    {
                        writer.WriteNumber("responseMaxBodyBytes", route.ResponseMaxBody);
                        writer.WriteString("responseType", route.Response.Type.FullName);
                    }

                    writer.WriteBoolean("rootNullable", false);
                    writer.WriteString("type", route.Request.Type.FullName);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteString("stringEncoding", "utf8");
                writer.WriteStartArray("types");
                foreach (Schema schema in schemas.Values)
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("fields");
                    foreach (Field field in schema.Fields)
                    {
                        writer.WriteStartObject();
                        if (field.ArrayLimit.HasValue)
                        {
                            writer.WriteNumber("arrayLimit", field.ArrayLimit.Value);
                            writer.WriteBoolean("elementNullable", field.ElementNullable);
                        }

                        writer.WriteString("name", field.Name);
                        writer.WriteBoolean("nullable", field.Nullable);
                        writer.WriteNumber("order", field.Order);
                        if (field.StringUtf16.HasValue)
                        {
                            writer.WriteNumber("stringUtf16Units", field.StringUtf16.Value);
                            writer.WriteNumber("stringUtf8Bytes", field.StringUtf8!.Value);
                        }

                        writer.WriteString("type", field.Type.FullName);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteString("name", schema.Type.FullName);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteNumber("wireVersion", 1);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n");
        }

        private static StringBuilder CodeHeader()
        {
            return new StringBuilder("// Generated by DotNet/NetworkProtocol. Do not edit.\n" +
                "#nullable enable\nusing System;\nusing GameFrame.Network;\nusing MemoryPack;\n");
        }

        private string CatalogCode(byte[] hash)
        {
            var code = CodeHeader();
            void Line(string text = "") => code.Append(text).Append('\n');
            Line();
            Line("namespace Game.Protocol");
            Line("{");
            Line("    public static class GameNetProtocol");
            Line("    {");
            Line("        public static NetProtocolCatalog Catalog { get; } = new NetProtocolCatalog(");
            Line("            new byte[] { " + string.Join(", ", hash.Select(value => "0x" + value.ToString("x2"))) + " },");
            for (int i = 0; i < routes.Count; i++)
            {
                Route route = routes[i];
                Line($"            global::{route.Module.Namespace}.{route.Module.Name}Routes.{route.MemberName}" + (i == routes.Count - 1 ? ");" : ","));
            }

            Line("    }");
            Line("}");
            return code.ToString();
        }

        private string ModuleCode(ProtocolModuleDefinition module, IReadOnlyList<Schema> moduleSchemas, IReadOnlyList<Type> moduleEnums,
            IReadOnlyList<Route> moduleRoutes)
        {
            var code = CodeHeader();
            void Line(string text = "") => code.Append(text).Append('\n');
            Line();
            Line("namespace " + module.Namespace);
            Line("{");
            if (moduleRoutes.Count != 0)
            {
                Line($"    public static class {module.Name}Routes");
                Line("    {");
                foreach (Route route in moduleRoutes)
                {
                    string request = CsharpType(route.Request.Type);
                    string descriptor = route.Kind == "Rpc" ? $"NetRpc<{request}, {CsharpType(route.Response!.Type)}>"
                        : route.Kind == "Push" ? $"NetPush<{request}>"
                        : route.Delivery == NetDelivery.Reliable ? $"NetSend<{request}>" : $"NetUnreliable<{request}>";
                    string arguments = route.Kind == "Rpc"
                        ? $"{route.Opcode}u, {route.MaxBody}, {route.ResponseMaxBody}, {CodecOwner(route.Request.Type)}.Codec{route.Request.Type.Name}, {CodecOwner(route.Response!.Type)}.Codec{route.Response!.Type.Name}"
                        : route.Kind == "Push" ? $"{route.Opcode}u, NetDelivery.{route.Delivery}, {route.MaxBody}, {CodecOwner(route.Request.Type)}.Codec{route.Request.Type.Name}"
                        : $"{route.Opcode}u, {route.MaxBody}, {CodecOwner(route.Request.Type)}.Codec{route.Request.Type.Name}";
                    Line($"        public static readonly {descriptor} {route.MemberName} = new {descriptor}({arguments});");
                }

                Line("    }");
                Line();
            }

            Line($"    internal static class {module.Name}Codecs");
            Line("    {");
            foreach (Schema schema in moduleSchemas)
            {
                string type = schema.Type.Name;
                string fullType = CsharpType(schema.Type);
                Line($"        internal static readonly NetMessageCodec<{fullType}> Codec{type} = new NetMessageCodec<{fullType}>(");
                Line($"            (writer, value) => MemoryPackSerializer.Serialize(writer, value, MemoryPackSerializerOptions.Utf8),");
                Line($"            bytes => MemoryPackSerializer.Deserialize<{fullType}>(bytes, MemoryPackSerializerOptions.Utf8)!,");
                Line($"            value => {{ int nodes = 0; CheckObject{type}(value, false, 1, ref nodes); }},");
                Line($"            bytes => {{ var reader = new NetWireReader(bytes); ReadObject{type}(ref reader, false, 1); reader.Complete(); }});");
                Line();
            }

            foreach (Type type in moduleEnums)
            {
                string valid = string.Join(" || ", Enum.GetValues(type).Cast<object>().Select(Convert.ToInt32)
                    .OrderBy(value => value).Select(value => $"value == {value}"));
                Line($"        private static bool IsEnum{type.Name}(int value) => {valid};");
                Line($"        internal static void ReadEnum{type.Name}(ref NetWireReader reader)");
                Line("        {");
                Line($"            if (!IsEnum{type.Name}(reader.Int32()))");
                Line("            {");
                Line("                throw new NetworkProtocolException(\"Unknown enum value.\");");
                Line("            }");
                Line("        }");
                Line($"        internal static void CheckEnum{type.Name}({CsharpType(type)} value)");
                Line("        {");
                Line($"            if (!IsEnum{type.Name}((int)value))");
                Line("            {");
                Line("                throw new ArgumentException(\"Unknown enum value.\");");
                Line("            }");
                Line("        }");
                Line();
            }

            foreach (Schema schema in moduleSchemas)
            {
                string type = schema.Type.Name;
                Line($"        internal static void ReadObject{type}(ref NetWireReader reader, bool nullable, int depth)");
                Line("        {");
                Line($"            if (!reader.Object({schema.Fields.Count}, nullable, depth))");
                Line("            {");
                Line("                return;");
                Line("            }");
                foreach (Field field in schema.Fields)
                {
                    if (field.ArrayLimit.HasValue)
                    {
                        Line($"            int count{field.Order} = reader.Array({field.ArrayLimit.Value}, {Bool(field.Nullable)});");
                        Line($"            for (int i = 0; i < count{field.Order}; i++)");
                        Line("            {");
                        Line("                " + ReadValue(field, field.Type.GetElementType()!, field.ElementNullable));
                        Line("            }");
                    }
                    else
                    {
                        Line("            " + ReadValue(field, field.Type, field.Nullable));
                    }
                }

                Line("        }");
                Line();
                Line($"        internal static void CheckObject{type}({CsharpType(schema.Type)}? value, bool nullable, int depth, ref int nodes)");
                Line("        {");
                Line("            if (value == null)");
                Line("            {");
                Line("                if (!nullable)");
                Line("                {");
                Line("                    throw new ArgumentException(\"Nonnullable DTO is null.\");");
                Line("                }");
                Line("                return;");
                Line("            }");
                Line("            if (depth > NetContractLimits.MaxObjectDepth || ++nodes > NetContractLimits.MaxDtoNodes)");
                Line("            {");
                Line("                throw new ArgumentException(\"Object budget exceeded.\");");
                Line("            }");
                foreach (Field field in schema.Fields)
                {
                    string access = "value.@" + field.Name;
                    if (field.ArrayLimit.HasValue)
                    {
                        Line($"            if ({access} == null)");
                        Line("            {");
                        if (!field.Nullable)
                        {
                            Line("                throw new ArgumentException(\"Nonnullable array is null.\");");
                        }
                        Line("            }");
                        Line("            else");
                        Line("            {");
                        Line($"                if ({access}.Length > {field.ArrayLimit.Value})");
                        Line("                {");
                        Line("                    throw new ArgumentException(\"Array count exceeds its limit.\");");
                        Line("                }");
                        string? check = CheckValue(field, field.Type.GetElementType()!, field.ElementNullable, "element");
                        if (check != null)
                        {
                            Line($"                foreach (var element in {access})");
                            Line("                {");
                            Line("                    " + check);
                            Line("                }");
                        }
                        Line("            }");
                    }
                    else
                    {
                        string? check = CheckValue(field, field.Type, field.Nullable, access);
                        if (check != null)
                        {
                            Line("            " + check);
                        }
                    }
                }

                Line("        }");
                Line();
            }

            Line("    }");
            Line("}");
            return code.ToString();
        }

        private string ReadValue(Field field, Type type, bool nullable)
        {
            if (type == typeof(string))
            {
                return $"reader.String({field.StringUtf16}, {field.StringUtf8}, {Bool(nullable)});";
            }

            if (type == typeof(bool))
            {
                return "reader.Boolean();";
            }

            if (type.IsEnum)
            {
                return $"{CodecOwner(type)}.ReadEnum{type.Name}(ref reader);";
            }

            return PrimitiveBytes.TryGetValue(type, out int bytes) ? $"reader.Primitive({bytes});"
                : $"{CodecOwner(type)}.ReadObject{type.Name}(ref reader, {Bool(nullable)}, depth + 1);";
        }

        private string? CheckValue(Field field, Type type, bool nullable, string access)
        {
            return type == typeof(string) ? $"NetWireReader.ValidateString({access}, {field.StringUtf16}, {field.StringUtf8}, {Bool(nullable)});"
                : type.IsEnum ? $"{CodecOwner(type)}.CheckEnum{type.Name}({access});"
                : PrimitiveBytes.ContainsKey(type) ? null : $"{CodecOwner(type)}.CheckObject{type.Name}({access}, {Bool(nullable)}, depth + 1, ref nodes);";
        }

        private string CodecOwner(Type type)
        {
            ProtocolModuleDefinition module = typeModules[type];
            return "global::" + module.Namespace + "." + module.Name + "Codecs";
        }

        private static void RequireIdentifier(string name)
        {
            if (!SyntaxFacts.IsValidIdentifier(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            {
                throw new InvalidOperationException($"Invalid generated C# identifier: {name}.");
            }
        }

        private static string CsharpType(Type type) => "global::" + type.FullName;

        private static string Bool(bool value) => value ? "true" : "false";

        private static bool IsCustomFormatter(Type type)
        {
            for (Type? current = type; current != null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(MemoryPackCustomFormatterAttribute<>))
                {
                    return true;
                }
            }

            return false;
        }

        private static readonly Dictionary<Type, int> PrimitiveBytes = new Dictionary<Type, int>
        {
            [typeof(byte)] = 1, [typeof(sbyte)] = 1, [typeof(bool)] = 1,
            [typeof(short)] = 2, [typeof(ushort)] = 2,
            [typeof(int)] = 4, [typeof(uint)] = 4, [typeof(float)] = 4,
            [typeof(long)] = 8, [typeof(ulong)] = 8, [typeof(double)] = 8
        };

        private readonly record struct EncodingBounds(long Length, long Capacity);
        private sealed record Schema(Type Type, List<Field> Fields)
        {
            public EncodingBounds Bounds { get; set; }
        }
        private sealed record Route(ProtocolModuleDefinition Module, string Name, string MemberName, string Source,
            uint Opcode, string Kind, NetDelivery Delivery, Schema Request, Schema? Response, int MaxBody, int ResponseMaxBody);

        private sealed class Field
        {
            public string Name { get; }
            public int Order { get; }
            public Type Type { get; }
            public bool Nullable { get; }
            public bool ElementNullable { get; set; }
            public int? ArrayLimit { get; set; }
            public int? StringUtf16 { get; set; }
            public int? StringUtf8 { get; set; }
            public Schema? Child { get; set; }

            public Field(string name, int order, Type type, bool nullable)
            {
                Name = name;
                Order = order;
                Type = type;
                Nullable = nullable;
            }
        }
    }
}
