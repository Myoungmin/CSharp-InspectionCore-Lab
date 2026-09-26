namespace Inspection.Core;

public sealed class InspectionJob
{
    public InspectionJob(string jobId, IEnumerable<double> samples, double lowerBound, double upperBound)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(lowerBound) || !double.IsFinite(upperBound) || lowerBound > upperBound)
        {
            throw new ArgumentOutOfRangeException(nameof(lowerBound), "Bounds must be finite and ordered.");
        }

        double[] snapshot = samples.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("At least one finite sample is required; all samples must be finite.", nameof(samples));
        }

        JobId = jobId;
        Samples = Array.AsReadOnly(snapshot);
        LowerBound = lowerBound;
        UpperBound = upperBound;
    }

    public string JobId { get; }
    public IReadOnlyList<double> Samples { get; }
    public double LowerBound { get; }
    public double UpperBound { get; }
}
