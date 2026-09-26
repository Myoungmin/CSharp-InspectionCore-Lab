using Inspection.Core;

namespace Inspection.Infrastructure;

public sealed class SimulatedDevice : IDevice
{
    public Task PrepareAsync(InspectionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<double[]> AcquireAsync(InspectionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(job.Samples.ToArray());
    }
}
