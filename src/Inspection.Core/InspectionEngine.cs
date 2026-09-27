namespace Inspection.Core;

public sealed class InspectionEngine : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly InspectionRunner _runner;
    private readonly TimeProvider _timeProvider;
    private InspectionEngineState _state = InspectionEngineState.Ready;
    private ActiveRun? _active;
    private InspectionRunSnapshot? _last;
    private Exception? _error;
    private Task? _disposeTask;

    public InspectionEngine(InspectionRunner runner, TimeProvider? timeProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public InspectionStartResult Start(InspectionJob job, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (timeout is { } duration && duration != Timeout.InfiniteTimeSpan &&
            (duration < TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1d))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_gate)
        {
            if (_state != InspectionEngineState.Ready) { return new(InspectionStartDisposition.Unavailable, null, null); }
            if (_active is not null) { return new(InspectionStartDisposition.Busy, null, _active.RunId); }

            var run = new ActiveRun(job, _timeProvider.GetUtcNow());
            _active = run;
            try
            {
                if (timeout == TimeSpan.Zero)
                {
                    RequestStopLocked(run, InspectionStopReason.Timeout);
                }
                else if (timeout is { } dueTime && dueTime != Timeout.InfiniteTimeSpan)
                {
                    run.Timer = _timeProvider.CreateTimer(_ =>
                    {
                        lock (_gate) { RequestStopLocked(run, InspectionStopReason.Timeout); }
                    }, null, dueTime, Timeout.InfiniteTimeSpan);
                }

                // The slot is already reserved. Even a synchronous inspector cannot block admission.
                _ = Task.Run(() => ExecuteAsync(run));
                return new(InspectionStartDisposition.Accepted, run.Handle, null);
            }
            catch
            {
                _active = null;
                run.Timer?.Dispose();
                run.Cancellation.Dispose();
                throw;
            }
        }
    }

    public InspectionEngineSnapshot GetStatus()
    {
        lock (_gate) { return new(_state, _active is not null, _active?.Snapshot() ?? _last, _error); }
    }

    public InspectionRunSnapshot? GetRun(Guid runId)
    {
        lock (_gate)
        {
            if (_active?.RunId == runId) { return _active.Snapshot(); }
            return _last?.RunId == runId ? _last : null;
        }
    }

    public InspectionCancelDisposition CancelRun(Guid runId)
    {
        lock (_gate)
        {
            if (_active?.RunId == runId) { return RequestStopLocked(_active, InspectionStopReason.UserCancellation); }
            return _last?.RunId == runId ? InspectionCancelDisposition.TooLate : InspectionCancelDisposition.NotFound;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) { return new(_disposeTask); }
            if (_state != InspectionEngineState.Faulted) { _state = InspectionEngineState.Stopping; }
            if (_active is not null) { RequestStopLocked(_active, InspectionStopReason.Shutdown); }
            _disposeTask = FinishDisposalAsync(_active?.Completion.Task ?? Task.CompletedTask);
            return new(_disposeTask);
        }
    }

    private async Task FinishDisposalAsync(Task completion)
    {
        await completion.ConfigureAwait(false);
        lock (_gate) { if (_state != InspectionEngineState.Faulted) { _state = InspectionEngineState.Disposed; } }
    }

    // All callers hold _gate, including persistence entry and completion arbitration.
    private InspectionCancelDisposition RequestStopLocked(ActiveRun run, InspectionStopReason reason)
    {
        if (_active != run || run.Finishing || run.State == InspectionRunState.Persisting) { return InspectionCancelDisposition.TooLate; }
        if (run.StopReason is not null) { return InspectionCancelDisposition.AlreadyRequested; }
        run.StopReason = reason;
        run.State = InspectionRunState.CancelRequested;
        // CancelAsync sets the token synchronously and invokes registered callbacks asynchronously.
        run.CancellationDelivery = run.Cancellation.CancelAsync();
        return InspectionCancelDisposition.Accepted;
    }

    private void EnterPersistence(ActiveRun run, InspectionResult result)
    {
        lock (_gate)
        {
            run.ComputedResult = result;
            if (run.StopReason is not null) { throw new OperationCanceledException(run.Cancellation.Token); }
            run.State = InspectionRunState.Persisting;
        }
    }

    private async Task ExecuteAsync(ActiveRun run)
    {
        InspectionResult? result = null;
        Exception? error = null;
        try
        {
            result = await _runner.RunAsync(run.Job, run.RunId, computed => EnterPersistence(run, computed),
                stage => { lock (_gate) { run.Stage = stage; } }, run.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { error = exception; }

        Task delivery;
        lock (_gate)
        {
            // No later stop request can replace the outcome of work that has already returned.
            run.Finishing = true;
            delivery = run.CancellationDelivery;
        }

        Exception? stopError = null;
        try { await delivery.ConfigureAwait(false); }
        catch (Exception exception) { stopError = exception; }
        try { if (run.Timer is not null) { await run.Timer.DisposeAsync().ConfigureAwait(false); } }
        catch (Exception exception) { stopError = stopError is null ? exception : new AggregateException(stopError, exception); }
        run.Cancellation.Dispose();

        lock (_gate)
        {
            run.Error = error;
            run.StopError = stopError;
            run.ComputedResult = result ?? (error as InspectionRunException)?.ComputedResult ?? run.ComputedResult;
            run.CompletedAtUtc = _timeProvider.GetUtcNow();
            var terminationError = (error as InspectionRunException)?.InnerException as InspectionTerminationException;
            run.State = stopError is not null || terminationError is not null ? InspectionRunState.Faulted
                : run.StopReason == InspectionStopReason.Timeout ? InspectionRunState.TimedOut
                : run.StopReason is not null ? InspectionRunState.Canceled
                : error is not null ? InspectionRunState.Faulted : InspectionRunState.Succeeded;
            if (stopError is not null || terminationError is not null)
            {
                _error = stopError ?? terminationError;
                _state = InspectionEngineState.Faulted;
            }
            _last = run.Snapshot();
            _active = null;
            run.Completion.SetResult(_last);
        }
    }

    private sealed class ActiveRun
    {
        internal ActiveRun(InspectionJob job, DateTimeOffset acceptedAtUtc)
        {
            Job = job;
            AcceptedAtUtc = acceptedAtUtc;
            Handle = new(RunId, job.JobId, Completion.Task);
        }

        internal Guid RunId { get; } = Guid.NewGuid();
        internal InspectionJob Job { get; }
        internal DateTimeOffset AcceptedAtUtc { get; }
        internal InspectionRunHandle Handle { get; }
        internal TaskCompletionSource<InspectionRunSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task CancellationDelivery { get; set; } = Task.CompletedTask;
        internal ITimer? Timer { get; set; }
        internal bool Finishing { get; set; }
        internal InspectionRunState State { get; set; } = InspectionRunState.Running;
        internal InspectionStage? Stage { get; set; }
        internal InspectionStopReason? StopReason { get; set; }
        internal DateTimeOffset? CompletedAtUtc { get; set; }
        internal InspectionResult? ComputedResult { get; set; }
        internal Exception? Error { get; set; }
        internal Exception? StopError { get; set; }

        internal InspectionRunSnapshot Snapshot() => new(RunId, Job.JobId, State, Stage, StopReason,
            AcceptedAtUtc, CompletedAtUtc, ComputedResult, Error, StopError);
    }
}
