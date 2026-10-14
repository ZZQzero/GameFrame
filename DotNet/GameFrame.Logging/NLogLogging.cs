using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Common;
using NLog.Extensions.Logging;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace GameFrame.Logging
{
    public sealed class NLogLogging : IAsyncDisposable
    {
        private readonly LogFactory logFactory;
        private readonly TimeSpan flushTimeout;
        private readonly Meter meter = new Meter("GameFrame.Logging.NLog");
        private readonly object shutdownGate = new object();
        private Task? shutdown;
        private readonly OutputDiagnostics diagnostics;

        public ILoggerFactory Factory { get; }
        public long DroppedCount => Interlocked.Read(ref diagnostics.Dropped);
        public long OutputErrorCount => Interlocked.Read(ref diagnostics.Errors);

        private NLogLogging(LogFactory logFactory, ILoggerFactory factory, AsyncTargetWrapper output,
            OutputDiagnostics diagnostics, TimeSpan flushTimeout)
        {
            this.logFactory = logFactory;
            Factory = factory;
            this.flushTimeout = flushTimeout;
            this.diagnostics = diagnostics;
            output.LogEventDropped += (_, _) => Interlocked.Increment(ref diagnostics.Dropped);
            meter.CreateObservableCounter("gameframe.logging.dropped", () => DroppedCount, "{event}",
                "Events discarded because the asynchronous output queue is full.");
            meter.CreateObservableCounter("gameframe.logging.output.errors", () => OutputErrorCount, "{error}",
                "Errors reported by the output target.");
        }

        public static NLogLogging Create(LoggingOptions options, NLogOutputOptions? outputOptions = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            NLogOutputOptions output = outputOptions ?? new NLogOutputOptions();
            output.Validate();
            return Create(options, output, new ConsoleTarget("console"));
        }

        // The factory owns and disposes the target; it must not be shared with another factory.
        public static NLogLogging Create(LoggingOptions options, NLogOutputOptions outputOptions, TargetWithLayout target)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(outputOptions);
            ArgumentNullException.ThrowIfNull(target);
            options.Validate();
            outputOptions.Validate();
            target.Layout = CreateLayout(options, outputOptions.Format);
            var diagnostics = new OutputDiagnostics();
            var observed = new ObservedTarget(target, diagnostics);
            var output = new AsyncTargetWrapper(observed, outputOptions.QueueCapacity, AsyncTargetWrapperOverflowAction.Discard)
            {
                Name = "async-output",
                BatchSize = outputOptions.BatchSize,
                TimeToSleepBetweenBatches = 1
            };
            var logFactory = new LogFactory { AutoShutdown = false, ThrowConfigExceptions = true };
            try
            {
                var configuration = new LoggingConfiguration(logFactory);
                configuration.AddRule(global::NLog.LogLevel.Trace, global::NLog.LogLevel.Fatal, output);
                logFactory.Configuration = configuration;
                ILoggerFactory factory = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(options.MinimumLevel);
                    foreach (KeyValuePair<string, Microsoft.Extensions.Logging.LogLevel> entry in options.CategoryLevels)
                    {
                        builder.AddFilter(entry.Key, options.MinimumLevel > entry.Value ? options.MinimumLevel : entry.Value);
                    }
                    builder.AddProvider(new NLogLoggerProvider(new NLogProviderOptions
                    {
                        CaptureMessageTemplates = true,
                        CaptureMessageProperties = true,
                        IncludeScopes = true,
                        RemoveLoggerFactoryFilter = false,
                        ShutdownOnDispose = false,
                        AutoShutdown = false
                    }, logFactory));
                });
                return new NLogLogging(logFactory, factory, output, diagnostics, outputOptions.FlushTimeout);
            }
            catch
            {
                logFactory.Dispose();
                throw;
            }
        }

        private static Layout CreateLayout(LoggingOptions options, NLogOutputFormat format)
        {
            var properties = new JsonLayout { IncludeEventProperties = true, IncludeScopeProperties = true };
            if (format == NLogOutputFormat.Text)
            {
                return new CompoundLayout
                {
                    Layouts =
                    {
                        new SimpleLayout("${date:universalTime=true:format=o} ${level:uppercase=true} "),
                        Layout.FromLiteral($"{options.Service} {options.Environment} {options.InstanceId} {options.Version} "),
                        new SimpleLayout("${logger} ${message} "),
                        properties,
                        new SimpleLayout(" ${exception:format=tostring}")
                    }
                };
            }

            properties.Attributes.Add(new JsonAttribute("Timestamp", "${date:universalTime=true:format=o}"));
            properties.Attributes.Add(new JsonAttribute("Level", "${level}"));
            properties.Attributes.Add(new JsonAttribute("Category", "${logger}"));
            properties.Attributes.Add(new JsonAttribute("Message", "${message}"));
            properties.Attributes.Add(new JsonAttribute("MessageTemplate", "${message:raw=true}"));
            properties.Attributes.Add(new JsonAttribute("Service", Layout.FromLiteral(options.Service)));
            properties.Attributes.Add(new JsonAttribute("Environment", Layout.FromLiteral(options.Environment)));
            properties.Attributes.Add(new JsonAttribute("InstanceId", Layout.FromLiteral(options.InstanceId)));
            properties.Attributes.Add(new JsonAttribute("Version", Layout.FromLiteral(options.Version)));
            properties.Attributes.Add(new JsonAttribute("Exception", "${exception:format=tostring}"));
            return properties;
        }

        public ValueTask DisposeAsync()
        {
            lock (shutdownGate)
            {
                shutdown ??= ShutdownAsync();
                return new ValueTask(shutdown);
            }
        }

        private async Task ShutdownAsync()
        {
            try
            {
                var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                logFactory.Flush(error =>
                {
                    if (error == null)
                    {
                        flushed.TrySetResult();
                    }
                    else
                    {
                        flushed.TrySetException(error);
                    }
                }, flushTimeout);
                await flushed.Task.ConfigureAwait(false);
                if (diagnostics.FirstError != null)
                {
                    ExceptionDispatchInfo.Capture(diagnostics.FirstError).Throw();
                }
            }
            finally
            {
                try
                {
                    Factory.Dispose();
                }
                finally
                {
                    try
                    {
                        logFactory.Dispose();
                    }
                    finally
                    {
                        meter.Dispose();
                    }
                }
            }
        }

        private sealed class OutputDiagnostics
        {
            internal long Dropped;
            internal long Errors;
            internal Exception? FirstError;
        }

        // Observes target completion without adding a queue, retries, or log recursion.
        private sealed class ObservedTarget : WrapperTargetBase
        {
            private readonly OutputDiagnostics diagnostics;
            private readonly Target target;

            internal ObservedTarget(Target target, OutputDiagnostics diagnostics)
            {
                WrappedTarget = target;
                this.target = target;
                this.diagnostics = diagnostics;
            }

            protected override void Write(AsyncLogEventInfo logEvent)
            {
                target.WriteAsyncLogEvent(Observe(logEvent));
            }

            protected override void Write(IList<AsyncLogEventInfo> logEvents)
            {
                var observed = new AsyncLogEventInfo[logEvents.Count];
                for (int i = 0; i < logEvents.Count; i++)
                {
                    observed[i] = Observe(logEvents[i]);
                }

                target.WriteAsyncLogEvents(observed);
            }

            private AsyncLogEventInfo Observe(AsyncLogEventInfo logEvent)
            {
                return new AsyncLogEventInfo(logEvent.LogEvent, error =>
                {
                    if (error != null)
                    {
                        Interlocked.Increment(ref diagnostics.Errors);
                        Interlocked.CompareExchange(ref diagnostics.FirstError, error, null);
                    }

                    logEvent.Continuation(error);
                });
            }
        }
    }
}
