using System.Collections.Concurrent;
using Inspection.Core;

namespace Inspection.Tests;

[TestClass]
public sealed class InspectionAutoTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static InspectionJob Job() => new("auto-job", [10, 20], 0, 100);

    [DataTestMethod]
    [DataRow(0, InspectionVerdict.Pass)]
    [DataRow(1, InspectionVerdict.Fail)]
    public async Task FiniteSequence_PersistsEveryRunWithDistinctRunIds(int defects, InspectionVerdict verdict)
    {
        await using var rig = new Rig { Defects = defects };
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), TimeSpan.Zero, maxRuns: 3));
        InspectionAutoSnapshot result = await auto.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoState.Completed, result.State);
        Assert.AreEqual(InspectionAutoStopReason.RunLimitReached, result.StopReason);
        Assert.AreEqual(3L, result.StartedRuns);
        Assert.AreEqual(3L, result.CompletedRuns);
        Assert.AreEqual(3, rig.Saved.Count);
        Assert.AreEqual(3, rig.Saved.Select(item => item.RunId).Distinct().Count());
        Assert.IsTrue(rig.Saved.All(item => item.JobId == Job().JobId && item.Assessment.Verdict == verdict));
        Assert.AreEqual(rig.Saved.Last().RunId, result.LastRun!.RunId);
        Assert.AreEqual(InspectionRunState.Succeeded, result.LastRun.State);
        Assert.IsNull(result.CurrentRunId);
        Assert.IsNull(result.NextRunAtUtc);
        Assert.IsNotNull(result.CompletedAtUtc);
        Assert.AreEqual(InspectionExecutionMode.Manual, rig.Engine.GetStatus().Mode);
        Assert.AreEqual(InspectionAutoStopDisposition.TooLate, rig.Engine.StopAuto(auto.AutoId));
        Assert.AreEqual(InspectionAutoStopDisposition.NotFound, rig.Engine.StopAuto(Guid.NewGuid()));
        Assert.IsNull(rig.Engine.GetAuto(Guid.NewGuid()));
        Assert.AreEqual(InspectionRunState.Succeeded, (await rig.Engine.Start(Job()).Run!.Completion.WaitAsync(Limit)).State);
    }

    [TestMethod]
    public async Task Interval_StartsAfterPersistenceAndReservesManualAdmissionWhileWaiting()
    {
        await using var rig = new Rig();
        Gate firstStore = rig.Block(1, InspectionStage.Persist);
        Gate second = rig.Block(2, InspectionStage.Prepare);
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), Interval, maxRuns: 2));
        await firstStore.Entered.Task.WaitAsync(Limit);
        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(0, rig.Clock.ActiveTimerCount);
        Assert.AreEqual(1, rig.Prepared);
        Assert.AreEqual(0L, rig.Engine.GetAuto(auto.AutoId)!.CompletedRuns);
        firstStore.Release();
        await rig.Clock.NextTimerAsync().WaitAsync(Limit);
        InspectionEngineSnapshot waiting = rig.Engine.GetStatus();
        Assert.AreEqual(InspectionExecutionMode.Automatic, waiting.Mode);
        Assert.IsFalse(waiting.IsBusy);
        Assert.AreEqual(InspectionAutoState.Waiting, waiting.Auto!.State);
        Assert.AreEqual(rig.Clock.GetUtcNow() + Interval, waiting.Auto.NextRunAtUtc);
        InspectionStartResult rejected = rig.Engine.Start(Job());
        Assert.AreEqual(InspectionStartDisposition.Busy, rejected.Disposition);
        Assert.AreEqual(auto.AutoId, rejected.BusyAutoId);
        Assert.IsNull(rejected.BusyRunId);
        Assert.AreEqual(InspectionStartDisposition.Busy, rig.Engine.StartAuto(Job(), Interval).Disposition);
        rig.Clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, rig.Prepared);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await second.Entered.Task.WaitAsync(Limit);
        Assert.AreEqual(2, rig.Prepared);
        Assert.AreEqual(InspectionAutoState.Waiting, waiting.Auto.State, "Earlier snapshots remain unchanged.");
        second.Release();
        await auto.Completion.WaitAsync(Limit);
        Assert.AreEqual(0, rig.Clock.ActiveTimerCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentManualAndAutoStarts_AdmitExactlyOne(bool includeManual)
    {
        await using var rig = new Rig();
        Gate prepare = rig.Block(1, InspectionStage.Prepare);
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>[] callers = Enumerable.Range(0, 24).Select(index => Task.Run(async () =>
        {
            await signal.Task;
            return includeManual && index % 2 == 0
                ? rig.Engine.Start(Job()).Disposition == InspectionStartDisposition.Accepted
                : rig.Engine.StartAuto(Job(), Interval, maxRuns: 1).Disposition == InspectionStartDisposition.Accepted;
        })).ToArray();
        signal.SetResult();
        Assert.AreEqual(1, (await Task.WhenAll(callers).WaitAsync(Limit)).Count(value => value));
        await prepare.Entered.Task.WaitAsync(Limit);
        Assert.AreEqual(1, rig.Prepared);
        prepare.Release();
    }

    [DataTestMethod]
    [DataRow(InspectionStage.Inspect)]
    [DataRow(InspectionStage.Persist)]
    public async Task StopAuto_DrainsCurrentRunWithoutCancelingIt(InspectionStage stage)
    {
        await using var rig = new Rig();
        Gate work = rig.Block(1, stage);
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), Interval));
        await work.Entered.Task.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoStopDisposition.Accepted, rig.Engine.StopAuto(auto.AutoId));
        Assert.AreEqual(InspectionAutoStopDisposition.AlreadyRequested, rig.Engine.StopAuto(auto.AutoId));
        Assert.AreEqual(InspectionAutoState.Stopping, rig.Engine.GetAuto(auto.AutoId)!.State);
        Assert.IsFalse(work.Token.IsCancellationRequested);
        Assert.IsFalse(auto.Completion.IsCompleted);
        Assert.AreEqual(InspectionStartDisposition.Busy, rig.Engine.Start(Job()).Disposition);
        work.Release();
        var result = await auto.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoState.Stopped, result.State);
        Assert.AreEqual(InspectionAutoStopReason.Requested, result.StopReason);
        Assert.AreEqual(InspectionRunState.Succeeded, result.LastRun!.State);
        Assert.AreEqual(1, rig.Saved.Count);
        Assert.AreEqual(0, rig.Clock.ActiveTimerCount);
    }

    [TestMethod]
    public async Task StopWaitingAndStaleCommands_CannotAffectManualOrNewAutoRun()
    {
        await using var rig = new Rig();
        Gate manual = rig.Block(2, InspectionStage.Inspect);
        Gate nextAuto = rig.Block(3, InspectionStage.Inspect);
        InspectionAutoHandle first = Accepted(rig.Engine.StartAuto(Job(), Interval));
        ManualTimeProvider.ManualTimer oldTimer = await rig.Clock.NextTimerAsync().WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoStopDisposition.Accepted, rig.Engine.StopAuto(first.AutoId));
        await first.Completion.WaitAsync(Limit);
        Assert.IsTrue(oldTimer.Disposed);
        InspectionRunHandle manualRun = rig.Engine.Start(Job()).Run!;
        await manual.Entered.Task.WaitAsync(Limit);
        oldTimer.Fire();
        Assert.AreEqual(InspectionAutoStopDisposition.TooLate, rig.Engine.StopAuto(first.AutoId));
        Assert.IsFalse(manual.Token.IsCancellationRequested);
        manual.Release();
        await manualRun.Completion.WaitAsync(Limit);
        InspectionAutoHandle next = Accepted(rig.Engine.StartAuto(Job(), Interval, maxRuns: 1));
        await nextAuto.Entered.Task.WaitAsync(Limit);
        oldTimer.Fire();
        Assert.AreNotEqual(first.AutoId, next.AutoId);
        Assert.AreEqual(next.AutoId, rig.Engine.GetStatus().Auto!.AutoId);
        Assert.IsNotNull(rig.Engine.GetAuto(first.AutoId));
        Assert.IsFalse(nextAuto.Token.IsCancellationRequested);
        nextAuto.Release();
        await next.Completion.WaitAsync(Limit);
        Assert.IsNull(rig.Engine.GetAuto(first.AutoId));
        Assert.AreEqual(1L, (await first.Completion).CompletedRuns);
        Assert.AreEqual(3, rig.Prepared);
    }

    [TestMethod]
    public async Task StopAfterNextAdmission_KeepsThatRunAndPreventsAnother()
    {
        await using var rig = new Rig();
        Gate second = rig.Block(2, InspectionStage.Inspect);
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), Interval));
        await rig.Clock.NextTimerAsync().WaitAsync(Limit);
        rig.Clock.Advance(Interval);
        await second.Entered.Task.WaitAsync(Limit);
        rig.Engine.StopAuto(auto.AutoId);
        Assert.IsFalse(second.Token.IsCancellationRequested);
        second.Release();
        Assert.AreEqual(2L, (await auto.Completion.WaitAsync(Limit)).CompletedRuns);
        Assert.AreEqual(2, rig.Saved.Count);
    }

    [TestMethod]
    public async Task CancelCurrentRun_StopsAutoOnlyAfterActualCompletion()
    {
        await using var rig = new Rig();
        Gate inspect = rig.Block(1, InspectionStage.Inspect);
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), Interval));
        await inspect.Entered.Task.WaitAsync(Limit);
        Guid runId = rig.Engine.GetAuto(auto.AutoId)!.CurrentRunId!.Value;
        Assert.AreEqual(InspectionCancelDisposition.Accepted, rig.Engine.CancelRun(runId));
        Assert.IsTrue(inspect.Token.IsCancellationRequested);
        Assert.IsFalse(auto.Completion.IsCompleted);
        Assert.AreEqual(InspectionStartDisposition.Busy, rig.Engine.StartAuto(Job(), Interval).Disposition);
        inspect.Release();
        var result = await auto.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoState.Stopped, result.State);
        Assert.AreEqual(InspectionAutoStopReason.RunCanceled, result.StopReason);
        Assert.AreEqual(InspectionRunState.Canceled, result.LastRun!.State);
        Assert.AreEqual(0, rig.Saved.Count);
        Assert.AreEqual(1, rig.Prepared);
    }

    [TestMethod]
    public async Task LateCancelDuringPersistence_DoesNotStopAuto()
    {
        await using var rig = new Rig();
        Gate store = rig.Block(1, InspectionStage.Persist);
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), TimeSpan.Zero, maxRuns: 2));
        await store.Entered.Task.WaitAsync(Limit);
        Assert.AreEqual(InspectionCancelDisposition.TooLate, rig.Engine.CancelRun(rig.Engine.GetAuto(auto.AutoId)!.CurrentRunId!.Value));
        store.Release();
        Assert.AreEqual(InspectionAutoState.Completed, (await auto.Completion.WaitAsync(Limit)).State);
        Assert.AreEqual(2, rig.Saved.Count);
    }

    [TestMethod]
    public async Task EachRunGetsFreshTimeout_AndOldTimerCannotCancelNextRun()
    {
        await using var rig = new Rig();
        Gate first = rig.Block(1, InspectionStage.Inspect);
        Gate second = rig.Block(2, InspectionStage.Inspect);
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), TimeSpan.Zero, TimeSpan.FromSeconds(10)));
        ManualTimeProvider.ManualTimer oldTimer = await rig.Clock.NextTimerAsync().WaitAsync(Limit);
        await first.Entered.Task.WaitAsync(Limit);
        rig.Clock.Advance(TimeSpan.FromSeconds(9));
        first.Release();
        await rig.Clock.NextTimerAsync().WaitAsync(Limit);
        await second.Entered.Task.WaitAsync(Limit);
        oldTimer.Fire();
        rig.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.IsFalse(second.Token.IsCancellationRequested);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(second.Token.IsCancellationRequested);
        Assert.IsFalse(auto.Completion.IsCompleted);
        second.Release();
        var result = await auto.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoStopReason.RunTimedOut, result.StopReason);
        Assert.AreEqual(2L, result.CompletedRuns);
        Assert.AreEqual(1, rig.Saved.Count);
        Assert.AreEqual(0, rig.Clock.ActiveTimerCount);
    }

    [DataTestMethod]
    [DataRow(InspectionStage.Prepare)]
    [DataRow(InspectionStage.Acquire)]
    [DataRow(InspectionStage.Inspect)]
    [DataRow(InspectionStage.Persist)]
    public async Task ExecutionFailure_StopsAutoAndAllowsExplicitRestart(InspectionStage stage)
    {
        await using var rig = new Rig { FailureStage = stage };
        var result = await Accepted(rig.Engine.StartAuto(Job(), TimeSpan.Zero)).Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoState.Faulted, result.State);
        Assert.AreEqual(InspectionAutoStopReason.RunFaulted, result.StopReason);
        Assert.AreEqual(stage, result.LastRun!.Stage);
        Assert.AreEqual(1L, result.StartedRuns);
        Assert.AreEqual(0, rig.Saved.Count);
        Assert.AreEqual(InspectionEngineState.Ready, rig.Engine.GetStatus().State);
        rig.FailureStage = null;
        Assert.AreEqual(InspectionAutoState.Completed,
            (await Accepted(rig.Engine.StartAuto(Job(), TimeSpan.Zero, maxRuns: 1)).Completion.WaitAsync(Limit)).State);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TerminationFailure_StopsAutoAndPermanentlyRejectsAdmission(bool confirmed)
    {
        await using var rig = new Rig { FailureStage = InspectionStage.Inspect, TerminationConfirmed = confirmed };
        var result = await Accepted(rig.Engine.StartAuto(Job(), TimeSpan.Zero)).Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoState.Faulted, result.State);
        Assert.AreEqual(confirmed, result.LastRun!.TerminationConfirmed);
        Assert.AreEqual(InspectionEngineState.Faulted, rig.Engine.GetStatus().State);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, rig.Engine.StartAuto(Job(), Interval).Disposition);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, rig.Engine.Start(Job()).Disposition);
        Assert.AreEqual(1, rig.Prepared);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task InvalidOptions_DoNotReserveAdmission(int fixture)
    {
        await using var rig = new Rig();
        TimeSpan interval = fixture switch { 0 => TimeSpan.FromMilliseconds(-1), 1 => TimeSpan.FromMilliseconds(uint.MaxValue), _ => Interval };
        int? maxRuns = fixture switch { 2 => 0, 3 => -1, _ => null };
        TimeSpan? timeout = fixture switch { 4 => TimeSpan.FromMilliseconds(-2), 5 => TimeSpan.FromMilliseconds(uint.MaxValue), _ => null };
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => rig.Engine.StartAuto(Job(), interval, timeout, maxRuns));
        Assert.ThrowsException<ArgumentNullException>(() => rig.Engine.StartAuto(null!, Interval));
        Assert.AreEqual(0, rig.Prepared);
        Assert.AreEqual(InspectionAutoState.Completed,
            (await Accepted(rig.Engine.StartAuto(Job(), TimeSpan.Zero, maxRuns: 1)).Completion.WaitAsync(Limit)).State);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TimerCreationFailure_ReleasesReservation(bool firstRun)
    {
        await using var rig = new Rig();
        rig.Clock.ThrowOnCreate = true;
        if (firstRun)
        {
            Assert.ThrowsException<InvalidOperationException>(() => rig.Engine.StartAuto(Job(), Interval, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, rig.Prepared);
            Assert.IsNull(rig.Engine.GetStatus().Auto);
        }
        else
        {
            var result = await Accepted(rig.Engine.StartAuto(Job(), Interval)).Completion.WaitAsync(Limit);
            Assert.AreEqual(InspectionAutoState.Faulted, result.State);
            Assert.AreEqual(InspectionAutoStopReason.SchedulingFailed, result.StopReason);
            Assert.AreEqual(1L, result.CompletedRuns);
            Assert.AreEqual(InspectionRunState.Succeeded, result.LastRun!.State);
        }
        Assert.AreEqual(InspectionExecutionMode.Manual, rig.Engine.GetStatus().Mode);
        rig.Clock.ThrowOnCreate = false;
        await rig.Engine.Start(Job()).Run!.Completion.WaitAsync(Limit);
    }

    [TestMethod]
    public async Task NextRunSetupFailure_DoesNotLeaveAnActiveRun()
    {
        await using var rig = new Rig();
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), Interval, TimeSpan.FromMinutes(1)));
        await rig.Clock.NextTimerAsync().WaitAsync(Limit);
        await rig.Clock.NextTimerAsync().WaitAsync(Limit);
        rig.Clock.ThrowOnCreate = true;
        rig.Clock.Advance(Interval);
        var result = await auto.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoStopReason.SchedulingFailed, result.StopReason);
        Assert.AreEqual(1L, result.StartedRuns);
        Assert.AreEqual(1L, result.CompletedRuns);
        Assert.IsNull(result.CurrentRunId);
        Assert.IsFalse(rig.Engine.GetStatus().IsBusy);
        Assert.AreEqual(0, rig.Clock.ActiveTimerCount);
    }

    [DataTestMethod]
    [DataRow(InspectionStage.Inspect, InspectionRunState.Canceled)]
    [DataRow(InspectionStage.Persist, InspectionRunState.Succeeded)]
    public async Task DisposeDuringAuto_DrainsCurrentRunAndStopsScheduling(InspectionStage stage, InspectionRunState expected)
    {
        await using var rig = new Rig();
        Gate work = rig.Block(1, stage);
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), Interval));
        await work.Entered.Task.WaitAsync(Limit);
        Task disposal = rig.Engine.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        Assert.IsFalse(auto.Completion.IsCompleted);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, rig.Engine.StartAuto(Job(), Interval).Disposition);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, rig.Engine.Start(Job()).Disposition);
        work.Release();
        await disposal.WaitAsync(Limit);
        Assert.AreEqual(expected, (await auto.Completion).LastRun!.State);
        Assert.AreEqual(1, rig.Prepared);
        Assert.AreEqual(0, rig.Clock.ActiveTimerCount);
        Assert.AreEqual(InspectionEngineState.Disposed, rig.Engine.GetStatus().State);
        Assert.IsFalse(rig.DependencyDisposed);
    }

    [TestMethod]
    public async Task DisposeWhileWaiting_ClosesScheduleAndIgnoresLateTimer()
    {
        await using var rig = new Rig();
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), Interval));
        ManualTimeProvider.ManualTimer timer = await rig.Clock.NextTimerAsync().WaitAsync(Limit);
        await rig.Engine.DisposeAsync().AsTask().WaitAsync(Limit);
        timer.Fire();
        var result = await auto.Completion;
        Assert.AreEqual(InspectionAutoStopReason.Shutdown, result.StopReason);
        Assert.AreEqual(1L, result.CompletedRuns);
        Assert.AreEqual(1, rig.Prepared);
        Assert.IsTrue(timer.Disposed);
        Assert.AreEqual(InspectionEngineState.Disposed, rig.Engine.GetStatus().State);
    }

    [TestMethod]
    public async Task StopAuto_DoesNotHideCurrentStorageFailure()
    {
        await using var rig = new Rig { FailureStage = InspectionStage.Persist };
        Gate store = rig.Block(1, InspectionStage.Persist);
        InspectionAutoHandle auto = Accepted(rig.Engine.StartAuto(Job(), TimeSpan.Zero));
        await store.Entered.Task.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoStopDisposition.Accepted, rig.Engine.StopAuto(auto.AutoId));
        store.Release();
        InspectionAutoSnapshot result = await auto.Completion.WaitAsync(Limit);
        Assert.AreEqual(InspectionAutoState.Faulted, result.State);
        Assert.AreEqual(InspectionAutoStopReason.RunFaulted, result.StopReason);
        Assert.AreEqual(InspectionRunState.Faulted, result.LastRun!.State);
        Assert.IsNotNull(result.LastRun.ComputedResult);
        Assert.AreEqual(1L, result.StartedRuns);
        Assert.AreEqual(0, rig.Saved.Count);
        Assert.AreEqual(InspectionEngineState.Ready, rig.Engine.GetStatus().State);
    }

    private static InspectionAutoHandle Accepted(InspectionAutoStartResult result)
    {
        Assert.AreEqual(InspectionStartDisposition.Accepted, result.Disposition);
        Assert.IsNotNull(result.Auto);
        Assert.IsNull(result.BusyAutoId);
        Assert.IsNull(result.BusyRunId);
        return result.Auto;
    }

    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        internal async Task WaitAsync(CancellationToken token) { Token = token; Entered.TrySetResult(); await _release.Task; }
        internal void Release() => _release.TrySetResult();
    }

    private sealed class Rig : IDevice, IInspector, IResultStore, IAsyncDisposable, IDisposable
    {
        private readonly Dictionary<(int Run, InspectionStage Stage), Gate> _gates = [];
        private int _prepared;
        internal int Prepared => Volatile.Read(ref _prepared);
        internal int Defects { get; init; }
        internal InspectionStage? FailureStage { get; set; }
        internal bool? TerminationConfirmed { get; init; }
        internal bool DependencyDisposed { get; private set; }
        internal ManualTimeProvider Clock { get; } = new();
        internal InspectionEngine Engine { get; }
        internal ConcurrentQueue<InspectionResult> Saved { get; } = new();
        internal Rig() { Engine = new(new InspectionRunner(this, this, this, Clock), Clock); }
        internal Gate Block(int run, InspectionStage stage) { var gate = new Gate(); _gates.Add((run, stage), gate); return gate; }
        private async Task Visit(InspectionStage stage, CancellationToken token)
        {
            if (_gates.TryGetValue((Prepared, stage), out Gate? gate)) { await gate.WaitAsync(token); }
            if (FailureStage == stage)
            {
                var error = new IOException("Simulated stage failure.");
                if (TerminationConfirmed is { } confirmed) { throw new InspectionTerminationException(confirmed, error); }
                throw error;
            }
        }
        public async Task PrepareAsync(InspectionJob job, CancellationToken token) { Interlocked.Increment(ref _prepared); await Visit(InspectionStage.Prepare, token); }
        public async Task<double[]> AcquireAsync(InspectionJob job, CancellationToken token) { await Visit(InspectionStage.Acquire, token); return job.Samples.ToArray(); }
        public async Task<InspectionAssessment> InspectAsync(InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken token)
        { await Visit(InspectionStage.Inspect, token); return new(samples.Length, Defects); }
        public async Task SaveAsync(InspectionResult result, CancellationToken token)
        { Assert.IsFalse(token.CanBeCanceled); await Visit(InspectionStage.Persist, token); Saved.Enqueue(result); }
        public void Dispose() => DependencyDisposed = true;
        public async ValueTask DisposeAsync()
        {
            foreach (Gate gate in _gates.Values) { gate.Release(); }
            await Engine.DisposeAsync();
        }
    }
}
