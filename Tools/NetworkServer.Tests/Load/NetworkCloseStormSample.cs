using GameFrame.Network;

namespace GameFrame.Tests
{
    internal sealed record NetworkCloseFault(string? StateErrorType, string? StateErrorStack,
        ushort? Reason, string? TaskErrorType, string? TaskErrorStack,
        int NativeState, int NativeFatalCode, int NativeError);

    internal sealed class NetworkCloseStormSample
    {
        public string Mode { get; init; } = "";
        public int Connections { get; init; }
        public long PlannedRequests { get; init; }
        public long AttemptedRequests { get; internal set; }
        public long ClosedRequests { get; private set; }
        public long UnexpectedResponses { get; private set; }
        public long Backpressure { get; private set; }
        public long TimedOut { get; private set; }
        public long Canceled { get; private set; }
        public long OtherFailures { get; private set; }
        public long UnexpectedFailures => UnexpectedResponses + Backpressure + TimedOut + Canceled + OtherFailures;
        public long Unresolved => AttemptedRequests - ClosedRequests - UnexpectedFailures;
        public int PendingAtClose { get; internal set; }
        public int HandlersEntered { get; internal set; }
        public int HandlersStarted { get; internal set; }
        public int HandlersFinished { get; internal set; }
        public int HandlersCanceled { get; internal set; }
        public bool Prepared { get; internal set; }
        public int CompletedClients { get; internal set; }
        public int CompletedServers { get; internal set; }
        public int UnexpectedClientFaults { get; internal set; }
        public List<NetworkCloseFault> UnexpectedClientFaultDetails { get; } = new List<NetworkCloseFault>();
        public int UnexpectedServerResults { get; internal set; }
        public bool CloseDeadlineExceeded { get; internal set; }
        public double PrepareSeconds { get; internal set; }
        public double IssueSeconds { get; internal set; }
        public double TotalSeconds { get; internal set; }
        public LoadLatency ClientCloseLatency { get; } = new LoadLatency();
        public LoadLatency ServerCloseLatency { get; } = new LoadLatency();
        public bool Healthy => Prepared && PlannedRequests == AttemptedRequests && PendingAtClose == PlannedRequests &&
            ClosedRequests == AttemptedRequests && Unresolved == 0 && UnexpectedFailures == 0 &&
            HandlersEntered == (PlannedRequests == 0 ? 0 : Connections) && HandlersStarted == HandlersFinished &&
            (Mode != "server" || (HandlersStarted == HandlersEntered && HandlersCanceled == HandlersEntered)) &&
            CompletedClients == Connections && CompletedServers == Connections && !CloseDeadlineExceeded &&
            UnexpectedClientFaults == 0 && UnexpectedServerResults == 0;

        internal void RecordOutcome(Exception? error, bool pendingAtClose)
        {
            // Client BeginClose uses the exact base type; server closure has a typed reason.
            // Only classify after the controlled close trigger, with separate connection result checks.
            if (pendingAtClose && ((Mode == "client" && error?.GetType() == typeof(NetworkException)) ||
                (Mode == "server" && error is NetworkConnectionClosedException closed && closed.ReasonCode == 3)))
            {
                ClosedRequests++;
            }
            else if (error == null)
            {
                UnexpectedResponses++;
            }
            else if (error is NetworkBackpressureException)
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
                OtherFailures++;
            }
        }
    }
}
