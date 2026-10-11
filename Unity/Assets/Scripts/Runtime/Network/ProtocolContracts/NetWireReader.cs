using System;
using System.Buffers.Binary;
using System.Text;

namespace GameFrame.Network
{
    /// <summary>生成验证器的 MemoryPack 1.21.4 读取原语；不分配 DTO、字符串或数组。</summary>
    public ref struct NetWireReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private ReadOnlySpan<byte> remaining;
        private int nodes;

        public NetWireReader(ReadOnlySpan<byte> bytes)
        {
            remaining = bytes;
            nodes = 0;
        }

        public bool Object(int members, bool nullable, int depth)
        {
            byte header = Take(1)[0];
            if (header == 255)
            {
                if (!nullable)
                {
                    throw new NetworkProtocolException("Nonnullable DTO is null.");
                }

                return false;
            }

            if (header != members || depth > NetContractLimits.MaxObjectDepth || ++nodes > NetContractLimits.MaxDtoNodes)
            {
                throw new NetworkProtocolException("Invalid DTO member count or object budget.");
            }

            return true;
        }

        public void Primitive(int bytes)
        {
            Take(bytes);
        }

        public void Boolean()
        {
            if (Take(1)[0] > 1)
            {
                throw new NetworkProtocolException("Boolean must be 0 or 1.");
            }
        }

        public int Array(int maxCount, bool nullable)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(Take(4));
            if (count == -1 && nullable)
            {
                return -1;
            }

            if (count < 0 || count > maxCount || count > remaining.Length)
            {
                throw new NetworkProtocolException("Invalid array count.");
            }

            return count;
        }

        public void String(int maxUtf16, int maxUtf8, bool nullable)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(Take(4));
            if (length == -1 && nullable)
            {
                return;
            }

            if (length == 0)
            {
                return;
            }

            if (length >= -1)
            {
                throw new NetworkProtocolException("String must use nonnull UTF-8 encoding.");
            }

            int utf8Length = ~length;
            int utf16Length = BinaryPrimitives.ReadInt32LittleEndian(Take(4));
            if (utf8Length > maxUtf8 || utf16Length < 1 || utf16Length > maxUtf16)
            {
                throw new NetworkProtocolException("String exceeds its declared constraints.");
            }

            try
            {
                if (StrictUtf8.GetCharCount(Take(utf8Length)) != utf16Length)
                {
                    throw new NetworkProtocolException("String UTF-16 length does not match UTF-8 bytes.");
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new NetworkProtocolException("Invalid UTF-8.", exception);
            }
        }

        public void Complete()
        {
            if (!remaining.IsEmpty)
            {
                throw new NetworkProtocolException("DTO has trailing bytes.");
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > remaining.Length)
            {
                throw new NetworkProtocolException("Truncated DTO.");
            }

            ReadOnlySpan<byte> value = remaining.Slice(0, count);
            remaining = remaining.Slice(count);
            return value;
        }

        public static void ValidateString(string value, int maxUtf16, int maxUtf8, bool nullable)
        {
            if (value == null)
            {
                if (!nullable)
                {
                    throw new ArgumentException("Nonnullable string is null.");
                }

                return;
            }

            if (value.Length > maxUtf16 || StrictUtf8.GetByteCount(value) > maxUtf8)
            {
                throw new ArgumentException("String exceeds its declared constraints.");
            }
        }
    }
}
