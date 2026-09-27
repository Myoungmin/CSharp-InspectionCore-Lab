using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Inspection.Core;
using Inspection.Infrastructure;
using Inspection.Interop;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("Native")]
public sealed class NativeInspectorTests
{
    private static InspectionJob Job() => new("native-job", [10, 20], 0, 100);

    [TestMethod]
    public void ActualDll_IsX64AndMatchesManagedAbiLayout()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "NativeInspection.dll"));
        using var reader = new PEReader(stream);
        Assert.AreEqual(Machine.Amd64, reader.PEHeaders.CoffHeader.Machine);
        Assert.AreEqual(1u, NativeMethods.AbiVersion());
        Assert.AreEqual(16u, NativeMethods.ResultSize());
        Assert.AreEqual(16, Marshal.SizeOf<NativeInspectionResult>());
        Assert.AreEqual((nint)0, Marshal.OffsetOf<NativeInspectionResult>(nameof(NativeInspectionResult.SampleCount)));
        Assert.AreEqual((nint)4, Marshal.OffsetOf<NativeInspectionResult>(nameof(NativeInspectionResult.DefectCount)));
        Assert.AreEqual((nint)8, Marshal.OffsetOf<NativeInspectionResult>(nameof(NativeInspectionResult.Score)));
    }

    [DataTestMethod]
    [DataRow(0, 0, 100.0)]
    [DataRow(1, 1, 75.0)]
    [DataRow(2, 0, 100.0)]
    [DataRow(3, 3, 0.0)]
    [DataRow(4, 1, 66.67)]
    public async Task NativeAndManagedInspectors_AgreeOnSameContract(int fixture, int defects, double score)
    {
        double[][] fixtures = [[10, 20, 30, 40], [10, 20, 30, 140], [0, 100], [-10, -1, 101], [1, 1, 200]];
        double[] samples = fixtures[fixture];
        double[] original = (double[])samples.Clone();
        var job = new InspectionJob("comparison", samples, 0, 100);
        using var native = new NativeInspector();

        InspectionAssessment expected = await new RangeInspector().InspectAsync(job, samples, CancellationToken.None);
        InspectionAssessment actual = await native.InspectAsync(job, samples, CancellationToken.None);

        Assert.AreEqual(expected, actual);
        Assert.AreEqual(defects, actual.DefectCount);
        Assert.AreEqual(score, actual.Score);
        CollectionAssert.AreEqual(original, samples, "The native input buffer is borrowed read-only.");
    }

    [TestMethod]
    public async Task MemorySlice_UsesOffsetAndElementCount()
    {
        double[] backing = [-999, 0, 100, 999];
        using var native = new NativeInspector();
        InspectionAssessment result = await native.InspectAsync(Job(), backing.AsMemory(1, 2), CancellationToken.None);
        Assert.AreEqual(2, result.SampleCount);
        Assert.AreEqual(0, result.DefectCount);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task InvalidAcquiredSamples_MapNativeErrorCode(int fixture)
    {
        double[][] fixtures = [[], [double.NaN], [double.PositiveInfinity], [double.NegativeInfinity]];
        using var native = new NativeInspector();
        var error = await Assert.ThrowsExceptionAsync<NativeInspectionException>(
            () => native.InspectAsync(Job(), fixtures[fixture], CancellationToken.None));
        Assert.AreEqual(NativeStatus.InvalidArgument, error.Status);
        Assert.AreEqual("Inspect", error.Operation);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public unsafe void Abi_RejectsNonPositiveLengthsAndClearsResult(int count)
    {
        using NativeInspectorHandle handle = CreateHandle();
        double sample = 10;
        NativeInspectionResult result = new() { SampleCount = 99, DefectCount = 99, Score = 99 };
        Assert.AreEqual(NativeStatus.InvalidArgument, NativeMethods.Run(handle, &sample, count, 0, 100, &result));
        Assert.AreEqual(0, result.SampleCount);
        Assert.AreEqual(0, result.DefectCount);
        Assert.AreEqual(0.0, result.Score);
    }

    [TestMethod]
    public unsafe void Abi_RejectsNullPointersAndInvalidBounds()
    {
        using NativeInspectorHandle handle = CreateHandle();
        using var emptyHandle = new NativeInspectorHandle();
        double sample = 10;
        NativeInspectionResult result;
        Assert.AreEqual(NativeStatus.InvalidArgument, NativeMethods.Run(handle, null, 1, 0, 100, &result));
        Assert.AreEqual(NativeStatus.InvalidArgument, NativeMethods.Run(handle, &sample, 1, 0, 100, null));
        Assert.AreEqual(NativeStatus.InvalidArgument, NativeMethods.Run(emptyHandle, &sample, 1, 0, 100, &result));
        Assert.AreEqual(NativeStatus.InvalidArgument, NativeMethods.Run(handle, &sample, 1, 100, 0, &result));
        Assert.AreEqual(NativeStatus.InvalidArgument, NativeMethods.Run(handle, &sample, 1, double.NaN, 100, &result));
        Assert.AreEqual(NativeStatus.InvalidArgument, NativeMethods.Run(handle, &sample, 1, 0, double.PositiveInfinity, &result));
    }

    [TestMethod]
    public unsafe void Abi_ReadsOnlyRequestedPrefixAndReturnsNativeScore()
    {
        using NativeInspectorHandle handle = CreateHandle();
        double[] samples = [0, 100, 150, double.NaN];
        NativeInspectionResult result;
        fixed (double* pointer = samples)
        {
            Assert.AreEqual(NativeStatus.Ok, NativeMethods.Run(handle, pointer, 3, 0, 100, &result));
        }
        Assert.AreEqual(3, result.SampleCount);
        Assert.AreEqual(1, result.DefectCount);
        Assert.AreEqual(200.0 / 3, result.Score, 1e-12);
    }

    [TestMethod]
    public void CppExceptionDuringCreate_BecomesManagedExceptionWithoutLeaking()
    {
        int before = NativeMethods.LiveHandles();
        var error = Assert.ThrowsException<NativeInspectionException>(() => new NativeInspector(NativeFaultMode.ThrowOnCreate));
        Assert.AreEqual(NativeStatus.InternalError, error.Status);
        Assert.AreEqual("Create", error.Operation);
        Assert.AreEqual(before, NativeMethods.LiveHandles());
    }

    [TestMethod]
    public async Task CppExceptionDuringInspect_FaultsRunnerAndPreventsSave()
    {
        int before = NativeMethods.LiveHandles();
        var store = new RecordingStore();
        using (var native = new NativeInspector(NativeFaultMode.ThrowOnInspect))
        {
            var runner = new InspectionRunner(new SimulatedDevice(), native, store);
            var error = await Assert.ThrowsExceptionAsync<InspectionRunException>(() => runner.RunAsync(Job()));
            Assert.AreEqual(InspectionStage.Inspect, error.Stage);
            Assert.IsInstanceOfType<NativeInspectionException>(error.InnerException);
            Assert.AreEqual(NativeStatus.InternalError, ((NativeInspectionException)error.InnerException).Status);
            Assert.IsFalse(store.Called);
            Assert.AreEqual(before + 1, NativeMethods.LiveHandles(), "The Host still owns this adapter after a run failure.");
        }
        Assert.AreEqual(before, NativeMethods.LiveHandles());
    }

    [TestMethod]
    public void DisposeTwice_DestroysExactlyOneNativeObjectAndRejectsReuse()
    {
        int before = NativeMethods.LiveHandles();
        ulong destroyed = NativeMethods.DestroyedHandles();
        using var native = new NativeInspector();
        Assert.AreEqual(before + 1, NativeMethods.LiveHandles());
        native.Dispose();
        native.Dispose();
        Assert.AreEqual(before, NativeMethods.LiveHandles());
        Assert.AreEqual(destroyed + 1, NativeMethods.DestroyedHandles());
        Assert.ThrowsException<ObjectDisposedException>(() => native.InspectAsync(Job(), new double[] { 10 }, CancellationToken.None));
    }

    [TestMethod]
    public async Task AlreadyCanceled_DoesNotEnterThrowingNativeInspector()
    {
        using var native = new NativeInspector(NativeFaultMode.ThrowOnInspect);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => native.InspectAsync(Job(), new double[] { 10 }, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
    }

    [TestMethod]
    public void SafeHandleFinalizer_ReleasesAbandonedNativeObject()
    {
        int before = NativeMethods.LiveHandles();
        ulong destroyed = NativeMethods.DestroyedHandles();
        WeakReference abandoned = AbandonInspector();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(abandoned.IsAlive);
        Assert.AreEqual(before, NativeMethods.LiveHandles());
        Assert.AreEqual(destroyed + 1, NativeMethods.DestroyedHandles());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonInspector() => new(new NativeInspector());

    private static NativeInspectorHandle CreateHandle()
    {
        NativeStatus status = NativeMethods.Create(NativeFaultMode.None, out NativeInspectorHandle handle);
        Assert.AreEqual(NativeStatus.Ok, status);
        return handle;
    }

    private sealed class RecordingStore : IResultStore
    {
        internal bool Called { get; private set; }
        public Task SaveAsync(InspectionResult result, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }
}
