using System.IO.Pipes;
using Inspection.Client;
using Inspection.Contracts;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("IPC")]
public sealed class ClientProtocolTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MismatchedResponse_IsRejectedAndConnectionClosed(bool wrongVersion)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string name = "InspectionProtocol_" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, Protocol.MaxFrameBytes, Protocol.MaxFrameBytes);
        Task accepting = server.WaitForConnectionAsync(deadline.Token);
        await using var client = await InspectionConnection.ConnectAsync(name, deadline.Token);
        await accepting;
        var request = InspectionConnection.CreateRequest("GetStatus", new GetStatusRequest());
        Task<ResponseEnvelope> response = client.SendAsync(request, deadline.Token);
        byte[] header = new byte[4];
        await server.ReadExactlyAsync(header, deadline.Token);
        byte[] body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header)];
        await server.ReadExactlyAsync(body, deadline.Token);
        await server.WriteAsync(IpcProcessFixture.Frame(new ResponseEnvelope(wrongVersion ? 2 : 1,
            wrongVersion ? request.RequestId : Guid.NewGuid(), null, null, null)), deadline.Token);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => response);
        try { Assert.AreEqual(0, await server.ReadAsync(new byte[1], deadline.Token)); }
        catch (IOException) { }
    }
}
