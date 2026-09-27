using System.IO.Pipes;
using System.Text.Json;
using Inspection.Contracts;
using Inspection.Transport;

namespace Inspection.Client;

public sealed class InspectionConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _requests = new(1, 1);

    private InspectionConnection(NamedPipeClientStream pipe) => _pipe = pipe;

    public static async Task<InspectionConnection> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new InspectionConnection(pipe);
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public static RequestEnvelope CreateRequest<T>(string messageType, T payload, Guid? requestId = null) =>
        new(Protocol.Version, requestId ?? Guid.NewGuid(), messageType, JsonSerializer.SerializeToElement(payload, PipeFrames.Json));

    public async Task<ResponseEnvelope> SendAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PipeFrames.WriteAsync(_pipe, request, cancellationToken).ConfigureAwait(false);
            byte[] body = await PipeFrames.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Host disconnected before the response.");
            ResponseEnvelope response = PipeFrames.Parse<ResponseEnvelope>(body);
            if (response.ProtocolVersion != Protocol.Version || response.RequestId != request.RequestId)
            {
                throw new InvalidDataException("Response correlation or protocol version mismatch.");
            }
            return response;
        }
        catch
        {
            // A canceled/failed exchange may leave an unread response; reconnect before reusing an ID.
            await _pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _requests.Release(); }
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
