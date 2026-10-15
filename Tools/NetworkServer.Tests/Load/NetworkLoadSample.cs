using System.Numerics;
using GameFrame.Network;

namespace GameFrame.Tests
{
    internal sealed record NetworkLoadOptions
    {
        public string Scenario { get; init; } = "echo";
        public string Transport { get; init; } = "wss";
        public string NetworkProfile { get; init; } = "direct";
        public int NetworkSeed { get; init; } = 1;
        public int SlowHandlerMilliseconds { get; init; }
        public int BurstSize { get; init; }
        public int BurstIntervalMilliseconds { get; init; }
        public string CloseMode { get; init; } = "";
        public int CloseRequestsPerConnection { get; init; }
        public int Connections { get; init; } = 4;
        public int RatePerConnection { get; init; } = 20;
        public int WarmupSeconds { get; init; } = 2;
        public int DurationSeconds { get; init; } = 10;
        public int PayloadBytes { get; init; } = 128;
        public int InflightPerConnection { get; init; } = 8;
        public int RequestTimeoutSeconds { get; init; } = 3;
        public int PumpIntervalMilliseconds { get; init; } = 1;

        internal void Validate()
        {
            if ((Transport != "wss" && Transport != "kcp") ||
                (Transport == "kcp" && Connections > 64))
            {
                throw new ArgumentException("KCP load supports at most 64 connections.");
            }

            if (NetworkSeed < 1 || (Transport != "kcp" && NetworkProfile != "direct"))
            {
                throw new ArgumentException("Network profiles require KCP and a positive seed.");
            }

            KcpImpairmentPlan.For(NetworkProfile);

            if ((Scenario != "echo" && Scenario != "slow-handler" && Scenario != "idle" && Scenario != "burst" && Scenario != "close-storm") ||
                (Scenario != "slow-handler" && Scenario != "burst" && SlowHandlerMilliseconds != 0) ||
                (Scenario == "idle" && (RatePerConnection != 0 || DurationSeconds < 6)) ||
                (Scenario == "burst" && (RatePerConnection != 0 || BurstSize < 1 || BurstSize > 1024 ||
                    BurstIntervalMilliseconds < 1 || BurstIntervalMilliseconds > 60000 ||
                    SlowHandlerMilliseconds < 0 || SlowHandlerMilliseconds > 30000)) ||
                (Scenario != "burst" && (BurstSize != 0 || BurstIntervalMilliseconds != 0)) ||
                (Scenario == "slow-handler" && (Connections < 2 || SlowHandlerMilliseconds < 1 || SlowHandlerMilliseconds > 30000)) ||
                (Scenario == "close-storm" && ((CloseMode != "client" && CloseMode != "server") ||
                    CloseRequestsPerConnection < 0 || CloseRequestsPerConnection > 128 || CloseRequestsPerConnection > InflightPerConnection)) ||
                (Scenario != "close-storm" && (CloseMode.Length != 0 || CloseRequestsPerConnection != 0)))
            {
                throw new ArgumentException("Invalid scenario options; see Tools/network_load.py --help.");
            }

            if (Connections < 1 || Connections > 128 || RatePerConnection < (Scenario == "idle" || Scenario == "burst" ? 0 : 1) || RatePerConnection > 10000 ||
                WarmupSeconds < 0 || WarmupSeconds > 300 || DurationSeconds < 1 || DurationSeconds > 3600 ||
                PayloadBytes < 16 || PayloadBytes > 128 || InflightPerConnection < 1 || InflightPerConnection > 1024 ||
                Connections * InflightPerConnection > 8192 || RequestTimeoutSeconds < 1 || RequestTimeoutSeconds > 30 ||
                PumpIntervalMilliseconds < 1 || PumpIntervalMilliseconds > 100)
            {
                throw new ArgumentException("Invalid network load parameters; see Tools/network_load.py --help.");
            }
        }

        internal int RequestsPerSlot => Scenario == "burst" ? BurstSize : 1;

        internal long PlannedSlots(int seconds) => Scenario == "burst"
            ? ((long)seconds * 1000 + BurstIntervalMilliseconds - 1) / BurstIntervalMilliseconds
            : (long)seconds * RatePerConnection;
    }

    // 32 buckets per power of two, in microseconds: bounded storage, <=3.125% bucket width.
    internal sealed class LoadLatency
    {
        private readonly long[] buckets = new long[64 * 32];
        public long Count { get; private set; }
        public double MaximumMs { get; private set; }
        private double total;
        public double? AverageMs => Count == 0 ? null : total / Count;
        public double? P50UpperMs => Quantile(0.50);
        public double? P95UpperMs => Quantile(0.95);
        public double? P99UpperMs => Quantile(0.99);

