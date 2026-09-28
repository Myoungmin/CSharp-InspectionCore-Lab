namespace Inspection.Core;

public interface IInspectionDiagnostics
{
    // Implementations must return promptly and must not wait for the observed run.
    void Record(InspectionDiagnostic diagnostic);
}

public sealed record InspectionDiagnostic(string EventName, Guid RunId, string JobId,
    InspectionStage? Stage = null, InspectionRunSnapshot? Outcome = null, Guid? AutoId = null);
