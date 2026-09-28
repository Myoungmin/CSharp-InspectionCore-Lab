using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Inspection.Core;
using Inspection.CppCli;
using Inspection.Infrastructure;
using Inspection.Interop;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("CppCli")]
public sealed class CliInspectorTests
{
    private static InspectionJob Job() => new("cli-job", [10, 200], 0, 100);

    [TestMethod]
    public void ActualAssembly_IsX64MixedModeAndMatchesNativeAbi()
    {
        using var stream = File.OpenRead(typeof(CliInspector).Assembly.Location);
        using var reader = new PEReader(stream);
        Assert.AreEqual(Machine.Amd64, reader.PEHeaders.CoffHeader.Machine);
        Assert.IsNotNull(reader.PEHeaders.CorHeader);
        Assert.IsFalse(reader.PEHeaders.CorHeader.Flags.HasFlag(CorFlags.ILOnly));
        Assert.AreEqual(2u, new CliNativeApi().AbiVersion());
        Assert.AreEqual(16u, new CliNativeApi().ResultSize());
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task CliNativeAndManaged_AgreeOnSameSamples(int fixture)
    {
        double[][] fixtures = [[10, 20, 30, 40], [10, 20, 30, 140], [0, 100], [-10, -1, 101], [1, 1, 200]];
        double[] samples = fixtures[fixture], original = (double[])samples.Clone();
        await using var cli = new CliInspector();
        await using var native = new NativeInspector();
        InspectionAssessment managed = await new RangeInspector().InspectAsync(Job(), samples, CancellationToken.None);
        Assert.AreEqual(managed, await cli.InspectAsync(Job(), samples, CancellationToken.None));
        Assert.AreEqual(managed, await native.InspectAsync(Job(), samples, CancellationToken.None));
        CollectionAssert.AreEqual(original, samples);
    }

    [TestMethod]
    public async Task MemorySlice_UsesOffsetAndElementCount()
    {
        await using var cli = new CliInspector();
        double[] samples = [-999, 0, 100, 999];
        Assert.AreEqual(new InspectionAssessment(2, 0), await cli.InspectAsync(Job(), samples.AsMemory(1, 2), CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task InvalidAcquiredSamples_ReturnSameStatusAsPInvoke(int fixture)
    {
        double[][] samples = [[], [double.NaN], [double.PositiveInfinity], [double.NegativeInfinity]];
        await using var cli = new CliInspector();
        await using var native = new NativeInspector();
        var actual = await Assert.ThrowsExceptionAsync<NativeInspectionException>(() => cli.InspectAsync(Job(), samples[fixture], CancellationToken.None));
        var expected = await Assert.ThrowsExceptionAsync<NativeInspectionException>(() => native.InspectAsync(Job(), samples[fixture], CancellationToken.None));
        Assert.AreEqual(NativeStatus.InvalidArgument, actual.Status);
        Assert.AreEqual(expected.Status, actual.Status);
        Assert.AreEqual(expected.Operation, actual.Operation);
    }

    [TestMethod]
    public void CreateFailure_DoesNotLeakHandle()
    {
        int before = NativeMethods.LiveHandles();
        var error = Assert.ThrowsException<NativeInspectionException>(() => new CliInspector(NativeFaultMode.ThrowOnCreate));
        Assert.AreEqual(NativeStatus.InternalError, error.Status);
        Assert.AreEqual("Create", error.Operation);
        Assert.AreEqual(before, NativeMethods.LiveHandles());
    }

    [TestMethod]
    public async Task InspectFailure_FaultsRunnerAndPreventsStorage()
    {
        var store = new Store();
        await using var cli = new CliInspector(NativeFaultMode.ThrowOnInspect);
        var error = await Assert.ThrowsExceptionAsync<InspectionRunException>(() => new InspectionRunner(new SimulatedDevice(), cli, store).RunAsync(Job()));
        Assert.AreEqual(InspectionStage.Inspect, error.Stage);
        Assert.AreEqual(NativeStatus.InternalError, ((NativeInspectionException)error.InnerException!).Status);
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public async Task AlreadyCanceled_DoesNotEnterThrowingInspector()
    {
        await using var cli = new CliInspector(NativeFaultMode.ThrowOnInspect);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => cli.InspectAsync(Job(), new double[] { 1 }, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
    }

    [TestMethod]
    public void DisposeTwice_UsesCliReleaseOnceAndRejectsReuse()
    {
        int before = NativeMethods.LiveHandles();
        ulong destroyed = NativeMethods.DestroyedHandles();
        var cli = new CliInspector();
        cli.Dispose();
        cli.Dispose();
        Assert.AreEqual(before, NativeMethods.LiveHandles());
        Assert.AreEqual(destroyed + 1, NativeMethods.DestroyedHandles());
        Assert.ThrowsException<ObjectDisposedException>(() => cli.InspectAsync(Job(), new double[] { 1 }, CancellationToken.None));
    }

    [TestMethod]
    public void SafeHandleFinalization_CallsCliReleaseForAbandonedAdapter()
    {
        int before = NativeMethods.LiveHandles();
        ulong destroyed = NativeMethods.DestroyedHandles();
        WeakReference reference = Abandon();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsFalse(reference.IsAlive);
        Assert.AreEqual(before, NativeMethods.LiveHandles());
        Assert.AreEqual(destroyed + 1, NativeMethods.DestroyedHandles());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon() => new(new CliInspector());

    private sealed class Store : IResultStore
    {
        internal int Count;
        public Task SaveAsync(InspectionResult result, CancellationToken token) { Count++; return Task.CompletedTask; }
    }
}
