using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Protocol;
using Game.Protocol.Common;
using Game.Protocol.Gomoku;
using Game.Protocol.Session;
using GameFrame.Network;
using MemoryPack;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed class NetworkMemoryPackTests
    {
        [Test]
        public void PushShapeValidationDoesNotAllocateDtoAndValidatedDecodeDoesNotRescan()
        {
            int validations = 0;
            int decodes = 0;
            var codec = new NetMessageCodec<GomokuSnapshot>((_, _) => { }, bytes =>
            {
                decodes++;
                return new GomokuSnapshot();
            }, _ => { }, bytes =>
            {
                validations++;
                if (bytes.Length != 1 || bytes[0] != 0)
                {
                    throw new NetworkProtocolException("Invalid test Push.");
                }
            });
            NetRoute route = new NetPush<GomokuSnapshot>(2000, NetDelivery.Reliable, 1, codec);
            byte[] body = { 0 };
            route.ValidatePushBody(body);
            Assert.AreEqual(1, validations);
            Assert.AreEqual(0, decodes, "Unsubscribed Push needs no DTO.");
            codec.DecodeValidated(body);
            Assert.AreEqual(1, validations, "Dispatch must not scan an already validated body again.");
            Assert.AreEqual(1, decodes);
            Assert.Throws<NetworkProtocolException>(() => route.ValidatePushBody(new byte[] { 255 }));
            Assert.AreEqual(1, decodes, "Malformed input must fail before deserialization.");
            codec.Decode(body);
            Assert.AreEqual(3, validations, "The normal RPC Decode path must still validate.");
            Assert.AreEqual(2, decodes);
        }

        [TestCaseSource(nameof(GoldenVectors))]
        public void AllDtoVectorsMatchDotNet10(Type type, string mode, string hex)
        {
            object value = CreateSample(type, mode);
            MethodInfo serialize = typeof(NetworkMemoryPackTests).GetMethod(nameof(Serialize), BindingFlags.NonPublic | BindingFlags.Static);
            byte[] actual = (byte[])serialize.MakeGenericMethod(type).Invoke(null, new[] { value });
            Assert.AreEqual(hex, BitConverter.ToString(actual).Replace("-", "").ToLowerInvariant());
        }

        [Test]
        public void RouteFirstInitializationDoesNotCreateNullCatalogEntries()
        {
            Assert.AreEqual(1000u, SessionRoutes.Login.Opcode);
            Assert.AreEqual(14, GameNetProtocol.Catalog.RouteCount);
            Assert.AreEqual(3094, GameNetProtocol.Catalog.MaxReliableBodyBytes);
            Assert.AreEqual(0, GameNetProtocol.Catalog.MaxUnreliableBodyBytes);
            GameNetProtocol.Catalog.RequireRegistered(SessionRoutes.Login);
        }

        [Test]
        public void Utf8StringsAndNestedArraysPassGeneratedValidator()
        {
            var login = new LoginRequest { Account = "中文😀", Password = "密码" };
            var writer = new ArrayBufferWriter<byte>();
            SessionRoutes.Login.RequestCodec.Encode(writer, login);
            LoginRequest decoded = SessionRoutes.Login.RequestCodec.Decode(writer.WrittenSpan);
            Assert.AreEqual(login.Account, decoded.Account);
            Assert.AreEqual(login.Password, decoded.Password);
            var snapshot = new GomokuSnapshot { Members = new[] { new GomokuMember { AccountId = 42, MemberId = 1250 } } };
            writer.Clear();
            GomokuRoutes.GomokuRoomSnapshot.Codec.Encode(writer, snapshot);
            Assert.AreEqual(1250, GomokuRoutes.GomokuRoomSnapshot.Codec.Decode(writer.WrittenSpan).Members[0].MemberId);
        }

        [Test]
        public void HugeStringDeclarationIsRejectedBeforeDeserialization()
        {
            var bytes = new byte[9];
            bytes[0] = 2;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), ~int.MaxValue);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), int.MaxValue);
            Assert.Throws<NetworkProtocolException>(() => SessionRoutes.Login.RequestCodec.Decode(bytes));
        }

        [Test]
        public void HugeArrayDeclarationIsRejectedBeforeDeserialization()
        {
            byte[] bytes = MemoryPackSerializer.Serialize(new GomokuSnapshot(), MemoryPackSerializerOptions.Utf8);
            // Empty Moves count precedes the four recipient/admission uint64 fields.
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 36), int.MaxValue);
            Assert.Throws<NetworkProtocolException>(() => GomokuRoutes.GomokuRoomSnapshot.Codec.Decode(bytes));
        }

        [Test]
        public void InvalidUtf8AndFalseUtf16LengthAreRejected()
        {
            byte[] bytes = MemoryPackSerializer.Serialize(new LoginRequest { Account = "a" }, MemoryPackSerializerOptions.Utf8);
            bytes[9] = 0xff;
            Assert.Throws<NetworkProtocolException>(() => SessionRoutes.Login.RequestCodec.Decode(bytes));
            bytes[9] = (byte)'a';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), 2);
            Assert.Throws<NetworkProtocolException>(() => SessionRoutes.Login.RequestCodec.Decode(bytes));
        }

        [Test]
        public void NullRootNullElementsAndTrailingBytesAreRejected()
        {
            Assert.Throws<NetworkProtocolException>(() => SessionRoutes.Login.RequestCodec.Decode(new byte[] { 255 }));
            byte[] snapshot = MemoryPackSerializer.Serialize(new GomokuSnapshot { Members = new GomokuMember[] { null } }, MemoryPackSerializerOptions.Utf8);
            Assert.Throws<NetworkProtocolException>(() => GomokuRoutes.GomokuRoomSnapshot.Codec.Decode(snapshot));
            byte[] bytes = MemoryPackSerializer.Serialize(new LoginRequest(), MemoryPackSerializerOptions.Utf8);
            Array.Resize(ref bytes, bytes.Length + 1);
            Assert.Throws<NetworkProtocolException>(() => SessionRoutes.Login.RequestCodec.Decode(bytes));
        }

        [Test]
        public void OutgoingConstraintsRunBeforeSerializerAndDoNotMutateDto()
        {
            var request = new LoginRequest { Account = new string('x', 65), Password = "secret" };
            var writer = new ArrayBufferWriter<byte>();
            Assert.Throws<ArgumentException>(() => SessionRoutes.Login.RequestCodec.Encode(writer, request));
            Assert.AreEqual(0, writer.WrittenCount);
            Assert.AreEqual(65, request.Account.Length);
            Assert.AreEqual("secret", request.Password);
        }

        [Test]
        public void CatalogCopiesHashAndRejectsDuplicateOpcodes()
        {
            var hash = new byte[32];
            var catalog = new NetProtocolCatalog(hash, SessionRoutes.Login);
            hash[0] = 99;
            var copy = new byte[32];
            catalog.CopyHashTo(copy);
            Assert.AreEqual(0, copy[0]);
            Assert.Throws<ArgumentException>(() => new NetProtocolCatalog(hash, SessionRoutes.Login, SessionRoutes.Login));
        }

        [Test]
        public void UnknownBusinessResultIsRejectedBeforeDeserializationOrWriting()
        {
            var response = new OperationResponse { ResultCode = (ResultCode)999 };
            byte[] bytes = MemoryPackSerializer.Serialize(response, MemoryPackSerializerOptions.Utf8);
            Assert.Throws<NetworkProtocolException>(() => SessionRoutes.Logout.ResponseCodec.Decode(bytes));
            var writer = new ArrayBufferWriter<byte>();
            Assert.Throws<ArgumentException>(() => SessionRoutes.Logout.ResponseCodec.Encode(writer, response));
            Assert.AreEqual(0, writer.WrittenCount);
        }

        [TestCase(ResultCode.Success)]
        [TestCase(ResultCode.InvalidCredentials)]
        [TestCase(ResultCode.InvalidInput)]
        public void DeclaredBusinessResultUsesFourByteLittleEndianValue(ResultCode code)
        {
            var writer = new ArrayBufferWriter<byte>();
            SessionRoutes.Logout.ResponseCodec.Encode(writer, new OperationResponse { ResultCode = code });
            Assert.AreEqual(5, writer.WrittenCount);
            Assert.AreEqual((int)code, BinaryPrimitives.ReadInt32LittleEndian(writer.WrittenSpan.Slice(1)));
            Assert.AreEqual(code, SessionRoutes.Logout.ResponseCodec.Decode(writer.WrittenSpan).ResultCode);
        }

        [Test]
        public void RoomPushesUseDistinctRoutesWithSharedCodecs()
        {
            Assert.AreEqual(NetDelivery.Reliable, GomokuRoutes.GomokuRoomSnapshot.Delivery);
            Assert.AreEqual(NetDelivery.Reliable, GomokuRoutes.GomokuRoomClosed.Delivery);
            Assert.AreNotEqual(GomokuRoutes.GomokuRoomSnapshot.Opcode, GomokuRoutes.GomokuRoomClosed.Opcode);
            Assert.AreSame(GomokuRoutes.GomokuRoomSnapshot.Codec, GomokuRoutes.GomokuRoomClosed.Codec);
            Assert.AreEqual(0, GameNetProtocol.Catalog.MaxUnreliableBodyBytes);
            GameNetProtocol.Catalog.RequireRegistered(GomokuRoutes.GomokuRoomClosed);
        }

        private static IEnumerable<TestCaseData> GoldenVectors()
        {
#if UNITY_EDITOR
            string path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "../../Protocol/Generated/golden-vectors.json"));
