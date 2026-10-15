using System.Linq;
using System.Threading.Tasks;
using GameFrame.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace GameFrame.Tests
{
    public sealed class RuntimeLogBufferTests
    {
        [Test]
        public void CapacityAndCharacterBudgetKeepNewestEntriesInOrder()
        {
            var buffer = new RuntimeLogBuffer(3, 10, 10);
            buffer.Add("1111", "", LogType.Log);
            buffer.Add("2222", "", LogType.Warning);
            buffer.Add("3333", "", LogType.Error);
            var snapshot = buffer.Snapshot(out _, out long dropped);
            CollectionAssert.AreEqual(new[] { "2222", "3333" }, snapshot.Select(entry => entry.Message));
            Assert.AreEqual(1, dropped);

            for (int i = 0; i < 4; i++)
            {
                buffer.Add(i.ToString(), "", LogType.Log);
            }
            snapshot = buffer.Snapshot(out _, out dropped);
            CollectionAssert.AreEqual(new[] { "1", "2", "3" }, snapshot.Select(entry => entry.Message));
            Assert.AreEqual(4, dropped);
        }

        [Test]
        public void OversizedEntryIsExplicitlyMarkedAndCannotExceedBudget()
        {
            var buffer = new RuntimeLogBuffer(2, 12, 8);
            buffer.Add("message", "stack trace", LogType.Exception);
            var entry = buffer.Snapshot(out _, out _).Single();
            Assert.AreEqual(8, entry.Message.Length + entry.StackTrace.Length);
            Assert.IsTrue(entry.IsTruncated);
            StringAssert.Contains("Truncated", buffer.FormatAll());
            StringAssert.Contains("Exception", entry.ToString());
        }

        [Test]
        public void ClearDoesNotInvalidateSnapshotsOrReuseEntryIds()
        {
            var buffer = new RuntimeLogBuffer();
            buffer.Add("before", "first stack", LogType.Log);
            var before = buffer.Snapshot(out long version, out _).Single();
            buffer.Clear();
            Assert.IsEmpty(buffer.FormatAll());
            Assert.Greater(buffer.Version, version);
            buffer.Add("after", "second stack", LogType.Warning);
            var after = buffer.Snapshot(out _, out _).Single();
            Assert.Greater(after.Id, before.Id);
            StringAssert.Contains("first stack", before.ToString());
            StringAssert.DoesNotContain("before", buffer.FormatAll());
        }

        [Test]
        public void ConcurrentWritersAndSnapshotsRemainBoundedAndOrdered()
        {
            var buffer = new RuntimeLogBuffer(100, 4000, 40);
            Parallel.For(0, 4000, i =>
            {
                buffer.Add(i.ToString(), "worker", LogType.Log);
                if (i % 31 == 0)
                {
                    var snapshot = buffer.Snapshot(out _, out _);
                    Assert.LessOrEqual(snapshot.Length, 100);
                    Assert.IsTrue(snapshot.Zip(snapshot.Skip(1), (a, b) => a.Id < b.Id).All(value => value));
                }
            });
            var final = buffer.Snapshot(out _, out long dropped);
            Assert.AreEqual(100, final.Length);
            Assert.AreEqual(3900, dropped);
            Assert.AreEqual(4000, final.Last().Id);
        }
    }
}
