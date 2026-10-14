using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Text.Json;
using GameFrame.Logging;
using Microsoft.Extensions.Logging;
using NLog.Common;
using NLog.Targets;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class LoggingTests
    {
        private static LoggingOptions Settings(string instance = "test") => new LoggingOptions
        {
            Service = "standalone-service",
            Environment = "Test",
            InstanceId = instance,
            Version = "1.0",
            CategoryLevels = new Dictionary<string, LogLevel> { ["Microsoft.AspNetCore"] = LogLevel.Warning }
        };

        [Test]
        public async Task JsonPreservesStructuredEventsAndConcurrentAsyncScopes()
        {
            var target = new MemoryTarget("capture");
            NLogLogging output = NLogLogging.Create(Settings(), new NLogOutputOptions { Format = NLogOutputFormat.Json }, target);
            ILogger logger = output.Factory.CreateLogger("Independent.Module");
            await Task.WhenAll(Enumerable.Range(1, 32).Select(async id =>
            {
                using IDisposable? scope = logger.BeginScope(new[] { new KeyValuePair<string, object>("ConnectionId", id) });
                await Task.Yield();
                logger.LogInformation(new EventId(1001, "RequestDone"), "Request {CorrelationId} complete.", id * 10);
            }));
            await output.DisposeAsync();
            Assert.That(target.Logs.Count, Is.EqualTo(32));
            foreach (string line in target.Logs)
            {
                using JsonDocument json = JsonDocument.Parse(line);
                JsonElement entry = json.RootElement;
                Assert.That(entry.GetProperty("CorrelationId").GetInt32(), Is.EqualTo(entry.GetProperty("ConnectionId").GetInt32() * 10));
                Assert.That(entry.GetProperty("Category").GetString(), Is.EqualTo("Independent.Module"));
                Assert.That(entry.GetProperty("Service").GetString(), Is.EqualTo("standalone-service"));
                Assert.That(entry.GetProperty("EventId").GetInt32(), Is.EqualTo(1001));
                Assert.That(entry.GetProperty("EventName").GetString(), Is.EqualTo("RequestDone"));
                Assert.That(entry.GetProperty("MessageTemplate").GetString(), Is.EqualTo("Request {CorrelationId} complete."));
            }
        }

        [Test]
        public async Task TextRendersPropertiesAndLiteralMetadata()
        {
            var target = new MemoryTarget("capture");
            NLogLogging output = NLogLogging.Create(Settings("${environment:SECRET}"), new NLogOutputOptions(), target);
            ILogger logger = output.Factory.CreateLogger("Example");
            using (logger.BeginScope(new[] { new KeyValuePair<string, object>("ConnectionId", 42) }))
            {
                logger.LogInformation("Value {Value}", 7);
            }

            await output.DisposeAsync();
            Assert.That(target.Logs.Single(), Does.Contain("${environment:SECRET}"));
            Assert.That(target.Logs.Single(), Does.Contain("\"ConnectionId\":42"));
            Assert.That(target.Logs.Single(), Does.Contain("Value 7"));
        }

        [Test]
        public async Task JsonEscapesMultilineExceptionsAndUsesUtcTimestamps()
        {
            var target = new MemoryTarget("capture");
            await using NLogLogging output = NLogLogging.Create(Settings(),
                new NLogOutputOptions { Format = NLogOutputFormat.Json }, target);
            var error = new InvalidOperationException("Injected\nouter failure", new IOException("Injected inner failure"));
            output.Factory.CreateLogger("Example").LogError(error, "Operation {Operation} failed", "load");
            await output.DisposeAsync();
            string line = target.Logs.Single();
            Assert.That(line, Does.Not.Contain("\n"));
            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement entry = json.RootElement;
            Assert.That(entry.GetProperty("Timestamp").GetDateTimeOffset().Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(entry.GetProperty("Exception").GetString(), Does.Contain("Injected\nouter failure"));
            Assert.That(entry.GetProperty("Exception").GetString(), Does.Contain("Injected inner failure"));
            Assert.That(entry.GetProperty("Operation").GetString(), Is.EqualTo("load"));
        }

        [Test]
        public async Task FiltersApplyBeforeDisabledLogsAndFactoriesHaveIndependentLifetimes()
        {
            var firstTarget = new MemoryTarget("first");
            var secondTarget = new MemoryTarget("second");
            NLogLogging first = NLogLogging.Create(Settings("first"), new NLogOutputOptions(), firstTarget);
            NLogLogging second = NLogLogging.Create(Settings("second"), new NLogOutputOptions(), secondTarget);
            ILogger logger = second.Factory.CreateLogger("Example");
            Assert.That(logger.IsEnabled(LogLevel.Debug), Is.False);
            Assert.That(second.Factory.CreateLogger("Microsoft.AspNetCore.Server").IsEnabled(LogLevel.Information), Is.False);
            await first.DisposeAsync();
            logger.LogDebug("filtered");
            logger.LogInformation("still active");
            Task shutdown = second.DisposeAsync().AsTask();
            Assert.That(second.DisposeAsync().AsTask(), Is.SameAs(shutdown));
            await shutdown;
            Assert.That(firstTarget.Logs, Is.Empty);
            Assert.That(secondTarget.Logs.Single(), Does.Contain("still active"));
        }

        [Test]
        public async Task FullQueueDiscardsWithoutWaitingForBlockedOutputAndReportsMetrics()
        {
            using var target = new BlockingTarget();
            NLogLogging output = NLogLogging.Create(Settings(), new NLogOutputOptions { QueueCapacity = 4, BatchSize = 1 }, target);
            long measuredDrops = -1;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "GameFrame.Logging.NLog" && instrument.Name == "gameframe.logging.dropped")
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => measuredDrops = value);
            listener.Start();
            try
            {
                ILogger logger = output.Factory.CreateLogger("Example");
                logger.LogInformation("block output");
                await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(() =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        logger.LogInformation("Event {Index}", i);
                    }
                }).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(output.DroppedCount, Is.GreaterThan(0));
                listener.RecordObservableInstruments();
                Assert.That(measuredDrops, Is.EqualTo(output.DroppedCount));
            }
            finally
            {
                target.Release.Set();
                await output.DisposeAsync();
            }
        }

        [Test]
        public void FlushFailureIsObservableAndRepeatedShutdownDoesNotRetry()
        {
            var target = new FailingFlushTarget();
            NLogLogging output = NLogLogging.Create(Settings(), new NLogOutputOptions(), target);
            output.Factory.CreateLogger("Example").LogInformation("accepted");
            Task shutdown = output.DisposeAsync().AsTask();
            Assert.ThrowsAsync<IOException>(async () => await shutdown);
            Assert.That(output.DisposeAsync().AsTask(), Is.SameAs(shutdown));
        }

        [Test]
        public void OutputFailureIsCountedAndReportedAtShutdown()
        {
            NLogLogging output = NLogLogging.Create(Settings(), new NLogOutputOptions(), new FailingWriteTarget());
            output.Factory.CreateLogger("Example").LogError("output fails");
            Assert.ThrowsAsync<IOException>(async () => await output.DisposeAsync());
            Assert.That(output.OutputErrorCount, Is.EqualTo(1));
        }

        [Test]
        public async Task FlushTimeoutIsObservableWhenTheTargetDoesNotComplete()
        {
            NLogLogging output = NLogLogging.Create(Settings(),
                new NLogOutputOptions { FlushTimeout = TimeSpan.FromMilliseconds(100) }, new IncompleteFlushTarget());
            output.Factory.CreateLogger("Example").LogInformation("accepted");
            Task shutdown = output.DisposeAsync().AsTask();
            Exception? failure = null;
            try
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error)
            {
                failure = error;
            }

            Assert.That(shutdown.IsCompleted, Is.True, "The adapter must report its flush timeout.");
            Assert.That(failure, Is.Not.Null);
            Assert.That(shutdown.IsFaulted, Is.True);
            Assert.That(output.DisposeAsync().AsTask(), Is.SameAs(shutdown));
        }

        [Test]
        public async Task DisabledGeneratedEventHasNoPerCallAllocationAfterWarmup()
        {
            var target = new MemoryTarget("capture");
            await using NLogLogging output = NLogLogging.Create(Settings(), new NLogOutputOptions(), target);
            ILogger logger = output.Factory.CreateLogger("Example");
            for (int i = 0; i < 1000; i++)
            {
                PerformanceLog.Disabled(logger, i);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < 100000; i++)
            {
                PerformanceLog.Disabled(logger, i);
            }

            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            TestContext.Progress.WriteLine($"100000 disabled generated events: {elapsed:F3} ms, {allocated} allocated bytes.");
            Assert.That(allocated, Is.Zero);
            Assert.That(target.Logs, Is.Empty);
        }

        [Test]
        public async Task NoneDisablesInfrastructureCategoriesAsWell()
        {
            var target = new MemoryTarget("capture");
            await using NLogLogging output = NLogLogging.Create(Settings() with { MinimumLevel = LogLevel.None }, new NLogOutputOptions(), target);
            Assert.That(output.Factory.CreateLogger("Microsoft.AspNetCore.Server").IsEnabled(LogLevel.Critical), Is.False);
        }

        [TestCase(0)]
        [TestCase(65537)]
        public void RejectsInvalidCapacityBeforeStartingOutput(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NLogLogging.Create(Settings(), new NLogOutputOptions { QueueCapacity = capacity }));
        }

        [Test]
        public void RejectsMetadataControlCharacters()
        {
            Assert.Throws<ArgumentException>(() => NLogLogging.Create(Settings("injected\nline")));
        }

        private sealed class BlockingTarget : TargetWithLayout
        {
            internal readonly TaskCompletionSource Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();

            protected override void Write(global::NLog.LogEventInfo logEvent)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Test output was not released.");
                }
            }
        }

        private sealed class FailingFlushTarget : TargetWithLayout
        {
            protected override void Write(global::NLog.LogEventInfo logEvent)
            {
            }

            protected override void FlushAsync(AsyncContinuation continuation) => continuation(new IOException("Injected flush failure."));
        }

        private sealed class FailingWriteTarget : TargetWithLayout
        {
            protected override void Write(global::NLog.LogEventInfo logEvent) => throw new IOException("Injected output failure.");
        }

        private sealed class IncompleteFlushTarget : TargetWithLayout
        {
            protected override void Write(global::NLog.LogEventInfo logEvent)
            {
            }

            protected override void FlushAsync(AsyncContinuation continuation)
            {
                // Simulates an asynchronous target that never acknowledges its flush.
            }
        }
    }

    internal static partial class PerformanceLog
    {
        [LoggerMessage(1, LogLevel.Debug, "Disabled event {Index}")]
        internal static partial void Disabled(ILogger logger, int index);
    }
}
