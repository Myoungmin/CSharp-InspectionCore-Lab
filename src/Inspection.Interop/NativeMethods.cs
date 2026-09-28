using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]

namespace Inspection.Interop;

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
    internal static partial NativeStatus Destroy(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "inspection_start")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeStatus Start(NativeInspectorHandle handle, double* samples, int count,
        double lowerBound, double upperBound, delegate* unmanaged[Cdecl]<nint, int, int, void> progress, nint context);

    [LibraryImport(LibraryName, EntryPoint = "inspection_request_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeStatus RequestStop(NativeInspectorHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "inspection_wait")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeStatus Wait(NativeInspectorHandle handle, out NativeStatus operationStatus,
        out NativeInspectionResult result);

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

    [LibraryImport(LibraryName, EntryPoint = "inspection_progress_callbacks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong ProgressCallbacks(NativeInspectorHandle handle);
}
