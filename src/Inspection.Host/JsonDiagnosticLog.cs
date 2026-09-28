using System.Text.Json;
using System.Text.Json.Serialization;
using Inspection.Core;
using Inspection.Interop;
using Microsoft.Data.Sqlite;

namespace Inspection.Host;

internal sealed class JsonDiagnosticLog : IInspectionDiagnostics, IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly Guid _sessionId = Guid.NewGuid();
    private long _sequence;
    private bool _failed;
    private bool _disposed;

    internal JsonDiagnosticLog(string path)
    {
        PathName = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        _writer = new StreamWriter(new FileStream(PathName, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    }

    internal string PathName { get; }

    public void Record(InspectionDiagnostic diagnostic)
    {
        InspectionRunSnapshot? outcome = diagnostic.Outcome;
        Write(diagnostic.EventName, new
        {
            diagnostic.Stage, outcome?.State, outcome?.StopReason, outcome?.AcceptedAtUtc, outcome?.CompletedAtUtc,
            outcome?.TerminationConfirmed, outcome?.ComputedResult
        }, runId: diagnostic.RunId, autoId: diagnostic.AutoId, jobId: diagnostic.JobId, error: outcome?.Error, secondaryError: outcome?.StopError);
    }

    internal void Write(string eventName, object? data = null, Guid? requestId = null, Guid? runId = null,
        Guid? autoId = null, Guid? connectionId = null, string? jobId = null, Exception? error = null, Exception? secondaryError = null)
    {
        lock (_gate)
        {
            if (_failed || _disposed) { return; }
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(new
                {
                    Utc = DateTimeOffset.UtcNow, SessionId = _sessionId, ProcessId = Environment.ProcessId,
                    Sequence = ++_sequence, Event = eventName, RequestId = requestId, RunId = runId,
                    AutoId = autoId, ConnectionId = connectionId, JobId = jobId, Data = data,
                    Errors = Flatten(error).Concat(Flatten(secondaryError)).ToArray()
                }, Options));
            }
            catch (Exception exception) { Fail(exception); }
        }
    }

    private static IEnumerable<object> Flatten(Exception? exception)
    {
        if (exception is null) { yield break; }
        var pending = new Queue<Exception>();
        pending.Enqueue(exception);
        for (int count = 0; pending.Count > 0 && count < 16; count++)
        {
            Exception current = pending.Dequeue();
            var native = current as NativeInspectionException;
            var sqlite = current as SqliteException;
            yield return new
            {
                Type = current.GetType().FullName,
                Message = current.Message.Length > 2048 ? current.Message[..2048] : current.Message,
                current.HResult, NativeOperation = native?.Operation, NativeStatus = native?.Status, NativeStatusCode = (int?)native?.Status,
                SqliteErrorCode = sqlite?.SqliteErrorCode, SqliteExtendedErrorCode = sqlite?.SqliteExtendedErrorCode
            };
            if (current is AggregateException aggregate) { foreach (Exception inner in aggregate.InnerExceptions) { pending.Enqueue(inner); } }
            else if (current.InnerException is { } inner) { pending.Enqueue(inner); }
        }
    }

    private void Fail(Exception exception)
    {
        _failed = true;
        try { Console.Error.WriteLine($"DiagnosticFailure SessionId={_sessionId} ErrorType={exception.GetType().Name}"); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            try { _writer.Dispose(); }
            catch (Exception exception) { if (!_failed) { Fail(exception); } }
        }
    }
}
