using System.Runtime.InteropServices;
using Inspection.Core;

namespace Inspection.Interop;

public sealed class NativeInspector : IInspector, IDisposable
{
    private readonly NativeInspectorHandle _handle;

    public NativeInspector(NativeFaultMode faultMode = NativeFaultMode.None)
    {
        if (!Enum.IsDefined(faultMode)) { throw new ArgumentOutOfRangeException(nameof(faultMode)); }
        if (NativeMethods.AbiVersion() != 1 || NativeMethods.ResultSize() != Marshal.SizeOf<NativeInspectionResult>())
        {
            throw new InvalidOperationException("Unsupported NativeInspection ABI or result layout.");
        }

        NativeStatus status = NativeMethods.Create(faultMode, out NativeInspectorHandle handle);
        if (status != NativeStatus.Ok || handle.IsInvalid)
        {
            handle.Dispose();
            throw new NativeInspectionException("Create", status == NativeStatus.Ok ? NativeStatus.InternalError : status);
        }
        _handle = handle;
    }

    public unsafe Task<InspectionAssessment> InspectAsync(
        InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        NativeInspectionResult result = default;
        NativeStatus status;
        // The DLL borrows this buffer synchronously. SafeHandle marshaling holds the handle during the call.
        fixed (double* pointer = samples.Span)
        {
            status = NativeMethods.Run(_handle, pointer, samples.Length, job.LowerBound, job.UpperBound, &result);
        }
        if (status != NativeStatus.Ok) { throw new NativeInspectionException("Inspect", status); }

        // M2 cannot interrupt a synchronous native call; observe cancellation after it actually returns.
        cancellationToken.ThrowIfCancellationRequested();
        if (result.SampleCount != samples.Length || result.DefectCount < 0 || result.DefectCount > result.SampleCount ||
            result.SampleCount == 0 || !double.IsFinite(result.Score) ||
            Math.Abs(result.Score - 100.0 * (result.SampleCount - result.DefectCount) / result.SampleCount) > 1e-9)
        {
            throw new InvalidOperationException("NativeInspection returned inconsistent result data.");
        }
        return Task.FromResult(new InspectionAssessment(result.SampleCount, result.DefectCount));
    }

    public void Dispose() => _handle.Dispose();
}
