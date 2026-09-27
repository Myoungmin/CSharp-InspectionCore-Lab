using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]

namespace Inspection.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInspectionResult
{
    internal int SampleCount;
    internal int DefectCount;
    internal double Score;
}

internal sealed class NativeInspectorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeInspectorHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle()
    {
        NativeMethods.Destroy(handle);
        return true;
    }
}

internal static unsafe partial class NativeMethods
{
    private const string LibraryName = "NativeInspection";

    [LibraryImport(LibraryName, EntryPoint = "inspection_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "inspection_result_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ResultSize();

    [LibraryImport(LibraryName, EntryPoint = "inspection_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeStatus Create(NativeFaultMode mode, out NativeInspectorHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "inspection_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "inspection_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeStatus Run(NativeInspectorHandle handle, double* samples, int count,
        double lowerBound, double upperBound, NativeInspectionResult* result);

    [LibraryImport(LibraryName, EntryPoint = "inspection_live_handles")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int LiveHandles();

    [LibraryImport(LibraryName, EntryPoint = "inspection_destroyed_handles")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong DestroyedHandles();
}
