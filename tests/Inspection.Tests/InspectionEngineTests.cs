using Inspection.Core;

namespace Inspection.Tests;

[TestClass]
public sealed class InspectionEngineTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static InspectionJob Job() => new("engine-job", [10, 20, 30, 40], 0, 100);

    [DataTestMethod]
    [DataRow(0, InspectionVerdict.Pass)]
    [DataRow(1, InspectionVerdict.Fail)]
    public async Task AcceptedRun_PublishesSameRunIdOnlyAfterStorage(int defects, InspectionVerdict verdict)
    {
        await using var rig = new Rig { Defects = defects };
        Gate store = rig.Block(InspectionStage.Persist);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job()));
        await store.Entered.Task.WaitAsync(Limit);

        InspectionRunSnapshot inProgress = rig.Engine.GetRun(handle.RunId)!;
        Assert.AreEqual(InspectionRunState.Persisting, inProgress.State);
        Assert.IsNotNull(inProgress.ComputedResult);
        Assert.AreEqual(handle.RunId, inProgress.ComputedResult.RunId);
        Assert.IsFalse(handle.Completion.IsCompleted);
        Assert.IsTrue(rig.Engine.GetStatus().IsBusy);
        Assert.IsNull(inProgress.CompletedAtUtc);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        store.Release();

        InspectionRunSnapshot result = await handle.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Succeeded, result.State);
        Assert.AreEqual(verdict, result.ComputedResult!.Assessment.Verdict);
        Assert.AreSame(rig.Saved, result.ComputedResult);
        Assert.AreEqual(rig.Clock.GetUtcNow(), result.CompletedAtUtc);
        Assert.IsFalse(rig.Engine.GetStatus().IsBusy);
        Assert.AreEqual(InspectionRunState.Persisting, inProgress.State, "Earlier snapshots must not mutate.");
        Assert.AreSame(result, rig.Engine.GetRun(handle.RunId));
        Assert.IsFalse(rig.DependencyDisposed);
    }

    [DataTestMethod]
    [DataRow(InspectionStage.Prepare)]
    [DataRow(InspectionStage.Acquire)]
    [DataRow(InspectionStage.Inspect)]
    public async Task Status_ReportsStageOfActualBlockedWork(InspectionStage stage)
    {
        await using var rig = new Rig();
        Gate blocked = rig.Block(stage);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job()));
        await blocked.Entered.Task.WaitAsync(Limit);
        Assert.AreEqual(stage, rig.Engine.GetRun(handle.RunId)!.Stage);
        Assert.AreEqual(InspectionRunState.Running, rig.Engine.GetStatus().Run!.State);
        Assert.IsFalse(handle.Completion.IsCompleted);
        blocked.Release();
        Assert.AreEqual(InspectionRunState.Succeeded, (await handle.Completion.WaitAsync(Limit)).State);
    }

    [TestMethod]
    public async Task ConcurrentStarts_AdmitExactlyOneWithoutQueuing()
    {
        await using var rig = new Rig();
        Gate prepare = rig.Block(InspectionStage.Prepare);
        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<InspectionStartResult>[] callers = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            await startSignal.Task;
            return rig.Engine.Start(Job());
        })).ToArray();
        startSignal.SetResult();
        InspectionStartResult[] results = await Task.WhenAll(callers).WaitAsync(Limit);
        InspectionRunHandle winner = Accepted(results.Single(result => result.Disposition == InspectionStartDisposition.Accepted));
        Assert.AreEqual(23, results.Count(result => result.Disposition == InspectionStartDisposition.Busy));
        foreach (InspectionStartResult rejected in results.Where(result => result.Disposition == InspectionStartDisposition.Busy))
        {
            Assert.IsNull(rejected.Run);
            Assert.AreEqual(winner.RunId, rejected.BusyRunId);
        }
        await prepare.Entered.Task.WaitAsync(Limit);
        CollectionAssert.AreEqual(new[] { InspectionStage.Prepare }, rig.Calls);
        prepare.Release();
        await winner.Completion.WaitAsync(Limit);
        Assert.AreEqual(4, rig.Calls.Length);
    }

    [TestMethod]
    public async Task Cancel_IgnoringDependencyKeepsSlotUntilActualReturn()
    {
        await using var rig = new Rig();
        Gate acquire = rig.Block(InspectionStage.Acquire);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job()));
        await acquire.Entered.Task.WaitAsync(Limit);
        InspectionRunSnapshot before = rig.Engine.GetRun(handle.RunId)!;

        Assert.AreEqual(InspectionCancelDisposition.Accepted, rig.Engine.CancelRun(handle.RunId));
        Assert.AreEqual(InspectionCancelDisposition.AlreadyRequested, rig.Engine.CancelRun(handle.RunId));
        InspectionRunSnapshot requested = rig.Engine.GetRun(handle.RunId)!;
        Assert.AreEqual(InspectionRunState.CancelRequested, requested.State);
        Assert.AreEqual(InspectionStopReason.UserCancellation, requested.StopReason);
        Assert.AreEqual(InspectionRunState.Running, before.State);
        Assert.IsFalse(handle.Completion.IsCompleted);
        Assert.AreEqual(InspectionStartDisposition.Busy, rig.Engine.Start(Job()).Disposition);
        acquire.Release();

        InspectionRunSnapshot final = await handle.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Canceled, final.State);
        CollectionAssert.AreEqual(new[] { InspectionStage.Prepare, InspectionStage.Acquire }, rig.Calls);
        Assert.IsNull(rig.Saved);
        Assert.IsFalse(rig.Engine.GetStatus().IsBusy);
    }

    [TestMethod]
    public async Task Timeout_UsesControlledTimeAndWaitsForActualReturn()
    {
        await using var rig = new Rig();
        Gate acquire = rig.Block(InspectionStage.Acquire);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job(), TimeSpan.FromSeconds(10)));
        await acquire.Entered.Task.WaitAsync(Limit);
        rig.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.AreEqual(InspectionRunState.Running, rig.Engine.GetStatus().Run!.State);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(InspectionRunState.CancelRequested, rig.Engine.GetStatus().Run!.State);
        Assert.AreEqual(InspectionStopReason.Timeout, rig.Engine.GetStatus().Run!.StopReason);
        Assert.IsFalse(handle.Completion.IsCompleted);
        Assert.AreEqual(InspectionStartDisposition.Busy, rig.Engine.Start(Job()).Disposition);
        acquire.Release();
        InspectionRunSnapshot final = await handle.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.TimedOut, final.State);
        Assert.IsNull(rig.Saved);
        Assert.AreEqual(0, rig.Clock.ActiveTimerCount);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FirstStopReason_WinsUserCancellationTimeoutRace(bool userFirst)
    {
        await using var rig = new Rig();
        Gate acquire = rig.Block(InspectionStage.Acquire);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job(), TimeSpan.FromSeconds(1)));
        await acquire.Entered.Task.WaitAsync(Limit);
        if (userFirst) { Assert.AreEqual(InspectionCancelDisposition.Accepted, rig.Engine.CancelRun(handle.RunId)); }
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(InspectionCancelDisposition.AlreadyRequested, rig.Engine.CancelRun(handle.RunId));
        acquire.Release();
        InspectionRunSnapshot result = await handle.Completion.WaitAsync(Limit);
        Assert.AreEqual(userFirst ? InspectionRunState.Canceled : InspectionRunState.TimedOut, result.State);
        Assert.AreEqual(userFirst ? InspectionStopReason.UserCancellation : InspectionStopReason.Timeout, result.StopReason);
    }

    [TestMethod]
    public async Task CancellationAtInspectionReturn_PreventsPersistence()
    {
        await using var rig = new Rig();
        rig.AfterInspect = () => Assert.AreEqual(InspectionCancelDisposition.Accepted,
            rig.Engine.CancelRun(rig.Engine.GetStatus().Run!.RunId));
        InspectionRunSnapshot result = await Accepted(rig.Engine.Start(Job())).Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Canceled, result.State);
        Assert.IsNotNull(result.ComputedResult);
        Assert.IsNull(rig.Saved);
        CollectionAssert.AreEqual(new[] { InspectionStage.Prepare, InspectionStage.Acquire, InspectionStage.Inspect }, rig.Calls);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Persisting_RejectsCancelAndTimeoutAndPreservesStorageOutcome(bool storeFails)
    {
        await using var rig = new Rig { StoreFails = storeFails };
        Gate store = rig.Block(InspectionStage.Persist);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job(), TimeSpan.FromSeconds(1)));
        await store.Entered.Task.WaitAsync(Limit);
        Assert.AreEqual(InspectionCancelDisposition.TooLate, rig.Engine.CancelRun(handle.RunId));
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(rig.Engine.GetStatus().Run!.StopReason);
        Assert.IsFalse(handle.Completion.IsCompleted);
        store.Release();
        InspectionRunSnapshot result = await handle.Completion.WaitAsync(Limit);
        Assert.AreEqual(storeFails ? InspectionRunState.Faulted : InspectionRunState.Succeeded, result.State);
        Assert.IsNotNull(result.ComputedResult);
        Assert.AreEqual(100d, result.ComputedResult.Assessment.Score);
        if (storeFails)
        {
            Assert.IsInstanceOfType<InspectionRunException>(result.Error);
            Assert.AreEqual(InspectionStage.Persist, ((InspectionRunException)result.Error).Stage);
        }
        Assert.AreEqual(InspectionEngineState.Ready, rig.Engine.GetStatus().State);
    }

    [TestMethod]
    public async Task ExecutionFailure_IsFaultedAndAllowsAnotherRun()
    {
        await using var rig = new Rig { AfterAcquireError = new OperationCanceledException("Dependency-local cancellation.") };
        InspectionRunSnapshot failed = await Accepted(rig.Engine.Start(Job())).Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Faulted, failed.State);
        Assert.IsNull(failed.StopReason);
        Assert.IsInstanceOfType<InspectionRunException>(failed.Error);
        Assert.AreSame(rig.AfterAcquireError, failed.Error.InnerException);
        rig.AfterAcquireError = null;
        InspectionRunHandle next = Accepted(rig.Engine.Start(Job()));
        Assert.AreNotEqual(failed.RunId, next.RunId);
        Assert.AreEqual(InspectionRunState.Succeeded, (await next.Completion.WaitAsync(Limit)).State);
    }

    [TestMethod]
    public async Task AcceptedCancellation_PreservesCauseAndRacingFaultForDiagnostics()
    {
        var cause = new IOException("Dependency failed while cancellation was pending.");
        await using var rig = new Rig { AfterAcquireError = cause };
        Gate acquire = rig.Block(InspectionStage.Acquire);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job()));
        await acquire.Entered.Task.WaitAsync(Limit);
        rig.Engine.CancelRun(handle.RunId);
        acquire.Release();
        InspectionRunSnapshot result = await handle.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Canceled, result.State);
        Assert.AreSame(cause, result.Error!.InnerException);
        Assert.IsNull(rig.Saved);
    }

    [TestMethod]
    public async Task ZeroTimeout_DoesNotCallDevice()
    {
        await using var rig = new Rig();
        InspectionRunSnapshot result = await Accepted(rig.Engine.Start(Job(), TimeSpan.Zero)).Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.TimedOut, result.State);
        Assert.AreEqual(InspectionStopReason.Timeout, result.StopReason);
        Assert.AreEqual(0, rig.Calls.Length);
    }

    [DataTestMethod]
    [DataRow(-2L)]
    [DataRow(4294967295L)]
    public async Task InvalidTimeout_DoesNotReserveSlot(long milliseconds)
    {
        await using var rig = new Rig();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => rig.Engine.Start(Job(), TimeSpan.FromMilliseconds(milliseconds)));
        Assert.ThrowsException<ArgumentNullException>(() => rig.Engine.Start(null!));
        Assert.IsFalse(rig.Engine.GetStatus().IsBusy);
        Assert.AreEqual(InspectionRunState.Succeeded,
            (await Accepted(rig.Engine.Start(Job(), Timeout.InfiniteTimeSpan)).Completion.WaitAsync(Limit)).State);
    }

    [TestMethod]
    public async Task PreviousRunAndLateTimer_CannotCancelNextRun()
    {
        await using var rig = new Rig();
        InspectionRunHandle first = Accepted(rig.Engine.Start(Job(), TimeSpan.FromSeconds(10)));
        await first.Completion.WaitAsync(Limit);
        ManualTimeProvider.ManualTimer oldTimer = rig.Clock.LastTimer!;
        Assert.IsTrue(oldTimer.Disposed);
        Gate acquire = rig.Block(InspectionStage.Acquire);
        InspectionRunHandle second = Accepted(rig.Engine.Start(Job()));
        await acquire.Entered.Task.WaitAsync(Limit);
        Assert.AreNotEqual(first.RunId, second.RunId);
        Assert.AreEqual(first.JobId, second.JobId);
        Assert.AreEqual(InspectionCancelDisposition.TooLate, rig.Engine.CancelRun(first.RunId));
        Assert.AreEqual(InspectionCancelDisposition.NotFound, rig.Engine.CancelRun(Guid.NewGuid()));
        Assert.IsNull(rig.Engine.GetRun(Guid.NewGuid()));
        oldTimer.Fire();
        Assert.AreEqual(InspectionRunState.Running, rig.Engine.GetRun(second.RunId)!.State);
        acquire.Release();
        Assert.AreEqual(InspectionRunState.Succeeded, (await second.Completion.WaitAsync(Limit)).State);
        Assert.IsNull(rig.Engine.GetRun(first.RunId), "Only the current and last completed runs are retained.");
        Assert.AreEqual(InspectionRunState.Succeeded, first.Completion.Result.State);
    }

    [TestMethod]
    public async Task TimerSetupFailure_DoesNotLeaveEngineBusy()
    {
        await using var rig = new Rig();
        rig.Clock.ThrowOnCreate = true;
        Assert.ThrowsException<InvalidOperationException>(() => rig.Engine.Start(Job(), TimeSpan.FromSeconds(1)));
        Assert.IsFalse(rig.Engine.GetStatus().IsBusy);
        Assert.AreEqual(0, rig.Calls.Length);
        rig.Clock.ThrowOnCreate = false;
        Assert.AreEqual(InspectionRunState.Succeeded,
            (await Accepted(rig.Engine.Start(Job(), TimeSpan.FromSeconds(1))).Completion.WaitAsync(Limit)).State);
    }

    [TestMethod]
    public async Task Dispose_StopsAdmissionAndWaitsWithoutDisposingBorrowedDependencies()
    {
        await using var rig = new Rig();
        Gate acquire = rig.Block(InspectionStage.Acquire);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job()));
        await acquire.Entered.Task.WaitAsync(Limit);
        Task disposal = rig.Engine.DisposeAsync().AsTask();
        Assert.AreEqual(InspectionEngineState.Stopping, rig.Engine.GetStatus().State);
        Assert.AreEqual(InspectionStopReason.Shutdown, rig.Engine.GetStatus().Run!.StopReason);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, rig.Engine.Start(Job()).Disposition);
        Assert.IsFalse(disposal.IsCompleted);
        acquire.Release();
        await disposal.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Canceled, (await handle.Completion.WaitAsync(Limit)).State);
        Assert.AreEqual(InspectionEngineState.Disposed, rig.Engine.GetStatus().State);
        Assert.IsFalse(rig.DependencyDisposed);
        Assert.AreSame(disposal, rig.Engine.DisposeAsync().AsTask());
        Assert.AreEqual(InspectionStartDisposition.Unavailable, rig.Engine.Start(Job()).Disposition);
    }

    [TestMethod]
    public async Task DisposeDuringPersistence_WaitsForSavedOutcome()
    {
        await using var rig = new Rig();
        Gate store = rig.Block(InspectionStage.Persist);
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job()));
        await store.Entered.Task.WaitAsync(Limit);
        Task disposal = rig.Engine.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(InspectionRunState.Persisting, rig.Engine.GetStatus().Run!.State);
        Assert.IsNull(rig.Engine.GetStatus().Run!.StopReason);
        store.Release();
        await disposal.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Succeeded, (await handle.Completion.WaitAsync(Limit)).State);
        Assert.IsNotNull(rig.Saved);
        Assert.IsFalse(rig.DependencyDisposed);
    }

    [TestMethod]
    public async Task CancellationCallbacks_MustFinishBeforeCompletionAndSlotRelease()
    {
        await using var rig = new Rig();
        Gate acquire = rig.Block(InspectionStage.Acquire);
        Gate callback = rig.CreateGate();
        InspectionRunSnapshot? observed = null;
        rig.OnAcquire = token => token.Register(() =>
        {
            observed = rig.Engine.GetStatus().Run;
            callback.EnterAsync().GetAwaiter().GetResult();
        });
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job()));
        await acquire.Entered.Task.WaitAsync(Limit);
        Assert.AreEqual(InspectionCancelDisposition.Accepted, rig.Engine.CancelRun(handle.RunId));
        await callback.Entered.Task.WaitAsync(Limit);
        acquire.Release();
        await rig.AcquireReturned.Task.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.CancelRequested, observed!.State);
        Assert.IsFalse(handle.Completion.IsCompleted);
        Assert.AreEqual(InspectionStartDisposition.Busy, rig.Engine.Start(Job()).Disposition);
        callback.Release();
        Assert.AreEqual(InspectionRunState.Canceled, (await handle.Completion.WaitAsync(Limit)).State);
    }

    [TestMethod]
    public async Task ThrowingCancellationCallback_FaultsEngineAndRejectsNewRuns()
    {
        await using var rig = new Rig();
        Gate acquire = rig.Block(InspectionStage.Acquire);
        rig.OnAcquire = token => token.Register(() => throw new IOException("Stop callback failed."));
        InspectionRunHandle handle = Accepted(rig.Engine.Start(Job()));
        await acquire.Entered.Task.WaitAsync(Limit);
        rig.Engine.CancelRun(handle.RunId);
        acquire.Release();
        InspectionRunSnapshot result = await handle.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionRunState.Faulted, result.State);
        Assert.AreEqual(InspectionStopReason.UserCancellation, result.StopReason);
        Assert.IsInstanceOfType<AggregateException>(result.StopError);
        Assert.AreEqual(InspectionEngineState.Faulted, rig.Engine.GetStatus().State);
        Assert.AreSame(result.StopError, rig.Engine.GetStatus().Error);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, rig.Engine.Start(Job()).Disposition);
        Assert.IsFalse(rig.DependencyDisposed);
    }

    [TestMethod]
    public async Task DisposeIdle_IsIdempotentAndRejectsAdmission()
    {
        await using var rig = new Rig();
        await rig.Engine.DisposeAsync();
        await rig.Engine.DisposeAsync();
        Assert.AreEqual(InspectionEngineState.Disposed, rig.Engine.GetStatus().State);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, rig.Engine.Start(Job()).Disposition);
        Assert.IsNull(rig.Engine.GetStatus().Run);
        Assert.AreEqual(0, rig.Calls.Length);
    }

    private static InspectionRunHandle Accepted(InspectionStartResult result)
    {
        Assert.AreEqual(InspectionStartDisposition.Accepted, result.Disposition);
        Assert.IsNotNull(result.Run);
        Assert.IsNull(result.BusyRunId);
        return result.Run;
    }

    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task EnterAsync() { Entered.TrySetResult(); await _release.Task; }
        internal void Release() => _release.TrySetResult();
    }

    private sealed class Rig : IDevice, IInspector, IResultStore, IDisposable, IAsyncDisposable
    {
        private readonly List<Gate> _gates = [];
        private readonly Dictionary<InspectionStage, Gate> _blocked = [];
        private readonly List<InspectionStage> _calls = [];
        internal ManualTimeProvider Clock { get; } = new();
        internal InspectionEngine Engine { get; }
        internal int Defects { get; init; }
        internal bool StoreFails { get; init; }
        internal Action<CancellationToken>? OnAcquire { get; set; }
        internal Action? AfterInspect { get; set; }
        internal Exception? AfterAcquireError { get; set; }
        internal InspectionResult? Saved { get; private set; }
        internal bool DependencyDisposed { get; private set; }
        internal TaskCompletionSource AcquireReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InspectionStage[] Calls { get { lock (_calls) { return _calls.ToArray(); } } }

        internal Rig() { Engine = new(new InspectionRunner(this, this, this, Clock), Clock); }
        internal Gate CreateGate() { var gate = new Gate(); _gates.Add(gate); return gate; }
        internal Gate Block(InspectionStage stage) { Gate gate = CreateGate(); _blocked[stage] = gate; return gate; }

        private Task Visit(InspectionStage stage)
        {
            lock (_calls) { _calls.Add(stage); }
            return _blocked.TryGetValue(stage, out Gate? gate) ? gate.EnterAsync() : Task.CompletedTask;
        }

        public Task PrepareAsync(InspectionJob job, CancellationToken cancellationToken) => Visit(InspectionStage.Prepare);
        public async Task<double[]> AcquireAsync(InspectionJob job, CancellationToken cancellationToken)
        {
            OnAcquire?.Invoke(cancellationToken);
            await Visit(InspectionStage.Acquire);
            AcquireReturned.TrySetResult();
            if (AfterAcquireError is not null) { throw AfterAcquireError; }
            return job.Samples.ToArray();
        }

        public async Task<InspectionAssessment> InspectAsync(InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken cancellationToken)
        {
            await Visit(InspectionStage.Inspect);
            AfterInspect?.Invoke();
            return new(samples.Length, Defects);
        }

        public async Task SaveAsync(InspectionResult result, CancellationToken cancellationToken)
        {
            Assert.IsFalse(cancellationToken.CanBeCanceled);
            await Visit(InspectionStage.Persist);
            if (StoreFails) { throw new IOException("Storage failed."); }
            Saved = result;
        }

        public void Dispose() => DependencyDisposed = true;
        public async ValueTask DisposeAsync()
        {
            // Release test-controlled work even when an assertion fails, then observe real engine shutdown.
            foreach (Gate gate in _gates) { gate.Release(); }
            await Engine.DisposeAsync();
        }
    }
}
