namespace Inspection.Core;

public enum InspectionExecutionMode { Manual, Automatic }
public enum InspectionAutoState { Running, Waiting, Stopping, Completed, Stopped, Faulted }
public enum InspectionAutoStopReason { RunLimitReached, Requested, RunCanceled, RunTimedOut, RunFaulted, Shutdown, SchedulingFailed }
public enum InspectionAutoStopDisposition { Accepted, AlreadyRequested, TooLate, NotFound }

public sealed record InspectionAutoSnapshot(
    Guid AutoId,
    string JobId,
    InspectionAutoState State,
    InspectionAutoStopReason? StopReason,
    long StartedRuns,
    long CompletedRuns,
    Guid? CurrentRunId,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? NextRunAtUtc,
    InspectionRunSnapshot? LastRun,
    Exception? Error);

public sealed record InspectionAutoStartResult(
    InspectionStartDisposition Disposition, InspectionAutoHandle? Auto, Guid? BusyRunId, Guid? BusyAutoId);

public sealed class InspectionAutoHandle
{
    internal InspectionAutoHandle(Guid autoId, Task<InspectionAutoSnapshot> completion)
    {
        AutoId = autoId;
        Completion = completion;
    }

    public Guid AutoId { get; }
    public Task<InspectionAutoSnapshot> Completion { get; }
}
