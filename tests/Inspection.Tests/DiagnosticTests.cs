using System.Collections.Concurrent;
using Inspection.Core;

namespace Inspection.Tests;

[TestClass]
public sealed class DiagnosticTests
{
    private static InspectionJob Job() => new("diagnostic-job", [1], 0, 10);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ThrowingDiagnostics_PreservesOutcomeAndStageOrder(bool storeFails)
    {
        var ports = new Ports { StoreFails = storeFails };
        var sink = new Sink { Throw = true };
        await using var engine = new InspectionEngine(new InspectionRunner(ports, ports, ports), diagnostics: sink);
        InspectionRunSnapshot outcome = await engine.Start(Job()).Run!.Completion.WaitAsync(Limit);
        Assert.AreEqual(storeFails ? InspectionRunState.Faulted : InspectionRunState.Succeeded, outcome.State);
        Assert.AreEqual(1, ports.Saves);
        Assert.IsNotNull(outcome.ComputedResult);
        CollectionAssert.AreEqual(new[] { "RunStarted", "StageEntered", "StageEntered", "StageEntered", "StageEntered", "RunCompleted" },
            sink.Entries.Select(e => e.EventName).ToArray());
        CollectionAssert.AreEqual(Enum.GetValues<InspectionStage>(), sink.Entries.Where(e => e.EventName == "StageEntered").Select(e => e.Stage!.Value).ToArray());
        Assert.AreSame(outcome, sink.Entries.Last().Outcome);
        Assert.IsTrue(sink.Entries.All(e => e.AutoId is null));
        if (storeFails) { Assert.AreEqual("Store fixture failure.", outcome.Error!.InnerException!.Message); }
    }

    [TestMethod]
    public async Task FinalDiagnostic_IsOutsideGateAndFinishesBeforeCompletionAndDispose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var sink = new Sink
        {
            OnRecord = entry => { if (entry.EventName == "RunCompleted") { entered.SetResult(); if (!release.Wait(Limit)) { throw new TimeoutException(); } } }
        };
        var ports = new Ports();
        var engine = new InspectionEngine(new InspectionRunner(ports, ports, ports), diagnostics: sink);
        InspectionRunHandle run = engine.Start(Job()).Run!;
        try
        {
            await entered.Task.WaitAsync(Limit);
            InspectionEngineSnapshot status = await Task.Run(engine.GetStatus).WaitAsync(Limit);
            Assert.IsTrue(status.IsBusy);
            Assert.IsNull(status.Run!.CompletedAtUtc);
            Assert.AreEqual(InspectionStartDisposition.Busy, engine.Start(Job()).Disposition);
            Task disposal = engine.DisposeAsync().AsTask();
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(run.Completion.IsCompleted);
            release.Set();
            Assert.AreEqual(InspectionRunState.Succeeded, (await run.Completion.WaitAsync(Limit)).State);
            await disposal.WaitAsync(Limit);
        }
        finally { release.Set(); await engine.DisposeAsync(); }
    }

    [TestMethod]
    public async Task AutoDiagnostics_ContainsEveryDistinctCompletedRun()
    {
        var sink = new Sink();
        var ports = new Ports();
        await using var engine = new InspectionEngine(new InspectionRunner(ports, ports, ports), diagnostics: sink);
        InspectionAutoSnapshot result = await engine.StartAuto(Job(), TimeSpan.Zero, maxRuns: 3).Auto!.Completion.WaitAsync(Limit);
        var completions = sink.Entries.Where(e => e.EventName == "RunCompleted").ToArray();
        Assert.AreEqual(3, completions.Length);
        Assert.AreEqual(3, completions.Select(e => e.RunId).Distinct().Count());
        Assert.IsTrue(completions.All(e => e.Outcome!.State == InspectionRunState.Succeeded));
        Assert.IsTrue(sink.Entries.All(e => e.AutoId == result.AutoId));
        Assert.AreEqual(result.LastRun!.RunId, completions[^1].RunId);
    }

    private sealed class Sink : IInspectionDiagnostics
    {
        internal ConcurrentQueue<InspectionDiagnostic> Entries { get; } = new();
        internal bool Throw { get; init; }
        internal Action<InspectionDiagnostic>? OnRecord { get; init; }
        public void Record(InspectionDiagnostic entry)
        {
            Entries.Enqueue(entry);
            OnRecord?.Invoke(entry);
            if (Throw) { throw new IOException("Log fixture failure."); }
        }
    }

    private sealed class Ports : IDevice, IInspector, IResultStore
    {
        internal bool StoreFails { get; init; }
        internal int Saves { get; private set; }
        public Task PrepareAsync(InspectionJob job, CancellationToken token) => Task.CompletedTask;
        public Task<double[]> AcquireAsync(InspectionJob job, CancellationToken token) => Task.FromResult(new[] { 1.0 });
        public Task<InspectionAssessment> InspectAsync(InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken token) => Task.FromResult(new InspectionAssessment(1, 0));
        public Task SaveAsync(InspectionResult result, CancellationToken token)
        {
            Saves++;
            return StoreFails ? Task.FromException(new IOException("Store fixture failure.")) : Task.CompletedTask;
        }
    }
}