        internal void Record(double milliseconds)
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 86400000)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            }

            double microseconds = Math.Max(1, milliseconds * 1000);
            int exponent = BitOperations.Log2((ulong)microseconds);
            double scale = Math.Pow(2, exponent);
            int fraction = Math.Min(31, (int)((microseconds / scale - 1) * 32));
            buckets[exponent * 32 + fraction]++;
            Count++;
            total += milliseconds;
            MaximumMs = Math.Max(MaximumMs, milliseconds);
        }

        internal double? Quantile(double fraction)
        {
            if (fraction <= 0 || fraction > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fraction));
            }

            if (Count == 0)
            {
                return null;
            }

            long target = (long)Math.Ceiling(Count * fraction);
            long seen = 0;
            for (int index = 0; index < buckets.Length; index++)
            {
                seen += buckets[index];
                if (seen >= target)
                {
                    return Math.Pow(2, index / 32) * (1 + (index % 32 + 1) / 32.0) / 1000;
                }
            }

            throw new InvalidOperationException("Latency histogram count mismatch.");
        }

        internal void Merge(LoadLatency other)
        {
            for (int index = 0; index < buckets.Length; index++)
            {
                buckets[index] += other.buckets[index];
            }

            Count += other.Count;
            total += other.total;
            MaximumMs = Math.Max(MaximumMs, other.MaximumMs);
        }
    }

    internal class NetworkLoadTraffic
    {
        public long Planned { get; internal set; }
        public long Attempted { get; internal set; }
        public long GeneratorLateSkipped { get; internal set; }
        public long InflightLimitSkipped { get; internal set; }
        public long Succeeded { get; internal set; }
        public long Backpressure { get; internal set; }
        public long TimedOut { get; internal set; }
        public long Canceled { get; internal set; }
        public long Failed { get; internal set; }
        public long Mismatched { get; internal set; }
        public long Unresolved => Attempted - Succeeded - Backpressure - TimedOut - Canceled - Failed;
        public double OfferSeconds { get; internal set; }
        public double DrainSeconds { get; internal set; }
        public long CompletedDuringOffer { get; internal set; }
        public double SuccessfulRps => OfferSeconds + DrainSeconds == 0 ? 0 : Succeeded / (OfferSeconds + DrainSeconds);
        public double CompletedDuringOfferRps => OfferSeconds == 0 ? 0 : CompletedDuringOffer / OfferSeconds;
        public LoadLatency SuccessLatency { get; } = new LoadLatency();
        public LoadLatency FailureLatency { get; } = new LoadLatency();
        public int PendingSampledPeak { get; internal set; }
        public int SendQueueSampledPeak { get; internal set; }
        public int ReceiveQueueSampledPeak { get; internal set; }
        public virtual bool Healthy => Planned == Attempted + GeneratorLateSkipped + InflightLimitSkipped &&
            Unresolved == 0 && Succeeded == Attempted && GeneratorLateSkipped == 0 && InflightLimitSkipped == 0;

        // Slots are time based, never completion based. Old slots are counted, not replayed in a burst.
        internal static long DueSlots(double elapsedSeconds, int rate, long planned) =>
            Math.Min(planned, Math.Max(0, (long)Math.Floor(elapsedSeconds * rate) + 1));

        internal static long DueBursts(double elapsedSeconds, int intervalMilliseconds, long planned) =>
            Math.Min(planned, Math.Max(0, (long)Math.Floor(elapsedSeconds * 1000 / intervalMilliseconds) + 1));

        internal void RecordFailure(Exception error, double milliseconds)
        {
            FailureLatency.Record(milliseconds);
            if (error is NetworkBackpressureException)
            {
                Backpressure++;
            }
            else if (error is NetworkRequestTimeoutException)
            {
                TimedOut++;
            }
            else if (error is OperationCanceledException)
            {
                Canceled++;
            }
            else
            {
                Failed++;
            }
        }

        internal void MergeOutcomes(NetworkLoadTraffic other)
        {
            Planned += other.Planned;
            Attempted += other.Attempted;
            GeneratorLateSkipped += other.GeneratorLateSkipped;
            InflightLimitSkipped += other.InflightLimitSkipped;
            Succeeded += other.Succeeded;
            Backpressure += other.Backpressure;
            TimedOut += other.TimedOut;
            Canceled += other.Canceled;
            Failed += other.Failed;
            Mismatched += other.Mismatched;
            CompletedDuringOffer += other.CompletedDuringOffer;
            SuccessLatency.Merge(other.SuccessLatency);
            FailureLatency.Merge(other.FailureLatency);
        }
    }

    internal sealed class NetworkLoadSample : NetworkLoadTraffic
    {
        public override bool Healthy => base.Healthy && (Idle?.Healthy ?? true) && (Kcp?.Healthy ?? true);
        public NetworkLoadTraffic Normal { get; } = new NetworkLoadTraffic();
        public NetworkLoadTraffic? Slow { get; internal set; }
        public NetworkIdleSample? Idle { get; internal set; }
        public NetworkKcpSample? Kcp { get; internal set; }
        public LoadLatency? BurstClientIssueLatency { get; internal set; }
        public long ClientOwnedBytesSampledPeak { get; internal set; }
        public long ServerOwnedBytesSampledPeak { get; internal set; }
        public double CpuSeconds { get; internal set; }
        public long AllocatedBytes { get; internal set; }
        public int[] GcCollections { get; internal set; } = new int[3];
        public long WorkingSetSampledPeak { get; internal set; }
        public long WorkingSetStart { get; internal set; }
        public long WorkingSetEnd { get; internal set; }
        public double ElapsedSeconds { get; internal set; }
        public double CpuCorePercent => ElapsedSeconds == 0 ? 0 : CpuSeconds / ElapsedSeconds * 100;
        public double AllocatedBytesPerSecond => ElapsedSeconds == 0 ? 0 : AllocatedBytes / ElapsedSeconds;
    }
}
