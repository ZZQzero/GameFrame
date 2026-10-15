using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class NetworkLoadStatisticsTests
    {
        [Test]
        public void HistogramIncludesTailAndHasBoundedQuantileError()
        {
            var latency = new LoadLatency();
            Assert.That(latency.P99UpperMs, Is.Null);
            for (int value = 1; value <= 100; value++)
            {
                latency.Record(value);
            }

            Assert.Multiple(() =>
            {
                Assert.That(latency.Count, Is.EqualTo(100));
                Assert.That(latency.AverageMs, Is.EqualTo(50.5));
                Assert.That(latency.P50UpperMs, Is.InRange(50, 50 * 1.03125));
                Assert.That(latency.P95UpperMs, Is.InRange(95, 95 * 1.03125));
                Assert.That(latency.P99UpperMs, Is.InRange(99, 99 * 1.03125));
                Assert.That(latency.MaximumMs, Is.EqualTo(100));
            });
        }

        [Test]
        public void SchedulingCountsOverdueSlotsWithoutDependingOnCompletions()
        {
            Assert.That(NetworkLoadSample.DueSlots(0, 20, 40), Is.EqualTo(1));
            Assert.That(NetworkLoadSample.DueSlots(0.24, 20, 40), Is.EqualTo(5));
            Assert.That(NetworkLoadSample.DueSlots(5, 20, 40), Is.EqualTo(40));
            var sample = new NetworkLoadSample { Planned = 5, Attempted = 1, Succeeded = 1, GeneratorLateSkipped = 4 };
            Assert.That(sample.Unresolved, Is.Zero);
            Assert.That(sample.Healthy, Is.False, "Skipped offered load must never be reported as a healthy faster run.");
        }

        [Test]
        public void FailureAccountingRetainsEveryOutcomeAndIncludesFailureLatency()
        {
            var sample = new NetworkLoadSample { Planned = 5, Attempted = 5, Succeeded = 1 };
            sample.RecordFailure(new NetworkBackpressureException("test"), 1);
            sample.RecordFailure(new NetworkRequestTimeoutException("test"), 100);
            sample.RecordFailure(new OperationCanceledException(), 2);
            sample.RecordFailure(new InvalidOperationException(), 3);
            Assert.Multiple(() =>
            {
                Assert.That(sample.Unresolved, Is.Zero);
                Assert.That(sample.Backpressure, Is.EqualTo(1));
                Assert.That(sample.TimedOut, Is.EqualTo(1));
                Assert.That(sample.Canceled, Is.EqualTo(1));
                Assert.That(sample.Failed, Is.EqualTo(1));
                Assert.That(sample.FailureLatency.Count, Is.EqualTo(4));
                Assert.That(sample.FailureLatency.MaximumMs, Is.EqualTo(100));
                Assert.That(sample.Healthy, Is.False);
            });
        }

        [TestCase(0, 8)]
        [TestCase(129, 8)]
        [TestCase(128, 128)]
        [TestCase(1, 1025)]
        public void InvalidConnectionOrInflightBudgetsAreRejected(int connections, int inflight)
        {
            Assert.Throws<ArgumentException>(() => new NetworkLoadOptions
            {
                Connections = connections, InflightPerConnection = inflight
            }.Validate());
        }

        [TestCase("other", "echo", 4)]
        [TestCase("kcp", "echo", 65)]
        public void UnsupportedLoadTransportCombinationsAreRejected(string transport, string scenario, int connections)
        {
            Assert.Throws<ArgumentException>(() => new NetworkLoadOptions
            {
                Transport = transport, Scenario = scenario, Connections = connections
            }.Validate());
        }

        [Test]
        public void AllocationHistogramUsesBytesAndKeepsZeroAndTailSamples()
        {
            var bytes = new NetworkLoadBytes();
            Assert.That(bytes.P95UpperBytes, Is.Null);
            bytes.Record(0);
            bytes.Record(1024);
            bytes.Record(4096);
            Assert.Multiple(() =>
            {
                Assert.That(bytes.Count, Is.EqualTo(3));
                Assert.That(bytes.AverageBytes, Is.EqualTo(5120.0 / 3).Within(0.001));
                Assert.That(bytes.MaximumBytes, Is.EqualTo(4096));
                Assert.That(bytes.P95UpperBytes, Is.InRange(4096, 4096 * 1.03125));
            });
        }

        [TestCase("wss", "loss", 1)]
        [TestCase("wss", "mixed", 1)]
        [TestCase("kcp", "unknown", 1)]
        [TestCase("kcp", "mixed", 0)]
        [TestCase("kcp", "delay", -1)]
        public void InvalidNetworkProfileOrSeedIsRejected(string transport, string profile, int seed)
        {
            Assert.Throws<ArgumentException>(() => new NetworkLoadOptions
            {
                Transport = transport, NetworkProfile = profile, NetworkSeed = seed
            }.Validate());
        }

        [Test]
        public void ImpairmentScheduleIsRepeatableAndRetainsDropAndDelayRules()
        {
            KcpImpairmentPlan plan = KcpImpairmentPlan.For("mixed");
            var first = new KcpPacketSchedule(plan, 42);
            var second = new KcpPacketSchedule(plan, 42);
            var different = new KcpPacketSchedule(plan, 43);
            int drops = 0, reordered = 0, changed = 0;
            var delays = new HashSet<int>();
            for (int index = 0; index < 200; index++)
            {
                var decision = first.Next();
                Assert.That(second.Next(), Is.EqualTo(decision));
                if (different.Next() != decision)
                {
                    changed++;
                }

                if (decision.Drop)
                {
                    drops++;
                }
                else
                {
                    Assert.That(decision.Delay, Is.InRange(20, 120));
                    delays.Add(decision.Delay);
                }

                if (decision.Reorder)
                {
                    reordered++;
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(drops, Is.EqualTo(10));
                Assert.That(reordered, Is.EqualTo(30));
                Assert.That(delays.Count, Is.GreaterThan(2));
                Assert.That(changed, Is.GreaterThan(0));
            });
        }

        [Test]
        public void RelayCannotPassWithoutExercisingItsProfileOrJoiningItsResources()
        {
            var sample = new KcpRelayReport
            {
                Plan = KcpImpairmentPlan.For("loss"), ReceivedPackets = 20, ImpairedPackets = 20,
                ForwardedPackets = 19, DroppedPackets = 1, Stopped = true, SocketsDisposed = true
            };
            Assert.That(sample.Healthy, Is.True);
            Assert.That((sample with { DroppedPackets = 0, ForwardedPackets = 20 }).Healthy, Is.False);
            Assert.That((sample with { QueuePackets = 1 }).Healthy, Is.False);
            Assert.That((sample with { Stopped = false }).Healthy, Is.False);
            Assert.That((sample with { SocketsDisposed = false }).Healthy, Is.False);
            Assert.That((sample with { QueueOverflowDrops = 1 }).Healthy, Is.False);
            Assert.That((sample with { ForwardedPackets = 18 }).Healthy, Is.False);
            var fault = sample with
            {
                Plan = KcpImpairmentPlan.For("direct"), ImpairedPackets = 0,
                BypassPackets = 20, ClientToServerFaultDrops = 1
            };
            Assert.That(fault.Healthy, Is.True);
            Assert.That(fault.ProfileExercised, Is.False, "Fault injection is separate from a sustained impairment profile.");
            Assert.That((fault with { ClientToServerFaultDrops = 0 }).Healthy, Is.False);
            Assert.That((fault with { QueueOverflowDrops = 1 }).Healthy, Is.False);
        }

        [Test]
        public void GroupMergeRetainsTailFailuresAndSkippedSlots()
        {
            var normal = new NetworkLoadTraffic { Planned = 99, Attempted = 99, Succeeded = 99 };
            for (int index = 0; index < 99; index++)
            {
                normal.SuccessLatency.Record(1);
            }

            var slow = new NetworkLoadTraffic { Planned = 3, Attempted = 2, Succeeded = 1, InflightLimitSkipped = 1 };
            slow.SuccessLatency.Record(100);
            slow.RecordFailure(new NetworkRequestTimeoutException("test"), 1000);
            var total = new NetworkLoadTraffic();
            total.MergeOutcomes(normal);
            total.MergeOutcomes(slow);
            Assert.Multiple(() =>
            {
                Assert.That(normal.Healthy, Is.True);
                Assert.That(slow.Healthy, Is.False);
                Assert.That(total.Planned, Is.EqualTo(102));
                Assert.That(total.Attempted, Is.EqualTo(101));
                Assert.That(total.Unresolved, Is.Zero);
                Assert.That(total.SuccessLatency.Count, Is.EqualTo(100));
                Assert.That(total.SuccessLatency.AverageMs, Is.EqualTo(1.99));
                Assert.That(total.SuccessLatency.MaximumMs, Is.EqualTo(100));
                Assert.That(total.SuccessLatency.Quantile(1), Is.InRange(100, 103.125));
                Assert.That(total.FailureLatency.Count, Is.EqualTo(1));
                Assert.That(total.FailureLatency.MaximumMs, Is.EqualTo(1000));
                Assert.That(total.Healthy, Is.False);
            });
        }

        [TestCase("slow-handler", 1, 25)]
        [TestCase("slow-handler", 2, 0)]
        [TestCase("slow-handler", 2, 30001)]
        [TestCase("echo", 2, 25)]
        [TestCase("other", 2, 0)]
        public void InvalidScenarioOrDelayIsRejected(string scenario, int connections, int delay)
        {
            Assert.Throws<ArgumentException>(() => new NetworkLoadOptions
            {
                Scenario = scenario, Connections = connections, SlowHandlerMilliseconds = delay
            }.Validate());
        }

        [TestCase("idle", 0, 5)]
        [TestCase("idle", 1, 6)]
        [TestCase("echo", 0, 6)]
        [TestCase("slow-handler", 0, 6)]
        public void InvalidIdleDurationOrRateIsRejected(string scenario, int rate, int duration)
        {
            Assert.Throws<ArgumentException>(() => new NetworkLoadOptions
            {
                Scenario = scenario, RatePerConnection = rate, DurationSeconds = duration,
                SlowHandlerMilliseconds = scenario == "slow-handler" ? 25 : 0
            }.Validate());
        }

        private static NetworkMetricsSnapshot IdleMetrics(long bytes, long receivedFrames = 0) =>
            new NetworkMetricsSnapshot(1, 0, 0, 0, 0, 0, 0, receivedFrames, 0, 0, 0, 0,
                sentApplicationBytes: bytes, receivedApplicationBytes: bytes);

        [Test]
        public void IdleSampleUsesCounterDeltasAndRetainsMemoryRange()
        {
            var idle = new NetworkIdleSample(new[] { IdleMetrics(80) }, true, 100, 200);
            idle.Record(new[] { IdleMetrics(120) }, 1, 1, 140, 240);
            Assert.That(idle.Healthy, Is.False, "One frame per direction does not establish Ping/Pong activity.");
            idle.Record(new[] { IdleMetrics(160) }, 1, 1, 110, 210);
            Assert.Multiple(() =>
            {
                Assert.That(idle.Healthy, Is.True);
                Assert.That(idle.SentApplicationBytes, Is.EqualTo(80));
                Assert.That(idle.ReceivedApplicationBytes, Is.EqualTo(80));
                Assert.That(idle.ClientOwnedBytesChange, Is.EqualTo(10));
                Assert.That(idle.ClientOwnedBytesSampledMinimum, Is.EqualTo(100));
                Assert.That(idle.ClientOwnedBytesSampledMaximum, Is.EqualTo(140));
                Assert.That(idle.ServerOwnedBytesChange, Is.EqualTo(10));
                Assert.That(idle.ServerOwnedBytesSampledMinimum, Is.EqualTo(200));
                Assert.That(idle.ServerOwnedBytesSampledMaximum, Is.EqualTo(240));
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void IdleCannotPassWithBusinessTrafficOrAConnectionLoss(bool businessTraffic)
        {
            var idle = new NetworkIdleSample(new[] { IdleMetrics(0) }, true, 0, 0);
            idle.Record(new[] { IdleMetrics(80, businessTraffic ? 1 : 0) }, businessTraffic ? 1 : 0, 1, 0, 0);
            idle.Record(new[] { IdleMetrics(160, businessTraffic ? 1 : 0) }, 1, 1, 0, 0);
            Assert.That(idle.Healthy, Is.False,
                "Empty RPC statistics must not hide business traffic or an earlier sampled disconnect.");
        }

        [TestCase(0, 333, 0)]
        [TestCase(1, 1000, 1)]
        [TestCase(1, 333, 4)]
        [TestCase(2, 1000, 2)]
        public void BurstPlanUsesHalfOpenWindowAndKeepsWholeBatchSize(int seconds, int interval, long expected)
        {
            var options = new NetworkLoadOptions
            {
                Scenario = "burst", RatePerConnection = 0, BurstSize = 8, BurstIntervalMilliseconds = interval
            };
            options.Validate();
            Assert.That(options.PlannedSlots(seconds), Is.EqualTo(expected));
            Assert.That(options.RequestsPerSlot, Is.EqualTo(8));
            Assert.That(NetworkLoadTraffic.DueBursts(seconds + 1, interval, expected), Is.EqualTo(expected));
        }

        [Test]
        public void BurstClockCountsMissedBatchesWithoutWaitingForResponses()
        {
            Assert.That(NetworkLoadTraffic.DueBursts(0, 250, 8), Is.EqualTo(1));
            Assert.That(NetworkLoadTraffic.DueBursts(0.25, 250, 8), Is.EqualTo(2));
            Assert.That(NetworkLoadTraffic.DueBursts(0.76, 250, 8), Is.EqualTo(4));
            Assert.That(NetworkLoadTraffic.DueBursts(2, 250, 8), Is.EqualTo(8));
        }

        [TestCase("burst", 0, 8, 1000, 0)]
        [TestCase("burst", 1025, 8, 1000, 0)]
        [TestCase("burst", 8, 8, 0, 0)]
        [TestCase("burst", 8, 8, 60001, 0)]
        [TestCase("burst", 8, 8, 1000, 1)]
        [TestCase("echo", 8, 8, 1000, 20)]
        public void InvalidBurstOptionsAreRejected(string scenario, int size, int inflight, int interval, int rate)
        {
            Assert.Throws<ArgumentException>(() => new NetworkLoadOptions
            {
                Scenario = scenario, BurstSize = size, InflightPerConnection = inflight,
                BurstIntervalMilliseconds = interval, RatePerConnection = rate
            }.Validate());
        }

        [TestCase("close-storm", "", 8, 8)]
        [TestCase("close-storm", "host", 8, 8)]
        [TestCase("close-storm", "client", -1, 8)]
        [TestCase("close-storm", "server", 129, 256)]
        [TestCase("close-storm", "client", 9, 8)]
        [TestCase("echo", "client", 0, 8)]
        [TestCase("burst", "", 1, 8)]
        public void InvalidCloseStormOptionsAreRejected(string scenario, string mode, int requests, int inflight)
        {
            Assert.Throws<ArgumentException>(() => new NetworkLoadOptions
            {
                Scenario = scenario, CloseMode = mode, CloseRequestsPerConnection = requests,
                InflightPerConnection = inflight, RatePerConnection = scenario == "burst" ? 0 : 20,
                BurstSize = scenario == "burst" ? 8 : 0, BurstIntervalMilliseconds = scenario == "burst" ? 1000 : 0
            }.Validate());
        }

        [TestCase("client")]
        [TestCase("server")]
        public void CloseStormOnlyAcceptsControlledPendingClosureAndRequiresJoinedResources(string mode)
        {
            Exception closed = mode == "client" ? new NetworkException("closed") : new NetworkConnectionClosedException(3);
            var sample = new NetworkCloseStormSample
            {
                Mode = mode, Connections = 1, PlannedRequests = 1, AttemptedRequests = 1,
                PendingAtClose = 1, HandlersEntered = 1, HandlersStarted = 1, HandlersFinished = 1,
                HandlersCanceled = mode == "server" ? 1 : 0, Prepared = true,
                CompletedClients = 1, CompletedServers = 1
            };
            sample.RecordOutcome(closed, pendingAtClose: true);
            Assert.That(sample.Healthy, Is.True);
            sample.CompletedServers = 0;
            Assert.That(sample.Healthy, Is.False, "Request closure alone does not prove the server released its resources.");

            var invalid = new NetworkCloseStormSample { Mode = mode, AttemptedRequests = 8 };
            invalid.RecordOutcome(null, pendingAtClose: true);
            invalid.RecordOutcome(new NetworkRequestTimeoutException("test"), pendingAtClose: true);
            invalid.RecordOutcome(new NetworkBackpressureException("test"), pendingAtClose: true);
            invalid.RecordOutcome(new OperationCanceledException(), pendingAtClose: true);
            invalid.RecordOutcome(new NetworkProtocolException("test"), pendingAtClose: true);
            invalid.RecordOutcome(new NetworkConnectionClosedException(7), pendingAtClose: true);
            invalid.RecordOutcome(new InvalidOperationException("test"), pendingAtClose: true);
            invalid.RecordOutcome(closed, pendingAtClose: false);
            Assert.Multiple(() =>
            {
                Assert.That(invalid.ClosedRequests, Is.Zero);
                Assert.That(invalid.UnexpectedFailures, Is.EqualTo(8));
                Assert.That(invalid.Unresolved, Is.Zero);
                Assert.That(invalid.Healthy, Is.False);
            });
        }
    }
}
