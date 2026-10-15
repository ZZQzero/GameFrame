using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace GameFrame.Editor
{
    public static class NetworkAllocationTraceAnalysis
    {
        [Serializable]
        private sealed class Allocation
        {
            public string Thread;
            public long Size;
            public string Stack;
            public string FirstManagedMethod;
            public long Count;
            public long Bytes;
        }

        [Serializable]
        private sealed class Report
        {
            public int FirstFrame;
            public int LastFrame;
            public double WindowMilliseconds;
            public long CalibrationBytes;
            public long CalibrationWithStackBytes;
            public long Bytes;
            public long Count;
            public long WithoutStackBytes;
            public long UnresolvedStackBytes;
            public long WithoutManagedFrameBytes;
            public Allocation[] Allocations;
        }

        // Loading a raw profile replaces the Editor's profiler history; preserve it independently.
        public static void Analyze(string rawPath, string reportPath)
        {
            if (ProfilerDriver.enabled || UnityEngine.Profiling.Profiler.enabled)
            {
                throw new InvalidOperationException("Stop Profiler recording before analyzing a trace.");
            }
            if (!File.Exists(rawPath) || File.Exists(reportPath))
            {
                throw new IOException("Require an existing trace and a new report path.");
            }
            string backup = Path.Combine(Path.GetTempPath(), "gameframe-profiler-" + Guid.NewGuid().ToString("N") + ".data");
            bool hasHistory = ProfilerDriver.firstFrameIndex >= 0;
            if (hasHistory && !ProfilerDriver.SaveProfile(backup))
            {
                throw new IOException("Could not preserve the existing profiler history.");
            }
            try
            {
                if (!ProfilerDriver.LoadProfile(rawPath, false))
                {
                    throw new IOException("Could not load profiler trace.");
                }
                File.WriteAllText(reportPath, JsonUtility.ToJson(Read(), true));
            }
            finally
            {
                if (hasHistory)
                {
                    if (!ProfilerDriver.LoadProfile(backup, false))
                    {
                        throw new IOException("Could not restore profiler history; backup retained at " + backup);
                    }
                    File.Delete(backup);
                }
                else
                {
                    ProfilerDriver.ClearAllFrames();
                }
            }
        }

        private static Report Read()
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last - first >= 4096)
            {
                throw new InvalidOperationException("Empty or oversized trace.");
            }
            var result = new Report { FirstFrame = first, LastFrame = last };
            double begin = -1;
            double end = -1;
            var stack = new List<ulong>();
            for (int frame = first; frame <= last; frame++)
            {
                using var view = ProfilerDriver.GetRawFrameDataView(frame, 0);
                if (!view.valid)
                {
                    throw new InvalidOperationException("Missing main-thread frame data.");
                }
                for (int sample = 0; sample < view.sampleCount; sample++)
                {
                    string name = view.GetSampleName(sample);
                    if (name == "GameFrame.Network.MeasurementBegin")
                    {
                        if (begin >= 0)
                        {
                            throw new InvalidOperationException("Duplicate measurement begin marker.");
                        }
                        begin = view.GetSampleStartTimeMs(sample);
                    }
                    else if (name == "GameFrame.Network.MeasurementEnd")
                    {
                        if (end >= 0)
                        {
                            throw new InvalidOperationException("Duplicate measurement end marker.");
                        }
                        end = view.GetSampleStartTimeMs(sample);
                    }
                    else if (name == "GameFrame.Network.TraceCalibration")
                    {
                        int stop = sample + view.GetSampleChildrenCountRecursive(sample);
                        for (int child = sample + 1; child <= stop; child++)
                        {
                            if (view.GetSampleName(child) == "GC.Alloc")
                            {
                                long size = view.GetSampleMetadataAsLong(child, 0);
                                result.CalibrationBytes += size;
                                stack.Clear();
                                view.GetSampleCallstack(child, stack);
                                if (stack.Count > 0)
                                {
                                    result.CalibrationWithStackBytes += size;
                                }
                            }
                        }
                    }
                }
            }
            if (end <= begin || begin < 0 || result.CalibrationWithStackBytes < 1024 * 1024)
            {
                throw new InvalidOperationException("Missing measurement markers or known-allocation callstack calibration failed.");
            }
            result.WindowMilliseconds = end - begin;
            var groups = new Dictionary<string, Allocation>();
            var symbols = new Dictionary<ulong, string>();
            for (int frame = first; frame <= last; frame++)
            {
                for (int thread = 0; thread < 256; thread++)
                {
                    using var view = ProfilerDriver.GetRawFrameDataView(frame, thread);
                    if (!view.valid)
                    {
                        break;
                    }
                    for (int sample = 0; sample < view.sampleCount; sample++)
                    {
                        double time = view.GetSampleStartTimeMs(sample);
                        if (time < begin || time >= end || view.GetSampleName(sample) != "GC.Alloc")
                        {
                            continue;
                        }
                        long size = view.GetSampleMetadataAsLong(sample, 0);
                        stack.Clear();
                        view.GetSampleCallstack(sample, stack);
                        bool unresolved = false;
                        string callstack = string.Join("\n", stack.Select(address =>
                        {
                            if (!symbols.TryGetValue(address, out string method))
                            {
                                method = view.ResolveMethodInfo(address).methodName;
                                symbols[address] = string.IsNullOrEmpty(method) ? "0x" + address.ToString("x") : method;
                            }
                            unresolved |= symbols[address].StartsWith("0x", StringComparison.Ordinal);
                            return symbols[address];
                        }));
                        string firstManaged = stack.Select(address => symbols[address])
                            .FirstOrDefault(method => method.Contains(".dll!"));
                        string threadName = view.threadGroupName + "/" + view.threadName;
                        string key = threadName + "\n" + size + "\n" + callstack;
                        if (!groups.TryGetValue(key, out Allocation group))
                        {
                            if (groups.Count >= 65536)
                            {
                                throw new InvalidOperationException("Allocation group capacity exceeded.");
                            }
                            group = new Allocation { Thread = threadName, Size = size, Stack = callstack,
                                FirstManagedMethod = firstManaged };
                            groups.Add(key, group);
                        }
                        group.Count++;
                        group.Bytes += size;
                        result.Count++;
                        result.Bytes += size;
                        result.WithoutStackBytes += stack.Count == 0 ? size : 0;
                        result.UnresolvedStackBytes += unresolved ? size : 0;
                        result.WithoutManagedFrameBytes += firstManaged == null ? size : 0;
                    }
                    if (thread == 255)
                    {
                        throw new InvalidOperationException("Profiler thread capacity exceeded.");
                    }
                }
            }
            result.Allocations = groups.Values.OrderByDescending(value => value.Bytes).ToArray();
            return result;
        }
    }
}
