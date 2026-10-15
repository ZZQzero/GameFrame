using System;
using System.Text;
using UnityEngine;

namespace GameFrame.Diagnostics
{
    /// <summary>线程安全的有界日志缓存；与 Unity 生命周期、UI 和剪切板无关。</summary>
    public sealed class RuntimeLogBuffer
    {
        readonly object gate = new object();
        readonly RuntimeLogEntry[] entries;
        readonly int maxCharacters;
        readonly int maxEntryCharacters;
        int start;
        int count;
        int characters;
        long sequence;
        long version;
        long dropped;

        public long Version
        {
            get
            {
                lock (gate)
                {
                    return version;
                }
            }
        }

        public RuntimeLogBuffer(int capacity = 1000, int maxCharacters = 2097152, int maxEntryCharacters = 65536)
        {
            if (capacity <= 0 || maxCharacters <= 0 || maxEntryCharacters <= 0 || maxEntryCharacters > maxCharacters)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Log limits must be positive; an entry must fit in the buffer.");
            }

            entries = new RuntimeLogEntry[capacity];
            this.maxCharacters = maxCharacters;
            this.maxEntryCharacters = maxEntryCharacters;
        }

        public void Add(string message, string stackTrace, LogType type)
        {
            message ??= string.Empty;
            stackTrace ??= string.Empty;
            bool truncated = (long)message.Length + stackTrace.Length > maxEntryCharacters;
            if (message.Length > maxEntryCharacters)
            {
                message = message.Substring(0, maxEntryCharacters);
            }
            if (stackTrace.Length > maxEntryCharacters - message.Length)
            {
                stackTrace = stackTrace.Substring(0, maxEntryCharacters - message.Length);
            }

            lock (gate)
            {
                var entry = new RuntimeLogEntry(++sequence, type, message, stackTrace, truncated);
                while (count == entries.Length || characters > maxCharacters - entry.CharacterCount)
                {
                    characters -= entries[start].CharacterCount;
                    entries[start] = null;
                    start = (start + 1) % entries.Length;
                    count--;
                    dropped++;
                }

                entries[(start + count) % entries.Length] = entry;
                count++;
                characters += entry.CharacterCount;
                version++;
            }
        }

        public RuntimeLogEntry[] Snapshot(out long snapshotVersion, out long droppedCount)
        {
            lock (gate)
            {
                var snapshot = new RuntimeLogEntry[count];
                for (int i = 0; i < count; i++)
                {
                    snapshot[i] = entries[(start + i) % entries.Length];
                }
                snapshotVersion = version;
                droppedCount = dropped;
                return snapshot;
            }
        }

        public string FormatAll()
        {
            var snapshot = Snapshot(out _, out long droppedCount);
            var text = new StringBuilder();
            if (droppedCount > 0)
            {
                text.AppendLine($"[Buffer limit: {droppedCount} older entries discarded]");
            }
            foreach (var entry in snapshot)
            {
                text.AppendLine(entry.ToString()).AppendLine();
            }
            return text.ToString();
        }

        public void Clear()
        {
            lock (gate)
            {
                Array.Clear(entries, 0, entries.Length);
                start = 0;
                count = 0;
                characters = 0;
                dropped = 0;
                version++;
            }
        }
    }
}
