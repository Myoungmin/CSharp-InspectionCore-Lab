using NativeInspector = Inspection.CppCli.CliInspector;
using Inspection.Core;
using Inspection.Infrastructure;
using Inspection.Interop;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("CppCli")]
public sealed class CliAutoTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutoStopAndCancel_DrainRealNativeCallbackBeforeReleasingReservation(bool cancelCurrent)
    {
        TimeSpan limit = TimeSpan.FromSeconds(5);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingStore();
        int live = NativeMethods.LiveHandles();
        var native = new NativeInspector(progress: _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        await using (native)
        {
            await using var engine = new InspectionEngine(new InspectionRunner(new SimulatedDevice(), native, store));
            var job = new InspectionJob("native-auto", [10, 200], 0, 100);
            InspectionAutoHandle auto = engine.StartAuto(job, TimeSpan.Zero).Auto!;
            try
            {
                await entered.Task.WaitAsync(limit);
                if (cancelCurrent) { engine.CancelRun(engine.GetAuto(auto.AutoId)!.CurrentRunId!.Value); }
                else { engine.StopAuto(auto.AutoId); }
                Assert.IsFalse(auto.Completion.IsCompleted);
                Assert.AreEqual(InspectionStartDisposition.Busy, engine.Start(job).Disposition);
                Assert.AreEqual(live + 1, NativeMethods.LiveHandles());
            }
            finally { release.TrySetResult(); }
            InspectionAutoSnapshot result = await auto.Completion.WaitAsync(limit);
            Assert.AreEqual(1L, result.StartedRuns);
            Assert.AreEqual(1L, result.CompletedRuns);
            Assert.AreEqual(true, result.LastRun!.TerminationConfirmed);
            Assert.AreEqual(cancelCurrent ? InspectionAutoStopReason.RunCanceled : InspectionAutoStopReason.Requested, result.StopReason);
            Assert.AreEqual(cancelCurrent ? InspectionRunState.Canceled : InspectionRunState.Succeeded, result.LastRun.State);
            Assert.AreEqual(cancelCurrent ? 0 : 1, store.Count);
            if (!cancelCurrent) { Assert.AreEqual(InspectionVerdict.Fail, result.LastRun.ComputedResult!.Assessment.Verdict); }
        }
        Assert.AreEqual(live, NativeMethods.LiveHandles());
    }

    private sealed class RecordingStore : IResultStore
    {
        internal int Count;
        public Task SaveAsync(InspectionResult result, CancellationToken cancellationToken) { Count++; return Task.CompletedTask; }
    }
}
