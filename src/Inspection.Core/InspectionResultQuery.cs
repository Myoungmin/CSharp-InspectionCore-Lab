namespace Inspection.Core;

public interface IResultReader
{
    Task<InspectionResult?> LoadAsync(Guid runId, CancellationToken cancellationToken);
}

public interface IResultSearch
{
    Task<InspectionResultPage> SearchAsync(InspectionResultQuery query, CancellationToken cancellationToken);
}

public sealed record InspectionResultCursor(DateTimeOffset InspectedAtUtc, Guid RunId);
public sealed record InspectionResultPage(IReadOnlyList<InspectionResult> Items, InspectionResultCursor? NextCursor);

public sealed class InspectionResultQuery
{
    public const int MaxLimit = 25;

    public InspectionResultQuery(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        InspectionVerdict? verdict = null, string? jobId = null, int limit = MaxLimit, InspectionResultCursor? cursor = null)
    {
        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc) { throw new ArgumentException("Search range must be nonempty and ordered."); }
        if (verdict is not null && !Enum.IsDefined(verdict.Value)) { throw new ArgumentOutOfRangeException(nameof(verdict)); }
        if (jobId is not null) { ArgumentException.ThrowIfNullOrWhiteSpace(jobId); }
        if (limit is < 1 or > MaxLimit) { throw new ArgumentOutOfRangeException(nameof(limit)); }
        if (cursor?.RunId == Guid.Empty) { throw new ArgumentException("Cursor identity must be nonempty.", nameof(cursor)); }
        FromUtc = fromUtc?.ToUniversalTime();
        ToUtc = toUtc?.ToUniversalTime();
        Verdict = verdict;
        JobId = jobId;
        Limit = limit;
        Cursor = cursor;
    }

    public DateTimeOffset? FromUtc { get; }
    public DateTimeOffset? ToUtc { get; }
    public InspectionVerdict? Verdict { get; }
    public string? JobId { get; }
    public int Limit { get; }
    public InspectionResultCursor? Cursor { get; }
}
