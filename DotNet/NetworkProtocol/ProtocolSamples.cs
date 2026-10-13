using System.Globalization;
using System.Reflection;
using GameFrame.Network;
using MemoryPack;

namespace GameFrame.ProtocolTools
{
    public static class ProtocolSamples
    {
        public static byte[] Serialize<T>(object value) => MemoryPackSerializer.Serialize((T)value, MemoryPackSerializerOptions.Utf8);

        public static object Create(Type type, string mode)
        {
            object value = Activator.CreateInstance(type)!;
            if (mode == "default")
            {
                return value;
            }

            foreach (PropertyInfo property in type.GetProperties().Where(property => property.IsDefined(typeof(MemoryPackOrderAttribute)) &&
                !property.IsDefined(typeof(MemoryPackIgnoreAttribute))))
            {
                property.SetValue(value, SampleValue(property.PropertyType, property, mode));
            }

            foreach (FieldInfo field in type.GetFields().Where(field => field.IsDefined(typeof(MemoryPackOrderAttribute)) &&
                !field.IsDefined(typeof(MemoryPackIgnoreAttribute))))
            {
                field.SetValue(value, SampleValue(field.FieldType, field, mode));
            }

            return value;
        }

        private static object SampleValue(Type type, MemberInfo member, string mode)
        {
            if (type.IsArray)
            {
                int max = member.GetCustomAttribute<NetArrayLimitAttribute>()!.MaxCount;
                int count = mode == "maximum" ? max : Math.Min(3, max);
                Array array = Array.CreateInstance(type.GetElementType()!, count);
                for (int i = 0; i < count; i++)
                {
                    array.SetValue(SampleValue(type.GetElementType()!, member, mode), i);
                }

                return array;
            }

            if (type == typeof(string))
            {
                var limit = member.GetCustomAttribute<NetStringLimitAttribute>()!;
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
                object[] values = Enum.GetValues(type).Cast<object>().OrderBy(value => Convert.ToInt32(value)).ToArray();
                return mode == "maximum" ? values[^1] : values.FirstOrDefault(value => Convert.ToInt32(value) != 0) ?? values[0];
            }

            if (type.IsPrimitive)
            {
                if (mode == "maximum")
                {
                    return type.GetField("MaxValue")!.GetValue(null)!;
                }

                return Convert.ChangeType(42, type, CultureInfo.InvariantCulture);
            }

            return Create(type, mode);
        }
    }
}
