namespace Inspection.Core;

public enum InspectionVerdict
{
    Pass,
    Fail
}

public sealed record InspectionAssessment
{
    public InspectionAssessment(int sampleCount, int defectCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 1);
        if (defectCount < 0 || defectCount > sampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(defectCount));
        }

        SampleCount = sampleCount;
        DefectCount = defectCount;
    }

    public int SampleCount { get; }
    public int DefectCount { get; }
    public double Score => Math.Round(100.0 * (SampleCount - DefectCount) / SampleCount, 2);
    public InspectionVerdict Verdict => DefectCount == 0 ? InspectionVerdict.Pass : InspectionVerdict.Fail;
}

public sealed record InspectionResult(
    Guid RunId,
    string JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset InspectedAtUtc,
    InspectionAssessment Assessment);
