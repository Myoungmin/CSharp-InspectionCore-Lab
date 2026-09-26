using Inspection.Core;

namespace Inspection.Infrastructure;

public sealed class RangeInspector : IInspector
{
    public Task<InspectionAssessment> InspectAsync(
        InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.IsEmpty)
        {
            throw new ArgumentException("Acquired samples cannot be empty.", nameof(samples));
        }

        int defectCount = 0;
        foreach (double value in samples.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(value))
            {
                throw new ArgumentException("Acquired samples must be finite.", nameof(samples));
            }

            if (value < job.LowerBound || value > job.UpperBound)
            {
                defectCount++;
            }
        }

        return Task.FromResult(new InspectionAssessment(samples.Length, defectCount));
    }
}
