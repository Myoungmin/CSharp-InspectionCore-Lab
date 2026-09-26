namespace Inspection.Core;

public interface IDevice
{
    Task PrepareAsync(InspectionJob job, CancellationToken cancellationToken);
    Task<double[]> AcquireAsync(InspectionJob job, CancellationToken cancellationToken);
}

public interface IInspector
{
    Task<InspectionAssessment> InspectAsync(
        InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken cancellationToken);
}

public interface IResultStore
{
    Task SaveAsync(InspectionResult result, CancellationToken cancellationToken);
}
