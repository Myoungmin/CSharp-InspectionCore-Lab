using Inspection.Core;

namespace Inspection.Tests;

[TestClass]
public sealed class InspectionJobTests
{
    [TestMethod]
    public void Job_CopiesInputAndExposesReadOnlySamples()
    {
        double[] source = [10, 20];
        var job = new InspectionJob("job-1", source, 0, 100);
        source[0] = 999;

        Assert.AreEqual(10.0, job.Samples[0]);
        Assert.ThrowsException<NotSupportedException>(() => ((IList<double>)job.Samples)[0] = 888);
    }

    [TestMethod]
    public void Job_RejectsInvalidDefinitionsBeforeDeviceAccess()
    {
        Assert.ThrowsException<ArgumentException>(() => new InspectionJob(" ", [1], 0, 100));
        Assert.ThrowsException<ArgumentException>(() => new InspectionJob("job", [], 0, 100));
        Assert.ThrowsException<ArgumentException>(() => new InspectionJob("job", [double.NaN], 0, 100));
        Assert.ThrowsException<ArgumentException>(() => new InspectionJob("job", [double.PositiveInfinity], 0, 100));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InspectionJob("job", [1], 100, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InspectionJob("job", [1], 0, double.PositiveInfinity));
    }

    [DataTestMethod]
    [DataRow(3, 0, 100.0, InspectionVerdict.Pass)]
    [DataRow(3, 1, 66.67, InspectionVerdict.Fail)]
    [DataRow(3, 3, 0.0, InspectionVerdict.Fail)]
    public void Assessment_ReportsYieldAndProductVerdict(int samples, int defects, double score, InspectionVerdict verdict)
    {
        var assessment = new InspectionAssessment(samples, defects);
        Assert.AreEqual(score, assessment.Score);
        Assert.AreEqual(verdict, assessment.Verdict);
    }

    [TestMethod]
    public void Assessment_RejectsImpossibleCounts()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InspectionAssessment(0, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InspectionAssessment(3, -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InspectionAssessment(3, 4));
    }
}
