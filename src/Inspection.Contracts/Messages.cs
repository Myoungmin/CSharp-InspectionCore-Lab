using System.Text.Json;

namespace Inspection.Contracts;

public static class Protocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 65_536;
    public const int MaxSamples = 4_096;
    public const int MaxJobIdLength = 128;
    public const int MaxStartRequests = 4_096;
}

public static class ErrorCodes
{
    public const string InvalidRequest = "InvalidRequest";
    public const string UnsupportedVersion = "UnsupportedVersion";
    public const string RequestIdConflict = "RequestIdConflict";
    public const string ReplayCapacityExceeded = "ReplayCapacityExceeded";
    public const string RunNotFound = "RunNotFound";
    public const string AutoNotFound = "AutoNotFound";
    public const string ResultNotFound = "ResultNotFound";
    public const string StoreReadFailed = "StoreReadFailed";
    public const string SearchNotSupported = "SearchNotSupported";
    public const string ExecutionFailed = "ExecutionFailed";
    public const string StopFailed = "StopFailed";
}

public sealed record RequestEnvelope(int ProtocolVersion, Guid RequestId, string MessageType, JsonElement Payload);
public sealed record ResponseEnvelope(int ProtocolVersion, Guid RequestId, string? ErrorCode, string? ErrorMessage, JsonElement? Payload);
public sealed record JobDto(string JobId, double[] Samples, double LowerBound, double UpperBound);
public sealed record StartJobRequest(JobDto Job, long? TimeoutMilliseconds = null);
public sealed record StartAutoRequest(JobDto Job, long IntervalMilliseconds, long? TimeoutMilliseconds = null, int? MaxRuns = null);
public sealed record GetStatusRequest(Guid? RunId = null, Guid? AutoId = null);
public sealed record RunRequest(Guid RunId);
public sealed record ResultCursorDto(DateTimeOffset InspectedAtUtc, Guid RunId);
public sealed record SearchResultsRequest(DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null,
    string? Verdict = null, string? JobId = null, int Limit = 25, ResultCursorDto? Cursor = null);
public sealed record ResultPageDto(ResultDto[] Items, ResultCursorDto? NextCursor);
public sealed record AutoRequest(Guid AutoId);
public sealed record AdmissionDto(string Disposition, Guid? RunId, Guid? AutoId, Guid? BusyRunId, Guid? BusyAutoId);
public sealed record ControlDto(string Disposition);
public sealed record ResultDto(Guid RunId, string JobId, DateTimeOffset StartedAtUtc, DateTimeOffset InspectedAtUtc,
    string Verdict, double Score, int SampleCount, int DefectCount);
public sealed record RunDto(Guid RunId, string JobId, string State, string? Stage, string? StopReason,
    DateTimeOffset AcceptedAtUtc, DateTimeOffset? CompletedAtUtc, bool? TerminationConfirmed,
    ResultDto? ComputedResult, string? ErrorCode);
public sealed record AutoDto(Guid AutoId, string JobId, string State, string? StopReason, long StartedRuns,
    long CompletedRuns, Guid? CurrentRunId, DateTimeOffset? NextRunAtUtc, RunDto? LastRun);
public sealed record StatusDto(string State, string Mode, bool IsBusy, RunDto? Run, AutoDto? Auto);
