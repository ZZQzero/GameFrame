using System;
using UnityEngine;

namespace GameFrame.Diagnostics
{
    public enum RuntimeConsoleMode
    {
        Disabled,
        EditorAndDevelopment,
        Always
    }

    /// <summary>由启动宿主持有生命周期，采集与任何日志界面的显示状态无关。</summary>
    public static class RuntimeLogCapture
    {
        static Application.LogCallback callback;

        public static RuntimeLogBuffer Buffer { get; private set; }
        public static bool IsCapturing => callback != null;

        public static void Initialize(RuntimeConsoleMode mode = RuntimeConsoleMode.Always,
            int capacity = 1000, int maxCharacters = 2097152, int maxEntryCharacters = 65536)
        {
            if (Buffer != null)
            {
                throw new InvalidOperationException("Runtime log capture is already initialized.");
            }

            var buffer = new RuntimeLogBuffer(capacity, maxCharacters, maxEntryCharacters);
            Buffer = buffer;
            if (mode == RuntimeConsoleMode.Always ||
                (mode == RuntimeConsoleMode.EditorAndDevelopment && (Application.isEditor || Debug.isDebugBuild)))
            {
                // 捕获具体缓存，退订时已在执行的后台回调仍可安全完成。
                callback = (message, stack, type) => buffer.Add(message, stack, type);
                Application.logMessageReceivedThreaded += callback;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Shutdown()
        {
            Application.logMessageReceivedThreaded -= callback;
            callback = null;
            Buffer = null;
        }
    }
}
