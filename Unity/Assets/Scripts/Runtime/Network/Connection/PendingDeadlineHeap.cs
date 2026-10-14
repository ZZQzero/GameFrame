#nullable disable
using System;
using System.Collections.Generic;

namespace GameFrame.Network
{
    internal abstract class PendingRequest
    {
        public ulong Id;
        public uint Opcode;
        public long Deadline;
        public long EncodingTicks;
        public long AdmittedTimestamp;
        public long ResponseTimestamp;
        public int HeapIndex = -1;
        public int State; // 0 waiting, 1 response reserved, 2 terminal, 3 completed
        public Exception Error;
        public System.Threading.CancellationTokenRegistration Registration;
        public System.Threading.CancellationToken CancellationToken;
        public abstract int ResponseLimit { get; }
        public abstract void Complete(OwnedFrame response);
    }

    internal sealed class PendingDeadlineHeap
    {
        private readonly List<PendingRequest> entries;
        public int Count => entries.Count;
        public PendingRequest First => entries.Count == 0 ? null : entries[0];

        public PendingDeadlineHeap(int capacity)
        {
            entries = new List<PendingRequest>(capacity);
        }

        public void Add(PendingRequest entry)
        {
            entry.HeapIndex = entries.Count;
            entries.Add(entry);
            Up(entry.HeapIndex);
        }

        public void Clear()
        {
            foreach (PendingRequest entry in entries)
            {
                entry.HeapIndex = -1;
            }

            entries.Clear();
        }

        public void Remove(PendingRequest entry)
        {
            int index = entry.HeapIndex;
            if (index < 0)
            {
                return;
            }

            int last = entries.Count - 1;
            PendingRequest replacement = entries[last];
            Swap(index, last);
            entries.RemoveAt(last);
            entry.HeapIndex = -1;
            if (index < entries.Count)
            {
                Up(index);
                Down(replacement.HeapIndex);
            }
        }

        private void Up(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (entries[parent].Deadline <= entries[index].Deadline)
                {
                    break;
                }

                Swap(index, parent);
                index = parent;
            }
        }

        private void Down(int index)
        {
            while (index * 2 + 1 < entries.Count)
            {
                int child = index * 2 + 1;
                if (child + 1 < entries.Count && entries[child + 1].Deadline < entries[child].Deadline)
                {
                    child++;
                }

                if (entries[index].Deadline <= entries[child].Deadline)
                {
                    break;
                }

                Swap(index, child);
                index = child;
            }
        }

        private void Swap(int a, int b)
        {
            PendingRequest value = entries[a];
            entries[a] = entries[b];
            entries[b] = value;
            entries[a].HeapIndex = a;
            entries[b].HeapIndex = b;
        }
    }
}
