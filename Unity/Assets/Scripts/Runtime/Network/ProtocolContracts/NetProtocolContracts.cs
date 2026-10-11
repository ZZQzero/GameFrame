using System;

namespace GameFrame.Network
{
    public enum NetDelivery
    {
        Reliable = 1,
        Unreliable = 2
    }

    public enum NetworkTransportKind
    {
        WebSocket = 1,
        Kcp = 2
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class NetStringLimitAttribute : Attribute
    {
        public int MaxUtf16Units { get; }
        public int MaxUtf8Bytes { get; }

        public NetStringLimitAttribute(int maxUtf16Units, int maxUtf8Bytes)
        {
            if (maxUtf16Units < 0 || maxUtf16Units > NetContractLimits.MaxStringUtf16Units)
            {
                throw new ArgumentOutOfRangeException(nameof(maxUtf16Units));
            }

            if (maxUtf8Bytes < 0 || maxUtf8Bytes > NetContractLimits.MaxStringUtf8Bytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
            }

            MaxUtf16Units = maxUtf16Units;
            MaxUtf8Bytes = maxUtf8Bytes;
        }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class NetArrayLimitAttribute : Attribute
    {
        public int MaxCount { get; }

        public NetArrayLimitAttribute(int maxCount)
        {
            if (maxCount < 0 || maxCount > NetContractLimits.MaxArrayCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCount));
            }

            MaxCount = maxCount;
        }
    }

    public static class NetContractLimits
    {
        public const int MaxStringUtf16Units = 256;
        public const int MaxStringUtf8Bytes = 1024;
        public const int MaxArrayCount = 512;
        public const int MaxObjectDepth = 8;
        public const int MaxDtoNodes = 2048;

        internal static void ValidateRoute(uint opcode, NetDelivery delivery, int maxBodyBytes)
        {
            if (opcode < 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(opcode), "Business opcodes start at 1000.");
            }

            if (delivery != NetDelivery.Reliable && delivery != NetDelivery.Unreliable)
            {
                throw new ArgumentOutOfRangeException(nameof(delivery));
            }

            int frameLimit = delivery == NetDelivery.Reliable ? 32768 : 1150;
            if (maxBodyBytes < 1 || maxBodyBytes > frameLimit - 32)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
            }
        }
    }
}