#else
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Unity")))
            {
                directory = directory.Parent;
            }

            string path = Path.Combine(directory.FullName, "Protocol/Generated/golden-vectors.json");
#endif
            foreach (Match match in Regex.Matches(File.ReadAllText(path), "\"([^\"]+)\"\\s*:\\s*\"([0-9a-f]+)\""))
            {
                string key = match.Groups[1].Value;
                int separator = key.LastIndexOf('/');
                Type type = typeof(LoginRequest).Assembly.GetType(key.Substring(0, separator), true);
                yield return new TestCaseData(type, key.Substring(separator + 1), match.Groups[2].Value).SetName(key);
            }
        }

        private static byte[] Serialize<T>(object value) where T : class
        {
            byte[] bytes = MemoryPackSerializer.Serialize((T)value, MemoryPackSerializerOptions.Utf8);
            foreach (NetRoute route in GameNetProtocol.Catalog.Routes)
            {
                foreach (PropertyInfo property in route.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (property.PropertyType != typeof(NetMessageCodec<T>))
                    {
                        continue;
                    }

                    int limit = property.Name == "ResponseCodec" ? route.ResponseMaxBodyBytes : route.MaxBodyBytes;
                    var memory = new FrameMemoryBudget(32 + limit + FrameMemoryBudget.FrameRecordReservation);
                    using (OwnedFrame frame = memory.Allocate(32 + limit))
                    {
                        var writer = new BoundedFrameWriter(frame);
                        ((NetMessageCodec<T>)property.GetValue(route)).Encode(writer, (T)value);
                        writer.Complete(FrameKind.Request, route.Opcode, 1);
                        Assert.LessOrEqual(writer.BodyLength, limit);
                        CollectionAssert.AreEqual(bytes, frame.Body.ToArray());
                    }

                    Assert.AreEqual(0, memory.OwnedBytes);
                }
            }

            return bytes;
        }

        private static object CreateSample(Type type, string mode)
        {
            object value = Activator.CreateInstance(type);
            if (mode == "default")
            {
                return value;
            }

            foreach (PropertyInfo property in type.GetProperties())
            {
                if (property.IsDefined(typeof(MemoryPackOrderAttribute)) && !property.IsDefined(typeof(MemoryPackIgnoreAttribute)))
                {
                    property.SetValue(value, SampleValue(property.PropertyType, property, mode));
                }
            }

            foreach (FieldInfo field in type.GetFields())
            {
                if (field.IsDefined(typeof(MemoryPackOrderAttribute)) && !field.IsDefined(typeof(MemoryPackIgnoreAttribute)))
                {
                    field.SetValue(value, SampleValue(field.FieldType, field, mode));
                }
            }

            return value;
        }

        private static object SampleValue(Type type, MemberInfo member, string mode)
        {
            if (type.IsArray)
            {
                int max = member.GetCustomAttribute<NetArrayLimitAttribute>().MaxCount;
                int count = mode == "maximum" ? max : Math.Min(3, max);
                Array array = Array.CreateInstance(type.GetElementType(), count);
                for (int i = 0; i < count; i++)
                {
                    array.SetValue(SampleValue(type.GetElementType(), member, mode), i);
                }

                return array;
            }

            if (type == typeof(string))
            {
                var limit = member.GetCustomAttribute<NetStringLimitAttribute>();
                if (mode == "maximum")
                {
                    return new string('界', Math.Min(limit.MaxUtf16Units, limit.MaxUtf8Bytes / 3));
                }

                return limit.MaxUtf16Units >= 4 && limit.MaxUtf8Bytes >= 10 ? "中文😀" : string.Empty;
            }

            if (type == typeof(bool))
            {
                return true;
            }

            if (type.IsEnum)
            {
                Array values = Enum.GetValues(type);
                var sorted = new List<object>();
                foreach (object value in values)
                {
                    sorted.Add(value);
                }

                sorted.Sort((left, right) => Convert.ToInt32(left).CompareTo(Convert.ToInt32(right)));
                if (mode == "maximum")
                {
                    return sorted[sorted.Count - 1];
                }

                foreach (object value in sorted)
                {
                    if (Convert.ToInt32(value) != 0)
                    {
                        return value;
                    }
                }

                return sorted[0];
            }

            if (type.IsPrimitive)
            {
                if (mode == "maximum")
                {
                    return type.GetField("MaxValue").GetValue(null);
                }

                return Convert.ChangeType(42, type, CultureInfo.InvariantCulture);
            }

            return CreateSample(type, mode);
        }
    }
}
