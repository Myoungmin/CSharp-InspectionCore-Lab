namespace Inspection.Core;

public enum InspectionEngineState { Ready, Stopping, Faulted, Disposed }
public enum InspectionRunState { Running, Persisting, CancelRequested, Succeeded, Canceled, TimedOut, Faulted }
public enum InspectionStopReason { UserCancellation, Timeout, Shutdown }
public enum InspectionStartDisposition { Accepted, Busy, Unavailable }
public enum InspectionCancelDisposition { Accepted, AlreadyRequested, TooLate, NotFound }

public sealed record InspectionRunSnapshot(
    Guid RunId,
    string JobId,
    InspectionRunState State,
    InspectionStage? Stage,
    InspectionStopReason? StopReason,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    InspectionResult? ComputedResult,
    Exception? Error,
    Exception? StopError)
{
    public bool? TerminationConfirmed => CompletedAtUtc is null ? null
        : ((Error as InspectionRunException)?.InnerException as InspectionTerminationException)?.TerminationConfirmed ?? true;
}

public sealed record InspectionEngineSnapshot(
    InspectionEngineState State, bool IsBusy, InspectionRunSnapshot? Run, Exception? Error);

public sealed record InspectionStartResult(
    InspectionStartDisposition Disposition, InspectionRunHandle? Run, Guid? BusyRunId);

public sealed class InspectionRunHandle
{
    internal InspectionRunHandle(Guid runId, string jobId, Task<InspectionRunSnapshot> completion)
    {
        RunId = runId;
        JobId = jobId;
        Completion = completion;
    }

    public Guid RunId { get; }
    public string JobId { get; }
    public Task<InspectionRunSnapshot> Completion { get; }
}
