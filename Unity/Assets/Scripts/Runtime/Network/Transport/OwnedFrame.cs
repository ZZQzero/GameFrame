#nullable disable
using System;
using System.Buffers;
using System.Threading;

namespace GameFrame.Network
{
    // Exact-size allocations avoid hidden pool capacity. Every lease includes in-flight bytes.
    internal sealed class FrameMemoryBudget
    {
        // Conservative allowance for the array/lease, queue entry and transport handoff records.
        // This is admission reservation, not a claim about CLR/IL2CPP object layout or RSS.
        internal const int FrameRecordReservation = 512;
        private readonly object gate = new object();
        private readonly long limit;
        private readonly FrameMemoryBudget parent;
        private long owned;
        private long peak;

        internal long PeakReservedBytes
        {
            get
            {
                lock (gate)
                {
                    return peak;
                }
            }
        }

        public long OwnedBytes
        {
            get
            {
                lock (gate)
                {
                    return owned;
                }
            }
        }

        public FrameMemoryBudget(long limit, FrameMemoryBudget parent = null)
        {
            if (limit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            this.limit = limit;
            this.parent = parent;
        }

        internal void Reserve(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            lock (gate)
            {
                if (count > limit - owned)
                {
                    throw new NetworkBackpressureException("Owned network memory limit exceeded.");
                }

                parent?.Reserve(count);
                owned += count;
                peak = Math.Max(peak, owned);
            }
        }

        internal void Release(int count)
        {
            lock (gate)
            {
                owned -= count;
                parent?.Release(count);
            }
        }

        public OwnedFrame Allocate(int capacity)
        {
            int reservation = checked(capacity + FrameRecordReservation);
            Reserve(reservation);
            try
            {
                return new OwnedFrame(this, new byte[capacity]);
            }
            catch
            {
                Release(reservation);
                throw;
            }
        }
    }

    internal sealed class OwnedFrame : IDisposable
    {
        private readonly FrameMemoryBudget budget;
        private byte[] bytes;
        public byte[] Bytes => bytes ?? throw new ObjectDisposedException(nameof(OwnedFrame));
        public int Capacity => Bytes.Length;
        public int Length { get; internal set; }
        public FrameHeader Header { get; internal set; }
        internal bool AcceptedByTransport { get; set; }
        public NetDelivery Delivery { get; internal set; } = NetDelivery.Reliable;
        public long AdmittedAt { get; internal set; }

        internal OwnedFrame(FrameMemoryBudget budget, byte[] bytes)
        {
            this.budget = budget;
            this.bytes = bytes;
        }

        public ReadOnlySpan<byte> Body => Bytes.AsSpan(FrameCodec.HeaderBytes, Header.BodyLength);

        public void Dispose()
        {
            byte[] released = Interlocked.Exchange(ref bytes, null);
            if (released != null)
            {
                Array.Clear(released, 0, released.Length);
                budget.Release(checked(released.Length + FrameMemoryBudget.FrameRecordReservation));
            }
        }
    }

    internal sealed class BoundedFrameWriter : IBufferWriter<byte>
    {
        private readonly OwnedFrame frame;
        private int position = FrameCodec.HeaderBytes;
        public int BodyLength => position - FrameCodec.HeaderBytes;

        public BoundedFrameWriter(OwnedFrame frame)
        {
            this.frame = frame;
        }

        public void Advance(int count)
        {
            if (count < 0 || count > frame.Capacity - position)
            {
                throw new NetworkProtocolException("Serializer exceeded the route body limit.");
            }

            position += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            RequireCapacity(sizeHint);
            return frame.Bytes.AsMemory(position);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            RequireCapacity(sizeHint);
            return frame.Bytes.AsSpan(position);
        }

        private void RequireCapacity(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            if (Math.Max(1, sizeHint) > frame.Capacity - position)
            {
                throw new NetworkProtocolException("Serializer exceeded the route body limit.");
            }
        }

        public void Complete(FrameKind kind, uint opcode, ulong correlationId, int statusCode = 0)
        {
            frame.Header = new FrameHeader(kind, opcode, correlationId, BodyLength, statusCode);
            frame.Length = position;
            FrameCodec.WriteHeader(frame.Bytes, frame.Header);
        }
    }
}
