using System;

namespace GameFrame.Diagnostics
{
    public sealed class RuntimeLogEntry
    {
        public long Id { get; }
        public DateTime Time { get; }
        public UnityEngine.LogType Type { get; }
        public string Message { get; }
        public string StackTrace { get; }
        public bool IsTruncated { get; }

        internal int CharacterCount => Message.Length + StackTrace.Length;

        internal RuntimeLogEntry(long id, UnityEngine.LogType type, string message, string stackTrace, bool truncated)
        {
            Id = id;
            Time = DateTime.Now;
            Type = type;
            Message = message;
            StackTrace = stackTrace;
            IsTruncated = truncated;
        }

        public override string ToString()
        {
            return $"[{Time:yyyy-MM-dd HH:mm:ss.fff}] [{Type}] {Message}" +
                (StackTrace.Length == 0 ? string.Empty : "\n" + StackTrace) +
                (IsTruncated ? "\n[Truncated: entry exceeded the configured character limit]" : string.Empty);
        }
    }
}
