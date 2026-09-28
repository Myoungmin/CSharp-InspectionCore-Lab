using Inspection.Core;

namespace Inspection.Tests;

[TestClass]
public sealed class ResultQueryTests
{
    [DataTestMethod]
    [DataRow("zero")]
    [DataRow("large")]
    [DataRow("range")]
    [DataRow("verdict")]
    [DataRow("job")]
    [DataRow("cursor")]
    public void Query_RejectsInvalidBoundsAndFilters(string invalid)
    {
        Assert.ThrowsException<ArgumentException>(() =>
        {
            try
            {
                _ = invalid switch
                {
                    "zero" => new InspectionResultQuery(limit: 0),
                    "large" => new InspectionResultQuery(limit: 26),
                    "range" => new InspectionResultQuery(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                    "verdict" => new InspectionResultQuery(verdict: (InspectionVerdict)99),
                    "job" => new InspectionResultQuery(jobId: " "),
                    _ => new InspectionResultQuery(cursor: new(DateTimeOffset.UnixEpoch, Guid.Empty))
                };
            }
            catch (ArgumentOutOfRangeException exception) { throw new ArgumentException("Invalid query.", exception); }
        });
    }

    [TestMethod]
    public void Query_NormalizesUtcAndRetainsExactJobAndCursor()
    {
        var from = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.FromHours(9));
        var cursor = new InspectionResultCursor(from.AddMinutes(1), Guid.NewGuid());
        var query = new InspectionResultQuery(from, from.AddHours(1), InspectionVerdict.Fail, "a' OR 1=1", 2, cursor);
        Assert.AreEqual(TimeSpan.Zero, query.FromUtc!.Value.Offset);
        Assert.AreEqual(from, query.FromUtc);
        Assert.AreEqual("a' OR 1=1", query.JobId);
        Assert.AreSame(cursor, query.Cursor);
        Assert.AreEqual(2, query.Limit);
    }
}
