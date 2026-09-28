using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Inspection.Interop;

// Adapter-internal transport contract shared across assemblies, never a Core port.
// Start copies samples. Wait must join the worker and every callback before returning Ok.
public interface INativeInspectionApi
{
    uint AbiVersion();
    uint ResultSize();
    NativeStatus Create(NativeFaultMode mode, out NativeInspectorHandle handle);
    NativeStatus Start(NativeInspectorHandle handle, double[] samples, double lowerBound, double upperBound, nint progress, nint context);
    NativeStatus RequestStop(NativeInspectorHandle handle);
    NativeStatus Wait(NativeInspectorHandle handle, out NativeStatus operationStatus, out NativeInspectionResult result);
    ulong ProgressCallbacks(NativeInspectorHandle handle);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeInspectionResult
{
    public int SampleCount;
    public int DefectCount;
    public double Score;
}

public sealed class NativeInspectorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Func<nint, NativeStatus>? _release;

    public NativeInspectorHandle() : base(ownsHandle: true) { }

    public NativeInspectorHandle(nint handle, Func<nint, NativeStatus> release) : base(ownsHandle: true)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => (_release is null ? NativeMethods.Destroy(handle) : _release(handle)) == NativeStatus.Ok;
}

internal sealed class PInvokeInspectionApi : INativeInspectionApi
{
    internal static readonly PInvokeInspectionApi Instance = new();
    public uint AbiVersion() => NativeMethods.AbiVersion();
    public uint ResultSize() => NativeMethods.ResultSize();
    public NativeStatus Create(NativeFaultMode mode, out NativeInspectorHandle handle) => NativeMethods.Create(mode, out handle);
    public unsafe NativeStatus Start(NativeInspectorHandle handle, double[] samples, double lowerBound, double upperBound, nint progress, nint context)
    {
        fixed (double* pointer = samples)
        {
            return NativeMethods.Start(handle, pointer, samples.Length, lowerBound, upperBound,
                (delegate* unmanaged[Cdecl]<nint, int, int, void>)progress, context);
        }
    }
    public NativeStatus RequestStop(NativeInspectorHandle handle) => NativeMethods.RequestStop(handle);
    public NativeStatus Wait(NativeInspectorHandle handle, out NativeStatus operationStatus, out NativeInspectionResult result) =>
        NativeMethods.Wait(handle, out operationStatus, out result);
    public ulong ProgressCallbacks(NativeInspectorHandle handle) => NativeMethods.ProgressCallbacks(handle);
}
