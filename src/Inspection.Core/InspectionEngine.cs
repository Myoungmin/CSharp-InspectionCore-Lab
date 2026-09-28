namespace Inspection.Core;

public sealed partial class InspectionEngine : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly InspectionRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly IInspectionDiagnostics? _diagnostics;
    private InspectionEngineState _state = InspectionEngineState.Ready;
    private ActiveRun? _active;
    private InspectionRunSnapshot? _last;
    private Exception? _error;
    private Task? _disposeTask;

    public InspectionEngine(InspectionRunner runner, TimeProvider? timeProvider = null, IInspectionDiagnostics? diagnostics = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _diagnostics = diagnostics;
    }

    public InspectionStartResult Start(InspectionJob job, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateTimeout(timeout);
        lock (_gate)
        {
            if (_state != InspectionEngineState.Ready) { return new(InspectionStartDisposition.Unavailable, null, null); }
            if (_active is not null || _auto is not null) { return new(InspectionStartDisposition.Busy, null, _active?.RunId, _auto?.AutoId); }
            return new(InspectionStartDisposition.Accepted, StartRunLocked(job, timeout).Handle, null);
        }
    }

    private static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is { } duration && duration != Timeout.InfiniteTimeSpan &&
            (duration < TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1d))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    private ActiveRun StartRunLocked(InspectionJob job, TimeSpan? timeout)
    {
        var run = new ActiveRun(job, _timeProvider.GetUtcNow(), _auto?.AutoId);
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
            return run;
        }
        catch
        {
            _active = null;
            run.Timer?.Dispose();
            run.Cancellation.Dispose();
            throw;
        }
    }

    public InspectionEngineSnapshot GetStatus()
    {
        lock (_gate)
        {
            return new(_state, _active is not null, _active?.Snapshot() ?? _last, _error)
            {
                Mode = _auto is null ? InspectionExecutionMode.Manual : InspectionExecutionMode.Automatic,
                Auto = _auto?.Snapshot() ?? _lastAuto
            };
        }
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
            if (_auto is not null) { StopAutoLocked(_auto, InspectionAutoStopReason.Shutdown); }
            if (_active is not null) { RequestStopLocked(_active, InspectionStopReason.Shutdown); }
            _disposeTask = FinishDisposalAsync(Task.WhenAll(
                _active?.Completion.Task ?? Task.CompletedTask, _auto?.Completion.Task ?? Task.CompletedTask));
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
        Record(new("RunStarted", run.RunId, run.Job.JobId, AutoId: run.AutoId));
        InspectionResult? result = null;
        Exception? error = null;
        try
        {
            result = await _runner.RunAsync(run.Job, run.RunId, computed => EnterPersistence(run, computed),
                stage =>
                {
                    lock (_gate) { run.Stage = stage; }
                    Record(new("StageEntered", run.RunId, run.Job.JobId, stage, AutoId: run.AutoId));
                }, run.Cancellation.Token).ConfigureAwait(false);
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

        InspectionRunSnapshot completed;
        Exception? healthError;
        lock (_gate)
        {
            InspectionResult? computed = result ?? (error as InspectionRunException)?.ComputedResult ?? run.ComputedResult;
            var terminationError = (error as InspectionRunException)?.InnerException as InspectionTerminationException;
            InspectionRunState state = stopError is not null || terminationError is not null ? InspectionRunState.Faulted
                : run.StopReason == InspectionStopReason.Timeout ? InspectionRunState.TimedOut
                : run.StopReason is not null ? InspectionRunState.Canceled
                : error is not null ? InspectionRunState.Faulted : InspectionRunState.Succeeded;
            healthError = stopError ?? terminationError;
            completed = new(run.RunId, run.Job.JobId, state, run.Stage, run.StopReason, run.AcceptedAtUtc,
                _timeProvider.GetUtcNow(), computed, error, stopError);
        }
        // Keep the reservation until diagnostics returns, outside the engine lock.
        // Completion and Dispose must not race disposal of the Host-owned sink.
        Record(new("RunCompleted", run.RunId, run.Job.JobId, run.Stage, completed, run.AutoId));
        lock (_gate)
        {
            if (healthError is not null) { _error = healthError; _state = InspectionEngineState.Faulted; }
            _last = completed;
            _active = null;
            run.Completion.SetResult(_last);
        }
    }

    private void Record(InspectionDiagnostic diagnostic)
    {
        try { _diagnostics?.Record(diagnostic); }
        catch (Exception) { /* Diagnostics must not replace the execution outcome. */ }
    }

    private sealed class ActiveRun
    {
        internal ActiveRun(InspectionJob job, DateTimeOffset acceptedAtUtc, Guid? autoId)
        {
            Job = job;
            AcceptedAtUtc = acceptedAtUtc;
            AutoId = autoId;
            Handle = new(RunId, job.JobId, Completion.Task);
        }

        internal Guid RunId { get; } = Guid.NewGuid();
        internal Guid? AutoId { get; }
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
        internal InspectionResult? ComputedResult { get; set; }

        internal InspectionRunSnapshot Snapshot() => new(RunId, Job.JobId, State, Stage, StopReason,
            AcceptedAtUtc, null, ComputedResult, null, null);
    }
}
