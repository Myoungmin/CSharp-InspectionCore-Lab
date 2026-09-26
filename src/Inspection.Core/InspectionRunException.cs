namespace Inspection.Core;

public enum InspectionStage
{
    Prepare,
    Acquire,
    Inspect,
    Persist
}

public sealed class InspectionRunException : Exception
{
    public InspectionRunException(Guid runId, InspectionStage stage, InspectionResult? computedResult, Exception innerException)
        : base($"Inspection run {runId} failed during {stage}.", innerException)
    {
        RunId = runId;
        Stage = stage;
        ComputedResult = computedResult;
    }

    public Guid RunId { get; }
    public InspectionStage Stage { get; }
    public InspectionResult? ComputedResult { get; }
}

public sealed class InspectionCanceledException : OperationCanceledException
{
    public InspectionCanceledException(Guid runId, InspectionStage stage, OperationCanceledException innerException, CancellationToken token)
        : base($"Inspection run {runId} was canceled during {stage}.", innerException, token)
    {
        RunId = runId;
        Stage = stage;
    }

    public Guid RunId { get; }
    public InspectionStage Stage { get; }
}
