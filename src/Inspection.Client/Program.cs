using System.Text.Json;
using Inspection.Client;
using Inspection.Contracts;
using Inspection.Transport;

if (args is ["--help"])
{
    Console.WriteLine("Inspection.Client --pipe NAME --request FILE.json\nSend one protocol request; print one JSON response. Exit: 0 success, 1 transport, 2 arguments, 3 protocol error.");
    return 0;
}
if (args is not ["--pipe", var pipeName, "--request", var requestPath])
{
    Console.Error.WriteLine("Use --help.");
    return 2;
}
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    if (new FileInfo(requestPath).Length > Protocol.MaxFrameBytes) { throw new InvalidDataException("Request exceeds the frame limit."); }
    RequestEnvelope request = PipeFrames.Parse<RequestEnvelope>(await File.ReadAllBytesAsync(requestPath, deadline.Token));
    await using var connection = await InspectionConnection.ConnectAsync(pipeName, deadline.Token);
    ResponseEnvelope response = await connection.SendAsync(request, deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(response));
    return response.ErrorCode is null ? 0 : 3;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or OperationCanceledException)
{
    Console.Error.WriteLine($"Request failed: {exception.Message}");
    return 1;
}
