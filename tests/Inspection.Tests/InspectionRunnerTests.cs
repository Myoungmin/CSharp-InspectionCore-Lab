using Inspection.Core;

namespace Inspection.Tests;

[TestClass]
public sealed class InspectionRunnerTests
{
    private static InspectionJob CreateJob() => new("job-1", [10, 20, 30, 40], 0, 100);

    [DataTestMethod]
    [DataRow(0, InspectionVerdict.Pass, 100.0)]
    [DataRow(1, InspectionVerdict.Fail, 75.0)]
    public async Task RunAsync_ReturnsOnlyAfterAllStagesIncludingStorage(int defects, InspectionVerdict verdict, double score)
    {
        var rig = new TestRig { Assessment = new InspectionAssessment(4, defects) };
        var time = new FixedTimeProvider();
        var runner = new InspectionRunner(rig, rig, rig, time);

        InspectionResult result = await runner.RunAsync(CreateJob());

        CollectionAssert.AreEqual(new[] { "Prepare", "Acquire", "Inspect", "Persist" }, rig.Calls);
        Assert.AreSame(result, rig.SavedResult);
        Assert.AreEqual(verdict, result.Assessment.Verdict);
        Assert.AreEqual(score, result.Assessment.Score);
        Assert.AreEqual(time.GetUtcNow(), result.StartedAtUtc);
        Assert.AreEqual(time.GetUtcNow(), result.InspectedAtUtc);
        Assert.AreNotEqual(Guid.Empty, result.RunId);
        Assert.IsFalse(rig.Disposed, "The runner must not dispose borrowed dependencies.");
    }

    [DataTestMethod]
    [DataRow(InspectionStage.Prepare, 1)]
    [DataRow(InspectionStage.Acquire, 2)]
    [DataRow(InspectionStage.Inspect, 3)]
    [DataRow(InspectionStage.Persist, 4)]
    public async Task Failure_StopsLaterStagesAndPreservesCause(InspectionStage failureStage, int expectedCallCount)
    {
        var cause = new IOException("Injected failure.");
        var rig = new TestRig { FailAt = failureStage, Failure = cause };

        var error = await Assert.ThrowsExceptionAsync<InspectionRunException>(
            () => new InspectionRunner(rig, rig, rig).RunAsync(CreateJob()));

        Assert.AreEqual(failureStage, error.Stage);
        Assert.AreSame(cause, error.InnerException);
        Assert.AreNotEqual(Guid.Empty, error.RunId);
        CollectionAssert.AreEqual(new[] { "Prepare", "Acquire", "Inspect", "Persist" }[..expectedCallCount], rig.Calls);
        Assert.IsNull(rig.SavedResult);
        if (failureStage == InspectionStage.Persist)
        {
            Assert.IsNotNull(error.ComputedResult);
            Assert.AreEqual(error.RunId, error.ComputedResult.RunId);
            Assert.AreEqual(100.0, error.ComputedResult.Assessment.Score);
        }
        else
        {
            Assert.IsNull(error.ComputedResult);
        }
    }

    [TestMethod]
    public async Task AlreadyCanceled_DoesNotCallDevice()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var rig = new TestRig();

        var error = await Assert.ThrowsExceptionAsync<InspectionCanceledException>(
            () => new InspectionRunner(rig, rig, rig).RunAsync(CreateJob(), cancellation.Token));

