using System;
using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed class NetworkBufferTests
    {
        [Test]
        public void AllocationReservesParentAndChildAndClearsFullCapacityOnRelease()
        {
            var parent = new FrameMemoryBudget(128 + FrameMemoryBudget.FrameRecordReservation);
            var child = new FrameMemoryBudget(64 + FrameMemoryBudget.FrameRecordReservation, parent);
            OwnedFrame frame = child.Allocate(64);
            byte[] bytes = frame.Bytes;
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = 0xa5;
            }

            frame.Length = 32;
            Assert.AreEqual(64 + FrameMemoryBudget.FrameRecordReservation, parent.OwnedBytes);
            Assert.AreEqual(64 + FrameMemoryBudget.FrameRecordReservation, child.OwnedBytes);
            Assert.Throws<NetworkBackpressureException>(() => child.Allocate(1));
            frame.Dispose();
            frame.Dispose();
            CollectionAssert.AreEqual(new byte[64], bytes);
            Assert.AreEqual(0, parent.OwnedBytes);
            Assert.AreEqual(0, child.OwnedBytes);
        }

        [Test]
        public void FailedParentReservationDoesNotLeakChildCapacity()
        {
            var parent = new FrameMemoryBudget(32);
            var child = new FrameMemoryBudget(64 + FrameMemoryBudget.FrameRecordReservation, parent);
            Assert.Throws<NetworkBackpressureException>(() => child.Allocate(64));
            Assert.AreEqual(0, child.OwnedBytes);
            Assert.AreEqual(0, parent.OwnedBytes);
        }

        [Test]
        public void WriterRejectsOversizeBeforeExposingMemory()
        {
            var budget = new FrameMemoryBudget(64 + FrameMemoryBudget.FrameRecordReservation);
            using OwnedFrame frame = budget.Allocate(64);
            var writer = new BoundedFrameWriter(frame);
            Assert.Throws<NetworkProtocolException>(() => writer.GetMemory(33));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetMemory(-1));
            writer.GetMemory(32).Span.Fill(1);
            writer.Advance(32);
            Assert.Throws<NetworkProtocolException>(() => writer.Advance(1));
        }
    }
}
