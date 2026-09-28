using NativeInspector = Inspection.CppCli.CliInspector;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Inspection.Core;
using Inspection.Infrastructure;
using Inspection.Interop;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("CppCli")]
public sealed class CliLifetimeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static InspectionJob Job() => new("lifetime-job", [10, 200], 0, 100);

    [TestMethod]
    public async Task Progress_IsOrderedRootedAndIsolatedBetweenRuns()
    {
        var reports = new List<NativeInspectionProgress>();
        await using var native = new NativeInspector(progress: report =>
        {
            if (report.CompletedSamples == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            reports.Add(report);
        });
        var first = await native.InspectAsync(Job(), new double[] { 10, 200 }, CancellationToken.None).WaitAsync(Limit);
        Assert.AreEqual(1, first.DefectCount);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, reports.Select(item => item.CompletedSamples).ToArray());
        Assert.IsTrue(reports.All(item => item.TotalSamples == 2));
        reports.Clear();
        var second = await native.InspectAsync(Job(), new double[] { 10 }, CancellationToken.None).WaitAsync(Limit);
        Assert.AreEqual(0, second.DefectCount);
        CollectionAssert.AreEqual(new[] { 0, 1 }, reports.Select(item => item.CompletedSamples).ToArray());
        Assert.IsTrue(reports.All(item => item.TotalSamples == 1));
    }

    [TestMethod]
    public async Task Cancel_WaitsForCallbackAndSuppressesLateNativeNotification()
    {
        var gate = new CallbackGate();
        using var cancellation = new CancellationTokenSource();
        await using var native = new NativeInspector(progress: gate.Report);
        Task<InspectionAssessment> work = native.InspectAsync(Job(), new double[] { 10, 200 }, cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            cancellation.Cancel();
            Assert.IsFalse(work.IsCompleted);
            Assert.ThrowsException<InvalidOperationException>(() => native.InspectAsync(Job(), new double[] { 10 }, CancellationToken.None));
        }
        finally { gate.Release(); }
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => work.WaitAsync(Limit));
        Assert.AreEqual(1, gate.Reports);
        Assert.AreEqual(2ul, native.ProgressCallbackCount, "Native emits a final callback after stop; the adapter suppresses delivery.");
        Assert.AreEqual(0, (await native.InspectAsync(Job(), new double[] { 10 }, CancellationToken.None).WaitAsync(Limit)).DefectCount);
    }

    [TestMethod]
    public async Task DisposeDuringCallback_WaitsAndDestroysExactlyOnce()
    {
        var gate = new CallbackGate();
        int live = NativeMethods.LiveHandles();
        ulong destroyed = NativeMethods.DestroyedHandles();
        var native = new NativeInspector(progress: gate.Report);
        Task<InspectionAssessment> work = native.InspectAsync(Job(), new double[] { 10 }, CancellationToken.None);
        Task? disposal = null;
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            disposal = native.DisposeAsync().AsTask();
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(work.IsCompleted);
            Assert.AreEqual(live + 1, NativeMethods.LiveHandles());
            Assert.AreEqual(destroyed, NativeMethods.DestroyedHandles());
            Assert.ThrowsException<ObjectDisposedException>(() => native.InspectAsync(Job(), new double[] { 10 }, CancellationToken.None));
        }
        finally { gate.Release(); await native.DisposeAsync().AsTask().WaitAsync(Limit); }
        await disposal!.WaitAsync(Limit);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => work);
        native.Dispose();
        Assert.AreEqual(live, NativeMethods.LiveHandles());
        Assert.AreEqual(destroyed + 1, NativeMethods.DestroyedHandles());
    }

    [DataTestMethod]
    [DataRow(0, InspectionRunState.Canceled, InspectionStopReason.UserCancellation)]
    [DataRow(1, InspectionRunState.TimedOut, InspectionStopReason.Timeout)]
    [DataRow(2, InspectionRunState.Canceled, InspectionStopReason.Shutdown)]
    public async Task EngineStop_DrainsNativeCallbackBeforeCompletion(int cause, InspectionRunState state, InspectionStopReason reason)
    {
        var gate = new CallbackGate();
        var clock = new TriggeredTimeProvider();
        var store = new RecordingStore();
        await using var native = new NativeInspector(progress: gate.Report);
        await using var engine = new InspectionEngine(new InspectionRunner(new SimulatedDevice(), native, store), clock);
        InspectionRunHandle run = engine.Start(Job(), TimeSpan.FromHours(1)).Run!;
        Task? disposal = null;
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            if (cause == 0) { Assert.AreEqual(InspectionCancelDisposition.Accepted, engine.CancelRun(run.RunId)); }
            else if (cause == 1) { clock.Fire(); }
            else { disposal = engine.DisposeAsync().AsTask(); }
            Assert.AreEqual(InspectionRunState.CancelRequested, engine.GetRun(run.RunId)!.State);
            Assert.IsTrue(engine.GetStatus().IsBusy);
            Assert.IsFalse(run.Completion.IsCompleted);
            Assert.AreNotEqual(InspectionStartDisposition.Accepted, engine.Start(Job()).Disposition);
            Assert.IsFalse(store.Called);
        }
        finally { gate.Release(); }
        InspectionRunSnapshot completed = await run.Completion.WaitAsync(Limit);
        Assert.AreEqual(state, completed.State);
        Assert.AreEqual(reason, completed.StopReason);
        Assert.IsFalse(store.Called);
        if (disposal is not null) { await disposal.WaitAsync(Limit); }
        else { Assert.AreEqual(InspectionRunState.Succeeded, (await engine.Start(Job()).Run!.Completion.WaitAsync(Limit)).State); }
    }

    [TestMethod]
    public async Task StopFailure_DrainsWorkThenFaultsEngineAndBlocksAdmission()
    {
        var gate = new CallbackGate();
        var store = new RecordingStore();
        int live = NativeMethods.LiveHandles();
        var native = new NativeInspector(NativeFaultMode.ThrowOnStop, gate.Report);
        await using var engine = new InspectionEngine(new InspectionRunner(new SimulatedDevice(), native, store));
        InspectionRunHandle run = engine.Start(Job()).Run!;
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            engine.CancelRun(run.RunId);
            // Adapter disposal delivers stop synchronously even if Engine's CancelAsync callback is still queued.
            Task disposal = native.DisposeAsync().AsTask();
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(run.Completion.IsCompleted);
            Assert.AreEqual(live + 1, NativeMethods.LiveHandles());
        }
        finally { gate.Release(); }
        InspectionRunSnapshot completed = await run.Completion.WaitAsync(Limit);
        var error = (InspectionTerminationException)((InspectionRunException)completed.Error!).InnerException!;
        Assert.IsTrue(error.TerminationConfirmed);
        Assert.AreEqual(true, completed.TerminationConfirmed);
        Assert.AreEqual("RequestStop", ((NativeInspectionException)error.InnerException!).Operation);
        Assert.AreEqual(InspectionRunState.Faulted, completed.State);
        Assert.AreEqual(InspectionStopReason.UserCancellation, completed.StopReason);
        Assert.AreEqual(InspectionEngineState.Faulted, engine.GetStatus().State);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, engine.Start(Job()).Disposition);
        Assert.IsFalse(store.Called);
        await native.DisposeAsync().AsTask().WaitAsync(Limit);
        await engine.DisposeAsync();
        Assert.AreEqual(InspectionEngineState.Faulted, engine.GetStatus().State);
        Assert.AreEqual(live, NativeMethods.LiveHandles());
    }

    [TestMethod]
    public async Task WaitFailure_QuarantinesHandleAndContextEvenAfterDisposeAndGc()
    {
        int live = NativeMethods.LiveHandles();
        ulong destroyed = NativeMethods.DestroyedHandles();
        var store = new RecordingStore();
        var native = new NativeInspector(NativeFaultMode.ThrowOnWait);
        await using var engine = new InspectionEngine(new InspectionRunner(new SimulatedDevice(), native, store));
        var completed = await engine.Start(Job()).Run!.Completion.WaitAsync(Limit);
        var error = (InspectionTerminationException)((InspectionRunException)completed.Error!).InnerException!;
        Assert.IsFalse(error.TerminationConfirmed);
        Assert.AreEqual(false, completed.TerminationConfirmed);
        Assert.AreEqual("Wait", ((NativeInspectionException)error.InnerException!).Operation);
        Assert.AreEqual(InspectionRunState.Faulted, completed.State);
        Assert.AreEqual(InspectionEngineState.Faulted, engine.GetStatus().State);
        Assert.AreEqual(InspectionStartDisposition.Unavailable, engine.Start(Job()).Disposition);
        Assert.ThrowsException<InvalidOperationException>(() => native.InspectAsync(Job(), new double[] { 10 }, CancellationToken.None));
        await native.DisposeAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.AreEqual(live + 1, NativeMethods.LiveHandles(), "One quarantined handle deliberately survives until this test process exits.");
        Assert.AreEqual(destroyed, NativeMethods.DestroyedHandles());
        Assert.IsFalse(store.Called);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallbackFailure_IsContainedAndJoinedBeforeReturning(bool reentrantDispose)
    {
        NativeInspector? native = null;
        native = new NativeInspector(progress: _ =>
        {
            if (reentrantDispose) { native!.Dispose(); }
            else { throw new ApplicationException("Observer failed."); }
        });
        await using (native)
        {
            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => native.InspectAsync(Job(), new double[] { 10 }, CancellationToken.None).WaitAsync(Limit));
            Assert.AreEqual("Native progress observer failed.", error.Message);
            if (reentrantDispose) { StringAssert.Contains(error.InnerException!.Message, "Dispose cannot run"); }
            else { Assert.IsInstanceOfType<ApplicationException>(error.InnerException); }
        }
    }

    [TestMethod]
    public async Task Abi_StartCopiesInputAndRejectsReuseOrDestroyUntilWait()
    {
        Assert.AreEqual(NativeStatus.Ok, new Inspection.CppCli.CliNativeApi().Create(NativeFaultMode.None, out NativeInspectorHandle handle));
        using (handle)
        {
            var gate = new CallbackGate();
            GCHandle context = GCHandle.Alloc(gate);
            double[] samples = [10, 200];
            bool started = false;
            try
            {
                Assert.AreEqual(NativeStatus.Ok, StartRaw(handle, samples, GCHandle.ToIntPtr(context)));
                started = true;
                await gate.Entered.Task.WaitAsync(Limit);
                samples[0] = double.NaN;
                samples[1] = 10;
                Assert.AreEqual(NativeStatus.Busy, StartRaw(handle, new double[] { 10 }, 0));
                Assert.AreEqual(NativeStatus.Busy, NativeMethods.Destroy(handle.DangerousGetHandle()));
            }
            finally
            {
                gate.Release();
                if (started)
                {
                    var outcome = await Task.Run(() => WaitRaw(handle)).WaitAsync(Limit);
                    Assert.AreEqual(NativeStatus.Ok, outcome.Status);
                    Assert.AreEqual(NativeStatus.Ok, outcome.Operation);
                    Assert.AreEqual(2, outcome.Result.SampleCount);
                    Assert.AreEqual(1, outcome.Result.DefectCount);
                }
                context.Free();
            }
        }
    }

    private static unsafe NativeStatus StartRaw(NativeInspectorHandle handle, double[] samples, nint context)
    {
        nint callback = context == 0 ? 0 : (nint)(delegate* unmanaged[Cdecl]<nint, int, int, void>)&RawProgress;
        return new Inspection.CppCli.CliNativeApi().Start(handle, samples, 0, 100, callback, context);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RawProgress(nint context, int completed, int total)
        => ((CallbackGate)GCHandle.FromIntPtr(context).Target!).Report(new(completed, total));

    private static (NativeStatus Status, NativeStatus Operation, NativeInspectionResult Result) WaitRaw(NativeInspectorHandle handle)
    {
        NativeStatus status = new Inspection.CppCli.CliNativeApi().Wait(handle, out NativeStatus operation, out NativeInspectionResult result);
        return (status, operation, result);
    }

    private sealed class CallbackGate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reports;
        internal void Report(NativeInspectionProgress progress)
        {
            Interlocked.Increment(ref Reports);
            Entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
        internal void Release() => _release.TrySetResult();
    }

    private sealed class RecordingStore : IResultStore
    {
        internal bool Called;
        public Task SaveAsync(InspectionResult result, CancellationToken cancellationToken) { Called = true; return Task.CompletedTask; }
    }

    private sealed class TriggeredTimeProvider : TimeProvider
    {
        private Action? _fire;
        internal void Fire() => _fire!();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _fire = () => callback(state);
            return new Timer();
        }
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
