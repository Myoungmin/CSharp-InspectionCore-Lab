using System.Security.Cryptography;
using System.Text.Json;
using Inspection.Contracts;
using Inspection.Core;
using Inspection.Transport;
using Microsoft.Data.Sqlite;

namespace Inspection.Host;

internal sealed class RequestDispatcher(InspectionEngine engine, IResultReader store, IResultSearch? search, JsonDiagnosticLog diagnostics)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (string Fingerprint, ResponseEnvelope Response)> _starts = [];
    private readonly Dictionary<Guid, InspectionRunHandle> _runs = [];
    private readonly Dictionary<Guid, InspectionAutoHandle> _autos = [];

    internal async Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, CancellationToken readCancellation)
    {
        if (request.RequestId == Guid.Empty) { return Error(request, ErrorCodes.InvalidRequest, "RequestId must be nonempty."); }
        if (request.ProtocolVersion != Protocol.Version) { return Error(request, ErrorCodes.UnsupportedVersion, "ProtocolVersion must be 1."); }
        try
        {
            object command = request.MessageType switch
            {
                "StartJob" => Parse<StartJobRequest>(request),
                "StartAuto" => Parse<StartAutoRequest>(request),
                "GetStatus" => Parse<GetStatusRequest>(request),
                "SearchResults" => Parse<SearchResultsRequest>(request),
                "CancelRun" or "GetResult" => Parse<RunRequest>(request),
                "StopAuto" => Parse<AutoRequest>(request),
                _ => throw new ArgumentException("Unknown MessageType.")
            };
            string fingerprint = request.MessageType + ":" + Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(command, command.GetType(), PipeFrames.Json)));
            lock (_gate)
            {
                if (_starts.TryGetValue(request.RequestId, out var previous))
                {
                    diagnostics.Write(previous.Fingerprint == fingerprint ? "StartReplayed" : "RequestIdConflict", requestId: request.RequestId);
                    return previous.Fingerprint == fingerprint ? previous.Response
                        : Error(request, ErrorCodes.RequestIdConflict, "RequestId already identifies another start request.");
                }
                if (command is StartJobRequest or StartAutoRequest)
                {
                    if (_starts.Count >= Protocol.MaxStartRequests)
                    {
                        return Error(request, ErrorCodes.ReplayCapacityExceeded, "Start request capacity reached; query, cancel and shutdown remain available.");
                    }
                    ResponseEnvelope response = Start(request, command);
                    // Commit the admission response before attempting any connection write.
                    _starts.Add(request.RequestId, (fingerprint, response));
                    return response;
                }
                if (command is GetStatusRequest status) { return GetStatus(request, status); }
                if (command is AutoRequest auto)
                {
                    RequireId(auto.AutoId);
                    return Success(request, new ControlDto(engine.StopAuto(auto.AutoId).ToString()));
                }
                if (request.MessageType == "CancelRun" && command is RunRequest cancel)
                {
                    RequireId(cancel.RunId);
                    return Success(request, new ControlDto(engine.CancelRun(cancel.RunId).ToString()));
                }
            }
            InspectionResultQuery? validated = command is SearchResultsRequest query ? ToQuery(query) : null;
            Guid requestedRunId = command is RunRequest get ? get.RunId : Guid.Empty;
            if (validated is null) { RequireId(requestedRunId); }
            try
            {
                if (validated is not null)
                {
                    if (search is null) { return Error(request, ErrorCodes.SearchNotSupported, "Search requires SQLite storage."); }
                    InspectionResultPage page = await search.SearchAsync(validated, readCancellation).ConfigureAwait(false);
                    return Success(request, new ResultPageDto(page.Items.Select(Map).ToArray(), page.NextCursor is { } cursor
                        ? new ResultCursorDto(cursor.InspectedAtUtc, cursor.RunId) : null));
                }
                InspectionResult? result = await store.LoadAsync(requestedRunId, readCancellation).ConfigureAwait(false);
                return result is null ? Error(request, ErrorCodes.ResultNotFound, "No persisted result exists for this RunId.")
                    : Success(request, Map(result));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or SqliteException or ArgumentException or FormatException)
            {
                diagnostics.Write("StoreReadFailed", new { request.MessageType }, requestId: request.RequestId, error: exception);
                return Error(request, ErrorCodes.StoreReadFailed, "The stored result could not be read.");
            }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            lock (_gate)
            {
                if (_starts.ContainsKey(request.RequestId)) { return Error(request, ErrorCodes.RequestIdConflict, "RequestId already identifies another start request."); }
            }
            return Error(request, ErrorCodes.InvalidRequest, "Unknown command, missing field or invalid payload.");
        }
    }

    private static InspectionResultQuery ToQuery(SearchResultsRequest query)
    {
        InspectionVerdict? verdict = query.Verdict switch
        {
            null => null, "Pass" => InspectionVerdict.Pass, "Fail" => InspectionVerdict.Fail,
            _ => throw new ArgumentException("Unknown verdict.")
        };
        if (query.JobId?.Length > Protocol.MaxJobIdLength) { throw new ArgumentException("JobId exceeds the limit."); }
        return new(query.FromUtc, query.ToUtc, verdict, query.JobId, query.Limit,
            query.Cursor is { } cursor ? new InspectionResultCursor(cursor.InspectedAtUtc, cursor.RunId) : null);
    }

    private ResponseEnvelope Start(RequestEnvelope request, object command)
    {
        if (command is StartJobRequest single)
        {
            InspectionStartResult result = engine.Start(ToJob(single.Job), Duration(single.TimeoutMilliseconds));
            if (result.Run is { } run) { _runs.Add(run.RunId, run); }
            return Success(request, new AdmissionDto(result.Disposition.ToString(), result.Run?.RunId, null, result.BusyRunId, result.BusyAutoId));
        }
        var auto = (StartAutoRequest)command;
        InspectionAutoStartResult admission = engine.StartAuto(ToJob(auto.Job), Duration(auto.IntervalMilliseconds)!.Value,
            Duration(auto.TimeoutMilliseconds), auto.MaxRuns);
        if (admission.Auto is { } handle) { _autos.Add(handle.AutoId, handle); }
        return Success(request, new AdmissionDto(admission.Disposition.ToString(), null, admission.Auto?.AutoId, admission.BusyRunId, admission.BusyAutoId));
    }

    private ResponseEnvelope GetStatus(RequestEnvelope request, GetStatusRequest query)
    {
        if (query.RunId is not null && query.AutoId is not null) { throw new ArgumentException("Choose one identity."); }
        InspectionEngineSnapshot status = engine.GetStatus();
        InspectionRunSnapshot? run = status.Run;
        InspectionAutoSnapshot? auto = status.Auto;
        if (query.RunId is { } runId)
        {
            RequireId(runId);
            run = engine.GetRun(runId);
            if (run is null && _runs.TryGetValue(runId, out var handle) && handle.Completion.IsCompletedSuccessfully)
            {
                run = handle.Completion.Result;
            }
            if (run is null) { return Error(request, ErrorCodes.RunNotFound, "Run status is not retained; use GetResult for older persisted auto runs."); }
        }
        if (query.AutoId is { } autoId)
        {
            RequireId(autoId);
            auto = engine.GetAuto(autoId);
            if (auto is null && _autos.TryGetValue(autoId, out var handle) && handle.Completion.IsCompletedSuccessfully)
            {
                auto = handle.Completion.Result;
            }
            if (auto is null) { return Error(request, ErrorCodes.AutoNotFound, "Auto status is not retained."); }
        }
        return Success(request, new StatusDto(status.State.ToString(), status.Mode.ToString(), status.IsBusy, Map(run), Map(auto)));
    }

    private static T Parse<T>(RequestEnvelope request) => request.Payload.Deserialize<T>(PipeFrames.Json)
        ?? throw new JsonException("Null payload.");

    private static InspectionJob ToJob(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobId is null || job.JobId.Length > Protocol.MaxJobIdLength || job.Samples is null || job.Samples.Length > Protocol.MaxSamples)
        {
            throw new ArgumentException("Job exceeds protocol limits.");
        }
        return new InspectionJob(job.JobId, job.Samples, job.LowerBound, job.UpperBound);
    }

    private static TimeSpan? Duration(long? milliseconds)
    {
        if (milliseconds is null) { return null; }
        if (milliseconds is < 0 or > uint.MaxValue - 1L) { throw new ArgumentOutOfRangeException(nameof(milliseconds)); }
        return TimeSpan.FromMilliseconds(milliseconds.Value);
    }

    private static void RequireId(Guid id) { if (id == Guid.Empty) { throw new ArgumentException("Empty identity."); } }
    internal static ResponseEnvelope Error(RequestEnvelope request, string code, string message) => new(Protocol.Version, request.RequestId, code, message, null);
    internal static ResponseEnvelope Success<T>(RequestEnvelope request, T payload) =>
        new(Protocol.Version, request.RequestId, null, null, JsonSerializer.SerializeToElement(payload, PipeFrames.Json));
    private static ResultDto Map(InspectionResult result) => new(result.RunId, result.JobId, result.StartedAtUtc, result.InspectedAtUtc,
        result.Assessment.Verdict.ToString(), result.Assessment.Score, result.Assessment.SampleCount, result.Assessment.DefectCount);
    private static RunDto? Map(InspectionRunSnapshot? run) => run is null ? null : new(run.RunId, run.JobId,
        run.State.ToString(), run.Stage?.ToString(), run.StopReason?.ToString(), run.AcceptedAtUtc, run.CompletedAtUtc,
        run.TerminationConfirmed, run.ComputedResult is null ? null : Map(run.ComputedResult),
        run.StopError is not null ? ErrorCodes.StopFailed : run.Error is not null && run.State == InspectionRunState.Faulted ? ErrorCodes.ExecutionFailed : null);
    private static AutoDto? Map(InspectionAutoSnapshot? auto) => auto is null ? null : new(auto.AutoId, auto.JobId,
        auto.State.ToString(), auto.StopReason?.ToString(), auto.StartedRuns, auto.CompletedRuns, auto.CurrentRunId, auto.NextRunAtUtc, Map(auto.LastRun));
}
