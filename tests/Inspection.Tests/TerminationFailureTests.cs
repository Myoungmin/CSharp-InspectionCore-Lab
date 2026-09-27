using Inspection.Core;

namespace Inspection.Tests;

[TestClass]
public sealed class TerminationFailureTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [DataTestMethod]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    [DataRow(true, 3)]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(false, 3)]
    public async Task TerminationFailure_OverridesStopOutcomeAndKeepsEngineFaulted(bool confirmed, int cause)
    {
        var dependency = new FailingDependency(confirmed);
        var clock = new ManualTimeProvider();
        await using var engine = new InspectionEngine(new InspectionRunner(dependency, dependency, dependency), clock);
        var job = new InspectionJob("termination-job", [10], 0, 100);
        InspectionRunHandle run = engine.Start(job, TimeSpan.FromSeconds(1)).Run!;
        Task? disposal = null;
        try
        {
            await dependency.Entered.Task.WaitAsync(Limit);
            Assert.IsNull(engine.GetRun(run.RunId)!.TerminationConfirmed);
            if (cause == 1) { engine.CancelRun(run.RunId); }
            else if (cause == 2) { clock.Advance(TimeSpan.FromSeconds(1)); }
            else if (cause == 3) { disposal = engine.DisposeAsync().AsTask(); }
            Assert.IsFalse(run.Completion.IsCompleted);
        }
        finally { dependency.Release.TrySetResult(); }
        InspectionRunSnapshot completed = await run.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Faulted, completed.State);
        Assert.AreEqual(confirmed, completed.TerminationConfirmed);
        InspectionStopReason? expectedReason = cause switch
        {
            1 => InspectionStopReason.UserCancellation,
            2 => InspectionStopReason.Timeout,
            3 => InspectionStopReason.Shutdown,
            _ => null
        };
        Assert.AreEqual(expectedReason, completed.StopReason);
        Assert.AreEqual(InspectionEngineState.Faulted, engine.GetStatus().State);
        Assert.AreSame(dependency.Error, engine.GetStatus().Error);
        Assert.IsFalse(dependency.Saved);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, engine.Start(job).Disposition);
        if (disposal is not null) { await disposal.WaitAsync(Limit); }
        await engine.DisposeAsync();
        Assert.AreEqual(InspectionEngineState.Faulted, engine.GetStatus().State);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, engine.Start(job).Disposition);
    }

    private sealed class FailingDependency(bool confirmed) : IDevice, IInspector, IResultStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InspectionTerminationException Error { get; } = new(confirmed, new InvalidOperationException("Stop protocol failed."));
        internal bool Saved;
        public Task PrepareAsync(InspectionJob job, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<double[]> AcquireAsync(InspectionJob job, CancellationToken cancellationToken) => Task.FromResult(new double[] { 10 });
        public async Task<InspectionAssessment> InspectAsync(InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task;
            throw Error;
        }
        public Task SaveAsync(InspectionResult result, CancellationToken cancellationToken) { Saved = true; return Task.CompletedTask; }
    }
}
