using System.Text.Json;
using Inspection.Core;
using Inspection.Infrastructure;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("File")]
public sealed class JsonResultStoreTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), "InspectionLabTests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InspectionLabTests")) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(_directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Invalid test cleanup path."); }
        if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
    }

    [TestMethod]
    public async Task Save_PublishesCompleteJsonAndRemovesTemporaryFile()
    {
        var store = new JsonResultStore(_directory);
        InspectionResult result = await new InspectionRunner(new SimulatedDevice(), new RangeInspector(), store)
            .RunAsync(new InspectionJob("file-job", [0, 100, 101], 0, 100));
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(store.GetResultPath(result.RunId)));
        Assert.AreEqual(result.RunId, json.RootElement.GetProperty("RunId").GetGuid());
        Assert.AreEqual("Fail", json.RootElement.GetProperty("Assessment").GetProperty("Verdict").GetString());
        Assert.AreEqual(66.67, json.RootElement.GetProperty("Assessment").GetProperty("Score").GetDouble());
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }

    [TestMethod]
    public async Task DuplicateRunId_DoesNotOverwriteOriginalOrLeaveTemporaryFile()
    {
        var store = new JsonResultStore(_directory);
        var result = new InspectionResult(Guid.NewGuid(), "original", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new InspectionAssessment(1, 0));
        await store.SaveAsync(result, CancellationToken.None);
        string original = await File.ReadAllTextAsync(store.GetResultPath(result.RunId));

        await Assert.ThrowsExceptionAsync<IOException>(() => store.SaveAsync(result with { JobId = "replacement" }, CancellationToken.None));

        Assert.AreEqual(original, await File.ReadAllTextAsync(store.GetResultPath(result.RunId)));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
    }

    [TestMethod]
    public async Task CanceledSave_DoesNotCreateOutputDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = new InspectionResult(Guid.NewGuid(), "canceled", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new InspectionAssessment(1, 0));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => new JsonResultStore(_directory).SaveAsync(result, cancellation.Token));
        Assert.IsFalse(Directory.Exists(_directory));
    }
}
