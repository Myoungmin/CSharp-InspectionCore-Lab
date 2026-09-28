using System.Text.Json;
using Inspection.Client;
using Inspection.Contracts;
using Inspection.Core;
using Inspection.Infrastructure;
using static Inspection.IntegrationTests.IpcProcessFixture;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("StorageDiagnostics")]
public sealed class StorageDiagnosticsTests
{
    private static JobDto Job(bool fail = false) => new("m5-job", fail ? [1, 99] : [1, 2], 0, 10);
    private static JsonElement[] Events(string path) => File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
    private static JsonElement Completed(IEnumerable<JsonElement> events, Guid runId) => events.Single(e =>
        e.GetProperty("Event").GetString() == "RunCompleted" && e.GetProperty("RunId").GetGuid() == runId);

    [DataTestMethod]
    [DataRow("managed")]
    [DataRow("native")]
    public async Task SqliteCliAuto_PersistsEveryFailAndLogsEveryCompletion(string inspector)
    {
        await using var fixture = await LaunchAsync();
        string output = Path.Combine(fixture.DirectoryPath, "cli-results");
        string log = Path.Combine(fixture.DirectoryPath, "cli.jsonl");
        using var process = fixture.Start("Inspection.Host", "--store", "sqlite", "--output", output,
            "--log", log, "--scenario", "fail", "--inspector", inspector, "--repeat", "3", "--interval-ms", "0");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(fixture.Token);
            Assert.AreEqual(0, process.ExitCode, await stderr);
            StringAssert.Contains(await stdout, "State=Completed Reason=RunLimitReached StartedRuns=3 CompletedRuns=3");
            var store = new SqliteResultStore(Path.Combine(output, "inspection.db"));
            InspectionResultPage page = await store.SearchAsync(new(verdict: InspectionVerdict.Fail), fixture.Token);
            Assert.AreEqual(3, page.Items.Count);
            JsonElement[] events = Events(log);
            Guid autoId = events.Single(e => e.GetProperty("Event").GetString() == "AutoAdmitted").GetProperty("AutoId").GetGuid();
            foreach (InspectionResult result in page.Items)
            {
                Assert.AreEqual("Succeeded", Completed(events, result.RunId).GetProperty("Data").GetProperty("State").GetString());
                Assert.AreEqual(autoId, Completed(events, result.RunId).GetProperty("AutoId").GetGuid());
                CollectionAssert.AreEqual(new[] { "Prepare", "Acquire", "Inspect", "Persist" }, events
                    .Where(e => e.GetProperty("Event").GetString() == "StageEntered" && e.GetProperty("RunId").GetGuid() == result.RunId)
                    .Select(e => e.GetProperty("Data").GetProperty("Stage").GetString()).ToArray());
            }
            Assert.AreEqual("HostStopped", events[^1].GetProperty("Event").GetString());
            CollectionAssert.AreEqual(Enumerable.Range(1, events.Length).Select(i => (long)i).ToArray(), events.Select(e => e.GetProperty("Sequence").GetInt64()).ToArray());
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    [DataTestMethod]
    [DataRow("managed")]
    [DataRow("native")]
    public async Task RestartHost_RetainsResultsAndSearchButNotRequestReplay(string inspector)
    {
        string results;
        Guid originalRequestId = Guid.NewGuid();
        var ids = new List<Guid>();
        await using (var first = await LaunchAsync(inspector, store: "sqlite"))
        {
            results = first.ResultsPath;
            foreach (bool fail in new[] { false, true })
            {
                AdmissionDto start = Payload<AdmissionDto>(await first.SendAsync("StartJob", new StartJobRequest(Job(fail)), fail ? null : originalRequestId));
                ids.Add(start.RunId!.Value);
                await first.UntilAsync(s => s.Run?.State == "Succeeded", new(start.RunId));
            }
        }
        await using var second = await LaunchAsync(inspector, store: "sqlite", resultsPath: results);
        foreach (Guid id in ids) { Assert.AreEqual(id, Payload<ResultDto>(await second.SendAsync("GetResult", new RunRequest(id))).RunId); }
        Assert.AreEqual("RunNotFound", (await second.SendAsync("GetStatus", new GetStatusRequest(ids[0]))).ErrorCode);
        ResultPageDto page = Payload<ResultPageDto>(await second.SendAsync("SearchResults", new SearchResultsRequest(JobId: "m5-job", Limit: 1)));
        Assert.AreEqual(ids[1], page.Items.Single().RunId);
        Assert.IsNotNull(page.NextCursor);
        ResultPageDto next = Payload<ResultPageDto>(await second.SendAsync("SearchResults", new SearchResultsRequest(JobId: "m5-job", Limit: 1, Cursor: page.NextCursor)));
        Assert.AreEqual(ids[0], next.Items.Single().RunId);
        Assert.IsNull(next.NextCursor);
        ResultPageDto failures = Payload<ResultPageDto>(await second.SendAsync("SearchResults", new SearchResultsRequest(Verdict: "Fail")));
        Assert.AreEqual(ids[1], failures.Items.Single().RunId);
        AdmissionDto fresh = Payload<AdmissionDto>(await second.SendAsync("StartJob", new StartJobRequest(Job()), originalRequestId));
        Assert.AreEqual("Accepted", fresh.Disposition);
        Assert.IsFalse(ids.Contains(fresh.RunId!.Value));
        await second.UntilAsync(s => s.Run?.State == "Succeeded", new(fresh.RunId));
    }

    [DataTestMethod]
    [DataRow("json")]
    [DataRow("sqlite")]
    public async Task StorageFault_PreservesComputedResultAndCorrelatesRequestAndRun(string store)
    {
        var host = await LaunchAsync(store: store, fault: "store");
        Guid requestId = Guid.NewGuid(), runId;
        try
        {
            AdmissionDto admission = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job()), requestId));
            runId = admission.RunId!.Value;
            StatusDto status = await host.UntilAsync(s => s.Run?.CompletedAtUtc is not null, new(runId));
            Assert.AreEqual("Faulted", status.Run!.State);
            Assert.AreEqual("Persist", status.Run.Stage);
            Assert.AreEqual(100.0, status.Run.ComputedResult!.Score);
            Assert.AreEqual("ResultNotFound", (await host.SendAsync("GetResult", new RunRequest(runId))).ErrorCode);
        }
        finally { await host.DisposeAsync(); }
        JsonElement[] events = Events(host.LogPath);
        Assert.IsTrue(events.Any(e => e.GetProperty("Event").GetString() == "RequestHandled" && e.GetProperty("RequestId").ValueKind == JsonValueKind.String
            && e.GetProperty("RequestId").GetGuid() == requestId && e.GetProperty("RunId").GetGuid() == runId));
        JsonElement completed = Completed(events, runId);
        Assert.AreEqual("Persist", completed.GetProperty("Data").GetProperty("Stage").GetString());
        Assert.IsTrue(completed.GetProperty("Errors").EnumerateArray().Any(e => e.GetProperty("Message").GetString() == "Injected persistence failure."));
    }

    [DataTestMethod]
    [DataRow("native-inspect", "Inspect", true)]
    [DataRow("native-wait", "Wait", false)]
    public async Task NativeFault_LogsOperationAndTerminationCertainty(string fault, string operation, bool confirmed)
    {
        var host = await LaunchAsync("native", store: "sqlite", fault: fault);
        Guid runId;
        try
        {
            runId = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job()))).RunId!.Value;
            StatusDto status = await host.UntilAsync(s => s.Run?.CompletedAtUtc is not null, new(runId));
            Assert.AreEqual("Faulted", status.Run!.State);
            Assert.AreEqual(confirmed, status.Run.TerminationConfirmed);
            Assert.AreEqual(confirmed ? "Ready" : "Faulted", status.State);
            Assert.AreEqual(0, Payload<ResultPageDto>(await host.SendAsync("SearchResults", new SearchResultsRequest())).Items.Length);
            if (!confirmed) { Assert.AreEqual("Unavailable", Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job()))).Disposition); }
        }
        finally { await host.DisposeAsync(); }
        JsonElement completed = Completed(Events(host.LogPath), runId);
        Assert.AreEqual(confirmed, completed.GetProperty("Data").GetProperty("TerminationConfirmed").GetBoolean());
        Assert.IsTrue(completed.GetProperty("Errors").EnumerateArray().Any(e => e.GetProperty("NativeOperation").GetString() == operation
            && e.GetProperty("NativeStatus").GetString() == "InternalError"));
    }

    [TestMethod]
    public async Task DroppedAdmissionResponse_IsTraceableAndReplayDoesNotDuplicateResult()
    {
        var host = await LaunchAsync(store: "sqlite", fault: "ipc-response");
        var request = InspectionConnection.CreateRequest("StartJob", new StartJobRequest(Job()));
        Guid runId;
        try
        {
            await using (var connection = await host.ConnectAsync())
            {
                try { await connection.SendAsync(request, host.Token); Assert.Fail("Injected response must be lost."); }
                catch (IOException) { }
            }
            AdmissionDto replay = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job()), request.RequestId));
            runId = replay.RunId!.Value;
            await host.UntilAsync(s => s.Run?.State == "Succeeded", new(runId));
            Assert.AreEqual(1, Payload<ResultPageDto>(await host.SendAsync("SearchResults", new SearchResultsRequest())).Items.Length);
        }
        finally { await host.DisposeAsync(); }
        JsonElement[] events = Events(host.LogPath);
        JsonElement drop = events.Single(e => e.GetProperty("Event").GetString() == "IpcResponseDropped");
        Assert.AreEqual(request.RequestId, drop.GetProperty("RequestId").GetGuid());
        Assert.AreEqual(runId, drop.GetProperty("RunId").GetGuid());
        Assert.IsTrue(events.Any(e => e.GetProperty("Event").GetString() == "StartReplayed"));
        Assert.AreEqual(1, events.Count(e => e.GetProperty("Event").GetString() == "RunCompleted"));
    }

    [TestMethod]
    public async Task MalformedIpc_LogsConnectionFailureAndLeavesServerUsable()
    {
        var host = await LaunchAsync(store: "sqlite");
        try
        {
            await using (var pipe = await host.RawAsync())
            {
                await pipe.WriteAsync(new byte[] { 0, 0, 0, 0 }, host.Token);
                try { Assert.AreEqual(0, await pipe.ReadAsync(new byte[1], host.Token)); } catch (IOException) { }
            }
            Assert.AreEqual("Ready", Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest())).State);
        }
        finally { await host.DisposeAsync(); }
        JsonElement failure = Events(host.LogPath).Single(e => e.GetProperty("Event").GetString() == "IpcTransportFailed");
        Assert.AreNotEqual(Guid.Empty, failure.GetProperty("ConnectionId").GetGuid());
        Assert.IsTrue(failure.GetProperty("Errors").EnumerateArray().Any(e => e.GetProperty("Type").GetString() == "System.IO.InvalidDataException"));
    }

    [TestMethod]
    public async Task LockedSqlite_RemainsControllableAndLogsDatabaseErrorCode()
    {
        var host = await LaunchAsync(store: "sqlite");
        Guid runId;
        try
        {
            using var connection = SqliteResultStoreTests.Open(Path.Combine(host.ResultsPath, "inspection.db"));
            using var transaction = connection.BeginTransaction();
            runId = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(Job()))).RunId!.Value;
            await host.UntilAsync(s => s.Run?.Stage == "Persist", new(runId));
            Assert.AreEqual("TooLate", Payload<ControlDto>(await host.SendAsync("CancelRun", new RunRequest(runId))).Disposition);
            StatusDto completed = await host.UntilAsync(s => s.Run?.CompletedAtUtc is not null, new(runId));
            Assert.AreEqual("Faulted", completed.Run!.State);
            Assert.AreEqual(100.0, completed.Run.ComputedResult!.Score);
            transaction.Rollback();
            Assert.AreEqual("ResultNotFound", (await host.SendAsync("GetResult", new RunRequest(runId))).ErrorCode);
        }
        finally { await host.DisposeAsync(); }
        JsonElement completedEvent = Completed(Events(host.LogPath), runId);
        Assert.IsTrue(completedEvent.GetProperty("Errors").EnumerateArray().Any(e => e.GetProperty("SqliteErrorCode").ValueKind == JsonValueKind.Number
            && e.GetProperty("SqliteErrorCode").GetInt32() == 5));
    }

    [TestMethod]
    public async Task Search_RejectsInvalidPayloadAndReportsJsonUnsupported()
    {
        await using (var sqlite = await LaunchAsync(store: "sqlite"))
        {
            foreach (var request in new[] { new SearchResultsRequest(Limit: 0), new SearchResultsRequest(Limit: 26), new SearchResultsRequest(Verdict: "fail"),
                new SearchResultsRequest(FromUtc: DateTimeOffset.UnixEpoch, ToUtc: DateTimeOffset.UnixEpoch), new SearchResultsRequest(Cursor: new(DateTimeOffset.UnixEpoch, Guid.Empty)) })
            {
                Assert.AreEqual("InvalidRequest", (await sqlite.SendAsync("SearchResults", request)).ErrorCode);
            }
            Assert.AreEqual(0, Payload<ResultPageDto>(await sqlite.SendAsync("SearchResults", new SearchResultsRequest())).Items.Length);
        }
        await using var json = await LaunchAsync();
        Assert.AreEqual("SearchNotSupported", (await json.SendAsync("SearchResults", new SearchResultsRequest())).ErrorCode);
    }

    [TestMethod]
    public async Task CorruptStoredIdentity_IsReadFailureAndDoesNotTerminateServer()
    {
        var host = await LaunchAsync(store: "sqlite");
        Guid requestId = Guid.NewGuid();
        try
        {
            using var connection = SqliteResultStoreTests.Open(Path.Combine(host.ResultsPath, "inspection.db"));
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO results VALUES('zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz','corrupt',0,0,1,0)";
            command.ExecuteNonQuery();
            Assert.AreEqual("StoreReadFailed", (await host.SendAsync("SearchResults", new SearchResultsRequest(), requestId)).ErrorCode);
            Assert.AreEqual("Ready", Payload<StatusDto>(await host.SendAsync("GetStatus", new GetStatusRequest())).State);
        }
        finally { await host.DisposeAsync(); }
        JsonElement failure = Events(host.LogPath).Single(e => e.GetProperty("Event").GetString() == "StoreReadFailed");
        Assert.AreEqual(requestId, failure.GetProperty("RequestId").GetGuid());
        Assert.IsTrue(failure.GetProperty("Errors").EnumerateArray().Any(e => e.GetProperty("Type").GetString() == "System.FormatException"));
    }
}
