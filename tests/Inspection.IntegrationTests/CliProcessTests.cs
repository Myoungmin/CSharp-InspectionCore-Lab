using System.Text.Json;
using Inspection.Client;
using Inspection.Contracts;
using static Inspection.IntegrationTests.IpcProcessFixture;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("CppCli")]
public sealed class CliProcessTests
{
    [DataTestMethod]
    [DataRow("json")]
    [DataRow("sqlite")]
    public async Task SeparateClient_AutoFailPersistsInSelectedStore(string store)
    {
        await using var host = await LaunchAsync("cli", store: store);
        var request = InspectionConnection.CreateRequest("StartAuto", new StartAutoRequest(new("cli-ipc", [1, 99], 0, 10), 0, MaxRuns: 3));
        string requestPath = Path.Combine(host.DirectoryPath, "start.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request));
        using var client = host.Start("Inspection.Client", "--pipe", host.PipeName, "--request", requestPath);
        Task<string> stdout = client.StandardOutput.ReadToEndAsync(), stderr = client.StandardError.ReadToEndAsync();
        AdmissionDto admission;
        try
        {
            await client.WaitForExitAsync(host.Token);
            Assert.AreEqual(0, client.ExitCode, await stderr);
            string response = await stdout;
            await File.WriteAllTextAsync(Path.Combine(host.DirectoryPath, "client.stdout.log"), response);
            admission = Payload<AdmissionDto>(JsonSerializer.Deserialize<ResponseEnvelope>(response)!);
        }
        finally { if (!client.HasExited) { client.Kill(entireProcessTree: true); await client.WaitForExitAsync(); } }
        StatusDto status = await host.UntilAsync(s => s.Auto?.State == "Completed", new(AutoId: admission.AutoId));
        Assert.AreEqual(3L, status.Auto!.CompletedRuns);
        Assert.AreEqual("Succeeded", status.Auto.LastRun!.State);
        ResultDto result = Payload<ResultDto>(await host.SendAsync("GetResult", new RunRequest(status.Auto.LastRun.RunId)));
        Assert.AreEqual("Fail", result.Verdict);
        if (store == "sqlite")
        {
            Assert.AreEqual(3, Payload<ResultPageDto>(await host.SendAsync("SearchResults", new SearchResultsRequest())).Items.Length);
        }
        else { Assert.AreEqual(3, Directory.GetFiles(host.ResultsPath, "*.json").Length); }
    }

    [DataTestMethod]
    [DataRow("native-inspect", "Inspect", true)]
    [DataRow("native-wait", "Wait", false)]
    [DataRow("store", "Persist", true)]
    public async Task CliFaults_PreserveRemoteOutcomeAndStructuredCause(string fault, string operation, bool confirmed)
    {
        var host = await LaunchAsync("cli", store: "sqlite", fault: fault);
        Guid id;
        try
        {
            id = Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(new("cli-fault", [1], 0, 10)))).RunId!.Value;
            StatusDto status = await host.UntilAsync(s => s.Run?.CompletedAtUtc is not null, new(id));
            Assert.AreEqual("Faulted", status.Run!.State);
            Assert.AreEqual(confirmed, status.Run.TerminationConfirmed);
            Assert.AreEqual(fault == "store" ? "Persist" : "Inspect", status.Run.Stage);
            if (fault == "store") { Assert.AreEqual(100.0, status.Run.ComputedResult!.Score); }
            if (!confirmed)
            {
                Assert.AreEqual("Faulted", status.State);
                Assert.AreEqual("Unavailable", Payload<AdmissionDto>(await host.SendAsync("StartJob", new StartJobRequest(new("next", [1], 0, 10)))).Disposition);
            }
            Assert.AreEqual("ResultNotFound", (await host.SendAsync("GetResult", new RunRequest(id))).ErrorCode);
        }
        finally { await host.DisposeAsync(); }
        var events = File.ReadAllLines(host.LogPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        JsonElement completed = events.Single(e => e.GetProperty("Event").GetString() == "RunCompleted" && e.GetProperty("RunId").GetGuid() == id);
        Assert.AreEqual(confirmed, completed.GetProperty("Data").GetProperty("TerminationConfirmed").GetBoolean());
        Assert.IsTrue(completed.GetProperty("Errors").EnumerateArray().Any(e => fault == "store"
            ? e.GetProperty("Type").GetString() == "System.IO.IOException"
            : e.GetProperty("NativeOperation").GetString() == operation && e.GetProperty("NativeStatusCode").GetInt32() == 3));
    }
}
