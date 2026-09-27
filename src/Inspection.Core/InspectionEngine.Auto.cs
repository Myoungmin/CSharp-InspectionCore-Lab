namespace Inspection.Core;

public sealed partial class InspectionEngine
{
    private ActiveAuto? _auto;
    private InspectionAutoSnapshot? _lastAuto;

    public InspectionAutoStartResult StartAuto(InspectionJob job, TimeSpan interval, TimeSpan? timeout = null, int? maxRuns = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateTimeout(timeout);
        if (interval < TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1d) { throw new ArgumentOutOfRangeException(nameof(interval)); }
        if (maxRuns is <= 0) { throw new ArgumentOutOfRangeException(nameof(maxRuns)); }
        lock (_gate)
        {
            if (_state != InspectionEngineState.Ready) { return new(InspectionStartDisposition.Unavailable, null, null, null); }
            if (_active is not null || _auto is not null) { return new(InspectionStartDisposition.Busy, null, _active?.RunId, _auto?.AutoId); }
            var session = new ActiveAuto(job, interval, timeout, maxRuns, _timeProvider.GetUtcNow());
            _auto = session;
            try
            {
                InspectionRunHandle first = StartAutoRunLocked(session);
                // The first run cannot complete under this admission lock; the loop first awaits its completion.
                _ = ExecuteAutoAsync(session, first);
                return new(InspectionStartDisposition.Accepted, session.Handle, null, null);
            }
            catch
            {
                _auto = null;
                session.ScheduleStop.Dispose();
                throw;
            }
        }
    }

    public InspectionAutoSnapshot? GetAuto(Guid autoId)
    {
        lock (_gate)
        {
            if (_auto?.AutoId == autoId) { return _auto.Snapshot(); }
            return _lastAuto?.AutoId == autoId ? _lastAuto : null;
        }
    }

    public InspectionAutoStopDisposition StopAuto(Guid autoId)
    {
        lock (_gate)
        {
            if (_auto?.AutoId == autoId) { return StopAutoLocked(_auto, InspectionAutoStopReason.Requested); }
            return _lastAuto?.AutoId == autoId ? InspectionAutoStopDisposition.TooLate : InspectionAutoStopDisposition.NotFound;
        }
    }

    private InspectionAutoStopDisposition StopAutoLocked(ActiveAuto session, InspectionAutoStopReason reason)
    {
        if (session.Finishing || session.State is InspectionAutoState.Completed or InspectionAutoState.Stopped or InspectionAutoState.Faulted)
        {
            return InspectionAutoStopDisposition.TooLate;
        }
        if (session.StopRequested) { return InspectionAutoStopDisposition.AlreadyRequested; }
        session.StopRequested = true;
        session.State = InspectionAutoState.Stopping;
        session.StopReason = reason;
        session.NextRunAtUtc = null;
        // This token belongs only to the schedule, never to a running inspection.
        session.StopDelivery = session.ScheduleStop.CancelAsync();
        return InspectionAutoStopDisposition.Accepted;
    }

    private InspectionRunHandle StartAutoRunLocked(ActiveAuto session)
    {
        InspectionRunHandle run = StartRunLocked(session.Job, session.Timeout).Handle;
        session.StartedRuns++;
        session.CurrentRunId = run.RunId;
        session.NextRunAtUtc = null;
        session.State = InspectionAutoState.Running;
        return run;
    }