        Assert.AreEqual(0, rig.Calls.Count);
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreNotEqual(Guid.Empty, error.RunId);
    }

    [TestMethod]
    public async Task CancellationDuringAcquisition_DoesNotInspectOrSave()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new TestRig
        {
            OnAcquire = async token =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };

        Task<InspectionResult> run = new InspectionRunner(rig, rig, rig).RunAsync(CreateJob(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var error = await Assert.ThrowsExceptionAsync<InspectionCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(InspectionStage.Acquire, error.Stage);
        CollectionAssert.AreEqual(new[] { "Prepare", "Acquire" }, rig.Calls);
    }

    [TestMethod]
    public async Task CancellationBeforePersistence_PreventsSaveEvenIfInspectorReturnsNormally()
    {
        using var cancellation = new CancellationTokenSource();
        var rig = new TestRig { AfterInspect = cancellation.Cancel };

        await Assert.ThrowsExceptionAsync<InspectionCanceledException>(
            () => new InspectionRunner(rig, rig, rig).RunAsync(CreateJob(), cancellation.Token));

        CollectionAssert.AreEqual(new[] { "Prepare", "Acquire", "Inspect" }, rig.Calls);
        Assert.IsNull(rig.SavedResult);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationAfterPersistenceStarts_DoesNotOverrideStorageOutcome(bool storageFails)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new TestRig
        {
            OnSave = async token =>
            {
                Assert.IsFalse(token.CanBeCanceled);
                entered.SetResult();
                await release.Task;
                if (storageFails) { throw new IOException("Disk failed after cancellation."); }
            }
        };

        Task<InspectionResult> run = new InspectionRunner(rig, rig, rig).RunAsync(CreateJob(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.IsFalse(run.IsCompleted, "A caller must wait for the persistence outcome.");
        release.SetResult();

        if (storageFails)
        {
            var error = await Assert.ThrowsExceptionAsync<InspectionRunException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(InspectionStage.Persist, error.Stage);
            Assert.IsNotNull(error.ComputedResult);
            Assert.IsInstanceOfType<IOException>(error.InnerException);
        }
        else
        {
            InspectionResult result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(result, rig.SavedResult);
        }
    }

    [TestMethod]
    public async Task UnrelatedOperationCanceledException_IsAnExecutionFailure()
    {
        var cause = new OperationCanceledException("Dependency canceled its own work.");
        var rig = new TestRig { FailAt = InspectionStage.Inspect, Failure = cause };

        var error = await Assert.ThrowsExceptionAsync<InspectionRunException>(
            () => new InspectionRunner(rig, rig, rig).RunAsync(CreateJob()));

        Assert.AreSame(cause, error.InnerException);
        Assert.AreEqual(InspectionStage.Inspect, error.Stage);
        Assert.IsNull(rig.SavedResult);
    }

    [TestMethod]
    public async Task RepeatedJobDefinition_GetsDistinctRunIds()
    {
        var rig = new TestRig();
        var runner = new InspectionRunner(rig, rig, rig);
        InspectionJob job = CreateJob();

        InspectionResult first = await runner.RunAsync(job);
        InspectionResult second = await runner.RunAsync(job);

        Assert.AreEqual(first.JobId, second.JobId);
        Assert.AreNotEqual(first.RunId, second.RunId);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestRig : IDevice, IInspector, IResultStore, IDisposable
    {
        public List<string> Calls { get; } = [];
        public InspectionAssessment Assessment { get; init; } = new(4, 0);
        public InspectionStage? FailAt { get; init; }
        public Exception Failure { get; init; } = new IOException("Injected failure.");
        public Func<CancellationToken, Task>? OnAcquire { get; init; }
        public Action? AfterInspect { get; init; }
        public Func<CancellationToken, Task>? OnSave { get; init; }
        public InspectionResult? SavedResult { get; private set; }
        public bool Disposed { get; private set; }

        public Task PrepareAsync(InspectionJob job, CancellationToken cancellationToken)
        {
            Visit(InspectionStage.Prepare);
            return Task.CompletedTask;
        }

        public async Task<double[]> AcquireAsync(InspectionJob job, CancellationToken cancellationToken)
        {
            Visit(InspectionStage.Acquire);
            if (OnAcquire is not null) { await OnAcquire(cancellationToken); }
            return job.Samples.ToArray();
        }

        public Task<InspectionAssessment> InspectAsync(InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken cancellationToken)
        {
            Visit(InspectionStage.Inspect);
            AfterInspect?.Invoke();
            return Task.FromResult(Assessment);
        }

        public async Task SaveAsync(InspectionResult result, CancellationToken cancellationToken)
        {
            Visit(InspectionStage.Persist);
            if (OnSave is not null) { await OnSave(cancellationToken); }
            SavedResult = result;
        }

        public void Dispose() => Disposed = true;

        private void Visit(InspectionStage stage)
        {
            Calls.Add(stage.ToString());
            if (FailAt == stage) { throw Failure; }
        }
    }
}
