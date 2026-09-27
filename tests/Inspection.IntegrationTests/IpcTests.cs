using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Inspection.Client;
using Inspection.Contracts;
using static Inspection.IntegrationTests.IpcProcessFixture;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("IPC")]
public sealed class IpcTests
{
    private static JobDto Job(bool fail = false) => new("ipc-검사", fail ? [0, 100, 101] : [0, 50, 100], 0, 100);

    [DataTestMethod]
    [DataRow("managed", false)]
    [DataRow("managed", true)]
    [DataRow("native", false)]
    [DataRow("native", true)]
    public async Task SeparateClientProcess_StartsAndRetrievesPersistedResult(string inspector, bool fail)
    {
        await using var host = await LaunchAsync(inspector);
        var request = InspectionConnection.CreateRequest("StartJob", new StartJobRequest(Job(fail)));
        string file = Path.Combine(host.DirectoryPath, "request with spaces.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(request));
        using var client = host.Start("Inspection.Client", "--pipe", host.PipeName, "--request", file);
        Task<string> output = client.StandardOutput.ReadToEndAsync();
        Task<string> errors = client.StandardError.ReadToEndAsync();
        try
        {
            await client.WaitForExitAsync(host.Token);
            Assert.AreEqual(0, client.ExitCode, await errors);
            var response = JsonSerializer.Deserialize<ResponseEnvelope>(await output)!;
            Assert.AreEqual(request.RequestId, response.RequestId);
            AdmissionDto admission = Payload<AdmissionDto>(response);
            Assert.AreEqual("Accepted", admission.Disposition);
            Guid runId = admission.RunId!.Value;
            StatusDto status = await host.UntilAsync(s => s.Run?.CompletedAtUtc is not null, new(runId));
            Assert.AreEqual("Succeeded", status.Run!.State);
            ResultDto result = Payload<ResultDto>(await host.SendAsync("GetResult", new RunRequest(runId)));
            Assert.AreEqual(runId, result.RunId);
            Assert.AreEqual("ipc-검사", result.JobId);
            Assert.AreEqual(fail ? "Fail" : "Pass", result.Verdict);
            Assert.AreEqual(fail ? 66.67 : 100, result.Score);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(host.ResultsPath, $"{runId:N}.json")));
            Assert.AreEqual(result.RunId, json.RootElement.GetProperty("RunId").GetGuid());
        }
        finally { if (!client.HasExited) { client.Kill(entireProcessTree: true); await client.WaitForExitAsync(); } }
    }

    [TestMethod]
    public async Task Reconnect_KeepsAcceptedRunAndAllowsBusyQueryAndCancel()
    {
        await using var host = await LaunchAsync(delay: 60_000);
        AdmissionDto start = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job())));
        Guid id = start.RunId!.Value;
        StatusDto running = await host.UntilAsync(s => s.Run?.Stage == "Acquire", new(id));
        Assert.AreEqual("Running", running.Run!.State);
        Guid busyRequestId = Guid.NewGuid();
        AdmissionDto busy = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job()), busyRequestId));
        Assert.AreEqual("Busy", busy.Disposition);
        Assert.AreEqual(id, busy.BusyRunId);
        Assert.AreEqual("Accepted", Payload<ControlDto>(await host.SendAsync("CancelRun", new RunRequest(id))).Disposition);
        StatusDto finished = await host.UntilAsync(s => s.Run?.CompletedAtUtc is not null, new(id));
        Assert.AreEqual("Canceled", finished.Run!.State);
        Assert.AreEqual("UserCancellation", finished.Run.StopReason);
        Assert.AreEqual(true, finished.Run.TerminationConfirmed);
        Assert.AreEqual("TooLate", Payload<ControlDto>(await host.SendAsync("CancelRun", new RunRequest(id))).Disposition);
        Assert.AreEqual("ResultNotFound", (await host.SendAsync("GetResult", new RunRequest(id))).ErrorCode);
        Assert.AreEqual(busy, Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job()), busyRequestId)));
    }

    [TestMethod]
    public async Task ConcurrentDuplicateStarts_ReturnOneAdmissionAndRejectChangedContent()
    {
        await using var host = await LaunchAsync(delay: 60_000);
        Guid requestId = Guid.NewGuid();
        var request = new StartJobRequest(Job());
        ResponseEnvelope[] responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.SendAsync("StartJob", request, requestId)));
        AdmissionDto first = Payload<AdmissionDto>(responses[0]);
        Assert.AreEqual("Accepted", first.Disposition);
        foreach (var response in responses) { Assert.AreEqual(first, Payload<AdmissionDto>(response)); }
        Assert.AreEqual("RequestIdConflict", (await host.SendAsync("StartJob", request with { Job = Job(true) }, requestId)).ErrorCode);
        Assert.AreEqual("RequestIdConflict", (await host.SendAsync("GetStatus", new GetStatusRequest(), requestId)).ErrorCode);
        Assert.AreEqual("Accepted", Payload<ControlDto>(await host.SendAsync("CancelRun", new RunRequest(first.RunId!.Value))).Disposition);
    }

    [TestMethod]
    public async Task LostResponse_RetryAfterCompletionDoesNotStartAgainAndOldStatusRemains()
    {
        await using var host = await LaunchAsync();
        var request = InspectionConnection.CreateRequest("StartJob", new StartJobRequest(Job()));
        await using (var pipe = await host.RawAsync()) { await pipe.WriteAsync(Frame(request), host.Token); }
        AdmissionDto original = Payload<AdmissionDto>(await host.SendAsync(request.MessageType, new StartJobRequest(Job()), request.RequestId));
        await host.UntilAsync(s => s.Run?.State == "Succeeded", new(original.RunId));
        // Reordered properties and omitted optional fields have the same typed meaning.
        using JsonDocument payload = JsonDocument.Parse("{\"Job\":{\"UpperBound\":100.0,\"Samples\":[0,50,100],\"LowerBound\":0,\"JobId\":\"ipc-검사\"}}");
        await using var connection = await host.ConnectAsync();
        Assert.AreEqual(original, Payload<AdmissionDto>(await connection.SendAsync(request with { Payload = payload.RootElement }, host.Token)));
        AdmissionDto second = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job(true))));
        await host.UntilAsync(s => s.Run?.State == "Succeeded", new(second.RunId));
        StatusDto old = Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest(original.RunId)));
        Assert.AreEqual(original.RunId, old.Run!.RunId);
        Assert.AreEqual("Pass", Payload<ResultDto>(await host.SendAsync("GetResult", new RunRequest(original.RunId!.Value))).Verdict);
        Assert.AreEqual(2, Directory.GetFiles(host.ResultsPath, "*.json").Length);
    }

    [TestMethod]
    public async Task PartialAndMultipleFrames_AreReadInOrderOnOneConnection()
    {
        await using var host = await LaunchAsync();
        await using var pipe = await host.RawAsync();
        var first = InspectionConnection.CreateRequest("GetStatus", new GetStatusRequest());
        byte[] frame = Frame(first);
        // Every header byte and body fragment is sent separately; reads must fill exact lengths.
        foreach (byte value in frame) { await pipe.WriteAsync(new byte[] { value }, host.Token); }
        Assert.AreEqual(first.RequestId, (await host.ReadAsync(pipe)).RequestId);
        var second = InspectionConnection.CreateRequest("GetStatus", new GetStatusRequest());
        var third = InspectionConnection.CreateRequest("GetStatus", new GetStatusRequest());
        await pipe.WriteAsync(Frame(second).Concat(Frame(third)).ToArray(), host.Token);
        Assert.AreEqual(second.RequestId, (await host.ReadAsync(pipe)).RequestId);
        Assert.AreEqual(third.RequestId, (await host.ReadAsync(pipe)).RequestId);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(65537)]
    public async Task InvalidFrameLength_ClosesOnlyThatConnection(int length)
    {
        await using var host = await LaunchAsync();
        await using var pipe = await host.RawAsync();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await pipe.WriteAsync(header, host.Token);
        try { Assert.AreEqual(0, await pipe.ReadAsync(new byte[1], host.Token)); }
        catch (IOException) { }
        Assert.AreEqual("Ready", Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest())).State);
    }

    [TestMethod]
    public async Task MalformedEnvelopeAndTruncatedBody_DoNotPoisonServer()
    {
        await using var host = await LaunchAsync();
        foreach (string bad in new[] { "{", "null", "{\"RequestId\":\"00000000-0000-0000-0000-000000000000\",\"RequestId\":\"00000000-0000-0000-0000-000000000000\"}" })
        {
            await using var pipe = await host.RawAsync();
            byte[] body = Encoding.UTF8.GetBytes(bad), header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
            await pipe.WriteAsync(header, host.Token);
            await pipe.WriteAsync(body, host.Token);
            try { Assert.AreEqual(0, await pipe.ReadAsync(new byte[1], host.Token)); }
            catch (IOException) { }
        }
        await using (var pipe = await host.RawAsync()) { await pipe.WriteAsync(new byte[] { 10, 0, 0, 0, 123 }, host.Token); }
        Assert.AreEqual("Ready", Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest())).State);
    }

    [TestMethod]
    public async Task UnsupportedVersionAndInvalidPayload_AreRejectedWithoutExecution()
    {
        await using var host = await LaunchAsync();
        await using var connection = await host.ConnectAsync();
        var request = InspectionConnection.CreateRequest("StartJob", new StartJobRequest(Job()));
        Assert.AreEqual("UnsupportedVersion", (await connection.SendAsync(request with { ProtocolVersion = 2 }, host.Token)).ErrorCode);
        Assert.AreEqual("InvalidRequest", (await connection.SendAsync(request with { RequestId = Guid.Empty }, host.Token)).ErrorCode);
        foreach (string json in new[] { "{}", "{\"Job\":null}", "{\"Job\":{\"JobId\":\"x\",\"Samples\":[],\"LowerBound\":0,\"UpperBound\":100}}", "{\"Job\":{\"JobId\":\"x\",\"Samples\":[1],\"LowerBound\":0,\"UpperBound\":100},\"Typo\":0}" })
        {
            using JsonDocument payload = JsonDocument.Parse(json);
            Assert.AreEqual("InvalidRequest", (await connection.SendAsync(request with { Payload = payload.RootElement }, host.Token)).ErrorCode);
        }
        Assert.AreEqual("InvalidRequest", (await host.SendAsync("Unknown", new { })).ErrorCode);
        Assert.IsFalse(Directory.Exists(host.ResultsPath));
        Assert.IsFalse(Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest())).IsBusy);
    }

    [TestMethod]
    public async Task AutoFailContinuesAndCompletedSessionCanBeReplayed()
    {
        await using var host = await LaunchAsync("native");
        Guid id = Guid.NewGuid();
        var request = new StartAutoRequest(Job(true), 0, MaxRuns: 3);
        AdmissionDto admission = Payload<AdmissionDto>(await host.SendAsync("StartAuto", request, id));
        StatusDto completed = await host.UntilAsync(s => s.Auto?.State == "Completed" && s.Mode == "Manual", new(AutoId: admission.AutoId));
        Assert.AreEqual(3L, completed.Auto!.CompletedRuns);
        Assert.AreEqual("Fail", completed.Auto.LastRun!.ComputedResult!.Verdict);
        Assert.AreEqual(admission, Payload<AdmissionDto>(await host.SendAsync("StartAuto", request, id)));
        Assert.AreEqual(3, Directory.GetFiles(host.ResultsPath, "*.json").Length);
    }

    [TestMethod]
    public async Task StopAutoWhileWaiting_ReleasesReservationWithoutCancelingLastRun()
    {
        await using var host = await LaunchAsync();
        AdmissionDto admission = Payload<AdmissionDto>(await host.SendAsync("StartAuto", new StartAutoRequest(Job(), 60_000)));
        StatusDto waiting = await host.UntilAsync(s => s.Auto?.State == "Waiting");
        Assert.AreEqual("Succeeded", waiting.Auto!.LastRun!.State);
        Assert.AreEqual("Busy", Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job()))).Disposition);
        Assert.AreEqual("Accepted", Payload<ControlDto>(await host.SendAsync("StopAuto", new AutoRequest(admission.AutoId!.Value))).Disposition);
        StatusDto stopped = await host.UntilAsync(s => s.Mode == "Manual");
        Assert.AreEqual("Requested", stopped.Auto!.StopReason);
        Assert.AreEqual(1L, stopped.Auto.StartedRuns);
        Assert.AreEqual("Succeeded", stopped.Auto.LastRun!.State);
    }

    [TestMethod]
    public async Task StopAutoDuringAcquire_DoesNotCancelCurrentRun()
    {
        await using var host = await LaunchAsync(delay: 60_000);
        AdmissionDto admission = Payload<AdmissionDto>(await host.SendAsync("StartAuto", new StartAutoRequest(Job(), 0)));
        StatusDto running = await host.UntilAsync(s => s.Run?.Stage == "Acquire");
        await host.SendAsync("StopAuto", new AutoRequest(admission.AutoId!.Value));
        StatusDto stopping = Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest()));
        Assert.AreEqual("Stopping", stopping.Auto!.State);
        Assert.AreEqual("Running", stopping.Run!.State);
        Assert.IsNull(stopping.Run.StopReason);
        await host.SendAsync("CancelRun", new RunRequest(running.Run!.RunId));
        StatusDto done = await host.UntilAsync(s => s.Mode == "Manual");
        Assert.AreEqual("RunCanceled", done.Auto!.StopReason);
        Assert.AreEqual(1L, done.Auto.StartedRuns);
    }

    [TestMethod]
    public async Task TimeoutAndStorageFailure_ReportTerminalCauseAndComputedDiagnostics()
    {
        await using (var host = await LaunchAsync())
        {
            AdmissionDto admission = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job(), 0)));
            StatusDto status = await host.UntilAsync(s => s.Run?.CompletedAtUtc is not null, new(admission.RunId));
            Assert.AreEqual("TimedOut", status.Run!.State);
            Assert.AreEqual("Timeout", status.Run.StopReason);
            Assert.IsFalse(Directory.Exists(host.ResultsPath));
        }
        await using (var host = await LaunchAsync(blockedStorage: true))
        {
            AdmissionDto admission = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job())));
            StatusDto status = await host.UntilAsync(s => s.Run?.CompletedAtUtc is not null, new(admission.RunId));
            Assert.AreEqual("Faulted", status.Run!.State);
            Assert.AreEqual("Persist", status.Run.Stage);
            Assert.AreEqual(100.0, status.Run.ComputedResult!.Score);
            Assert.AreEqual("ExecutionFailed", status.Run.ErrorCode);
        }
    }

    [TestMethod]
    public async Task Shutdown_WithPartialFrameAndRunningWork_ExitsWithinDeadline()
    {
        var host = await LaunchAsync("native", delay: 60_000);
        await using var pipe = await host.RawAsync();
        try
        {
            await pipe.WriteAsync(new byte[] { 1, 0 }, host.Token);
            await host.SendAsync("StartJob", new StartJobRequest(Job()));
            await host.UntilAsync(s => s.Run?.Stage == "Acquire");
        }
        finally { await host.DisposeAsync(); }
        Assert.IsFalse(Directory.Exists(host.ResultsPath));
    }

    [TestMethod]
    public async Task ReplayCapacity_DoesNotEvictAdmissionsOrBlockQueryAndCancel()
    {
        await using var host = await LaunchAsync(delay: 60_000);
        await using var connection = await host.ConnectAsync();
        var original = InspectionConnection.CreateRequest("StartJob", new StartJobRequest(Job()));
        AdmissionDto accepted = Payload<AdmissionDto>(await connection.SendAsync(original, host.Token));
        for (int i = 1; i < Protocol.MaxStartRequests; i++)
        {
            var request = original with { RequestId = Guid.NewGuid() };
            Assert.AreEqual("Busy", Payload<AdmissionDto>(await connection.SendAsync(request, host.Token)).Disposition);
        }
        Assert.AreEqual("ReplayCapacityExceeded", (await connection.SendAsync(original with { RequestId = Guid.NewGuid() }, host.Token)).ErrorCode);
        Assert.AreEqual(accepted, Payload<AdmissionDto>(await connection.SendAsync(original, host.Token)));
        Assert.IsTrue(Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest())).IsBusy);
        Assert.AreEqual("Accepted", Payload<ControlDto>(await host.SendAsync("CancelRun", new RunRequest(accepted.RunId!.Value))).Disposition);
        await host.UntilAsync(s => s.Run?.State == "Canceled");
    }

    [TestMethod]
    public async Task UnknownIdentitiesAndCorruptStoredResult_ReturnExplicitErrors()
    {
        await using var host = await LaunchAsync();
        Guid id = Guid.NewGuid();
        Assert.AreEqual("RunNotFound", (await host.SendAsync("GetStatus", new GetStatusRequest(id))).ErrorCode);
        Assert.AreEqual("AutoNotFound", (await host.SendAsync("GetStatus", new GetStatusRequest(AutoId: id))).ErrorCode);
        Assert.AreEqual("ResultNotFound", (await host.SendAsync("GetResult", new RunRequest(id))).ErrorCode);
        Assert.AreEqual("NotFound", Payload<ControlDto>(await host.SendAsync("CancelRun", new RunRequest(id))).Disposition);
        Assert.AreEqual("NotFound", Payload<ControlDto>(await host.SendAsync("StopAuto", new AutoRequest(id))).Disposition);
        Directory.CreateDirectory(host.ResultsPath);
        await File.WriteAllTextAsync(Path.Combine(host.ResultsPath, $"{id:N}.json"), "{");
        Assert.AreEqual("StoreReadFailed", (await host.SendAsync("GetResult", new RunRequest(id))).ErrorCode);
        Assert.AreEqual("Ready", Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest())).State);
    }

    [TestMethod]
    public async Task RepeatedTruncatedConnections_DoNotExhaustListenerPool()
    {
        await using var host = await LaunchAsync();
        for (int i = 0; i < 40; i++)
        {
            await using (var pipe = await host.RawAsync())
            {
                if (i % 2 == 0) { await pipe.WriteAsync(new byte[] { 10, 0, 0, 0, 123 }, host.Token); }
            }
            Assert.AreEqual("Ready", Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest())).State);
        }
    }
}