    private async Task ExecuteAutoAsync(ActiveAuto session, InspectionRunHandle current)
    {
        try
        {
            while (true)
            {
                InspectionRunSnapshot result = await current.Completion.ConfigureAwait(false);
                Task delay;
                lock (_gate)
                {
                    session.CompletedRuns++;
                    session.CurrentRunId = null;
                    session.LastRun = result;
                    if (result.State != InspectionRunState.Succeeded)
                    {
                        session.State = result.State == InspectionRunState.Faulted ? InspectionAutoState.Faulted : InspectionAutoState.Stopped;
                        session.StopReason = result.State switch
                        {
                            InspectionRunState.Canceled => InspectionAutoStopReason.RunCanceled,
                            InspectionRunState.TimedOut => InspectionAutoStopReason.RunTimedOut,
                            _ => InspectionAutoStopReason.RunFaulted
                        };
                        session.Error = result.StopError ?? result.Error;
                        break;
                    }
                    if (session.StopRequested) { session.State = InspectionAutoState.Stopped; break; }
                    if (session.MaxRuns is { } maximum && session.CompletedRuns >= maximum)
                    {
                        session.State = InspectionAutoState.Completed;
                        session.StopReason = InspectionAutoStopReason.RunLimitReached;
                        break;
                    }
                    // The session still reserves admission while no inspection is active.
                    delay = Task.Delay(session.Interval, _timeProvider, session.ScheduleStop.Token);
                    session.NextRunAtUtc = _timeProvider.GetUtcNow() + session.Interval;
                    session.State = InspectionAutoState.Waiting;
                }
                await delay.ConfigureAwait(false);
                lock (_gate)
                {
                    // StopAuto and the next admission linearize on the same lock.
                    if (session.StopRequested) { session.State = InspectionAutoState.Stopped; break; }
                    current = StartAutoRunLocked(session);
                }
            }
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == session.ScheduleStop.Token && session.ScheduleStop.IsCancellationRequested)
        {
            lock (_gate) { session.State = InspectionAutoState.Stopped; }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                session.State = InspectionAutoState.Faulted;
                session.StopReason = InspectionAutoStopReason.SchedulingFailed;
                session.Error = exception;
            }
        }
        finally
        {
            Task delivery;
            lock (_gate)
            {
                session.Finishing = true;
                if (!session.ScheduleStop.IsCancellationRequested) { session.StopDelivery = session.ScheduleStop.CancelAsync(); }
                delivery = session.StopDelivery;
            }
            try { await delivery.ConfigureAwait(false); }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    session.State = InspectionAutoState.Faulted;
                    session.StopReason = InspectionAutoStopReason.SchedulingFailed;
                    session.Error = exception;
                    _error = exception;
                    _state = InspectionEngineState.Faulted;
                }
            }
            session.ScheduleStop.Dispose();
            lock (_gate)
            {
                session.CompletedAtUtc = _timeProvider.GetUtcNow();
                session.NextRunAtUtc = null;
                _lastAuto = session.Snapshot();
                _auto = null;
                session.Completion.SetResult(_lastAuto);
            }
        }
    }

    private sealed class ActiveAuto
    {
        internal ActiveAuto(InspectionJob job, TimeSpan interval, TimeSpan? timeout, int? maxRuns, DateTimeOffset acceptedAtUtc)
        {
            Job = job;
            Interval = interval;
            Timeout = timeout;
            MaxRuns = maxRuns;
            AcceptedAtUtc = acceptedAtUtc;
            Handle = new(AutoId, Completion.Task);
        }

        internal Guid AutoId { get; } = Guid.NewGuid();
        internal InspectionJob Job { get; }
        internal TimeSpan Interval { get; }
        internal TimeSpan? Timeout { get; }
        internal int? MaxRuns { get; }
        internal DateTimeOffset AcceptedAtUtc { get; }
        internal InspectionAutoHandle Handle { get; }
        internal TaskCompletionSource<InspectionAutoSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource ScheduleStop { get; } = new();
        internal Task StopDelivery { get; set; } = Task.CompletedTask;
        internal bool StopRequested { get; set; }
        internal bool Finishing { get; set; }
        internal InspectionAutoState State { get; set; } = InspectionAutoState.Running;
        internal InspectionAutoStopReason? StopReason { get; set; }
        internal long StartedRuns { get; set; }
        internal long CompletedRuns { get; set; }
        internal Guid? CurrentRunId { get; set; }
        internal DateTimeOffset? CompletedAtUtc { get; set; }
        internal DateTimeOffset? NextRunAtUtc { get; set; }
        internal InspectionRunSnapshot? LastRun { get; set; }
        internal Exception? Error { get; set; }

        internal InspectionAutoSnapshot Snapshot() => new(AutoId, Job.JobId, State, StopReason, StartedRuns, CompletedRuns,
            CurrentRunId, AcceptedAtUtc, CompletedAtUtc, NextRunAtUtc, LastRun, Error);
    }
}
