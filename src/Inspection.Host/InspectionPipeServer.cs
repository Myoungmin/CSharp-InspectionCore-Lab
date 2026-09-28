using System.IO.Pipes;
using System.Text.Json;
using Inspection.Contracts;
using Inspection.Transport;

namespace Inspection.Host;

internal sealed class InspectionPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream[] _listeners;
    private readonly RequestDispatcher _dispatcher;
    private readonly string _name;
    private readonly object _replacementGate = new();
    private readonly JsonDiagnosticLog _diagnostics;
    private int _dropStartResponse;

    internal InspectionPipeServer(string name, RequestDispatcher dispatcher, JsonDiagnosticLog diagnostics, bool dropStartResponse)
    {
        _dispatcher = dispatcher;
        _name = name;
        _diagnostics = diagnostics;
        _dropStartResponse = dropStartResponse ? 1 : 0;
        var listeners = new List<NamedPipeServerStream>();
        try
        {
            for (int i = 0; i < 16; i++)
            {
                listeners.Add(new NamedPipeServerStream(name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (i == 0 ? PipeOptions.FirstPipeInstance : 0),
                    Protocol.MaxFrameBytes, Protocol.MaxFrameBytes));
            }
            _listeners = listeners.ToArray();
        }
        catch { foreach (var listener in listeners) { listener.Dispose(); } throw; }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] workers = Enumerable.Range(0, _listeners.Length).Select(index => ServeAsync(index, stopping.Token)).ToArray();
        await Task.WhenAny(workers).ConfigureAwait(false);
        await stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task ServeAsync(int index, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = _listeners[index];
            bool connected = false;
            Guid connectionId = Guid.NewGuid();
            Guid? requestId = null;
            try
            {
                await pipe.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                connected = true;
                _diagnostics.Write("ConnectionOpened", connectionId: connectionId);
                while (!stopping.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    byte[]? body = await PipeFrames.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
                    if (body is null) { break; }
                    RequestEnvelope request;
                    try { request = PipeFrames.Parse<RequestEnvelope>(body); }
                    catch (JsonException exception)
                    {
                        // There is no trustworthy correlation ID. Close without a reply.
                        _diagnostics.Write("IpcEnvelopeRejected", connectionId: connectionId, error: exception);
                        break;
                    }
                    requestId = request.RequestId;
                    _diagnostics.Write("RequestReceived", new { request.MessageType, request.ProtocolVersion }, requestId: requestId, connectionId: connectionId);
                    deadline.Token.ThrowIfCancellationRequested();
                    ResponseEnvelope response = await _dispatcher.DispatchAsync(request, deadline.Token).ConfigureAwait(false);
                    Guid? runId = Identity(response, "RunId");
                    Guid? autoId = Identity(response, "AutoId");
                    _diagnostics.Write("RequestHandled", new { request.MessageType, response.ErrorCode, response.Payload },
                        requestId, runId, autoId, connectionId);
                    if (request.MessageType is "StartJob" or "StartAuto" && (runId is not null || autoId is not null)
                        && Interlocked.Exchange(ref _dropStartResponse, 0) == 1)
                    {
                        _diagnostics.Write("IpcResponseDropped", requestId: requestId, runId: runId, autoId: autoId, connectionId: connectionId);
                        break;
                    }
                    await PipeFrames.WriteAsync(pipe, response, deadline.Token).ConfigureAwait(false);
                    requestId = null;
                }
            }
            catch (IOException exception) when (!connected && !stopping.IsCancellationRequested)
            {
                _diagnostics.Write("IpcConnectFailed", connectionId: connectionId, error: exception);
                // A peer can close before ConnectNamedPipe completes. Recreate that
                // instance instead of repeatedly waiting on an unusable handle.
                lock (_replacementGate)
                {
                    pipe.Dispose();
                    _listeners[index] = new NamedPipeServerStream(_name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, Protocol.MaxFrameBytes, Protocol.MaxFrameBytes);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
            {
                _diagnostics.Write(stopping.IsCancellationRequested ? "ConnectionStopping" : "IpcTransportFailed",
                    requestId: requestId, connectionId: connectionId, error: exception);
            }
            finally
            {
                // IsConnected becomes false after a broken read. Disconnect must also
                // reset that state before reusing the instance for another connection.
                if (connected)
                {
                    pipe.Disconnect();
                    _diagnostics.Write("ConnectionClosed", connectionId: connectionId);
                }
            }
        }
    }

    private static Guid? Identity(ResponseEnvelope response, string name) =>
        response.Payload is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out Guid id) ? id : null;

    public async ValueTask DisposeAsync()
    {
        foreach (var listener in _listeners) { await listener.DisposeAsync().ConfigureAwait(false); }
    }
}
