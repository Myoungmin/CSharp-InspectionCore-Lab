namespace Inspection.Core;

// A dependency cannot be reused safely after its termination protocol failed.
public sealed class InspectionTerminationException : Exception
{
    public InspectionTerminationException(bool terminationConfirmed, Exception innerException)
        : base(terminationConfirmed
            ? "Dependency stop failed; work has ended, but a new engine is required."
            : "Dependency termination is unconfirmed; resources are retained until process exit.", innerException)
    {
        TerminationConfirmed = terminationConfirmed;
    }

    public bool TerminationConfirmed { get; }
}
