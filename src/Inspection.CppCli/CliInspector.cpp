#include <vcclr.h>
#include "../../native/NativeInspection/inspection_api.h"

// Keep the opaque native type out of CLR metadata; only void* crosses this thunk.
#pragma managed(push, off)
namespace native_bridge {
    static int create(int mode, void** output)
    {
        inspection_handle* handle = nullptr;
        int status = inspection_create(mode, &handle);
        *output = handle;
        return status;
    }
    static int destroy(void* handle) { return inspection_destroy(static_cast<inspection_handle*>(handle)); }
    static int start(void* handle, const double* samples, int count, double lower, double upper, void* progress, void* context)
    {
        return inspection_start(static_cast<inspection_handle*>(handle), samples, count, lower, upper,
            reinterpret_cast<inspection_progress>(progress), context);
    }
    static int stop(void* handle) { return inspection_request_stop(static_cast<inspection_handle*>(handle)); }
    static int wait(void* handle, int32_t* operation, inspection_result* result)
    {
        return inspection_wait(static_cast<inspection_handle*>(handle), operation, result);
    }
    static uint64_t callbacks(void* handle) { return inspection_progress_callbacks(static_cast<inspection_handle*>(handle)); }
}
#pragma managed(pop)

using namespace System;
using namespace System::Runtime::InteropServices;
using namespace System::Threading;
using namespace System::Threading::Tasks;
using namespace Inspection::Core;
using namespace Inspection::Interop;

namespace Inspection { namespace CppCli {

// This transport calls the import library directly. No P/Invoke is used here.
public ref class CliNativeApi sealed : INativeInspectionApi
{
private:
    static NativeStatus Destroy(IntPtr handle)
    {
        return static_cast<NativeStatus>(native_bridge::destroy(handle.ToPointer()));
    }

public:
    virtual UInt32 AbiVersion() { return inspection_abi_version(); }
    virtual UInt32 ResultSize() { return inspection_result_size(); }

    virtual NativeStatus Create(NativeFaultMode mode, [Out] NativeInspectorHandle^% handle)
    {
        void* native = nullptr;
        int status = native_bridge::create(static_cast<int>(mode), &native);
        try
        {
            handle = gcnew NativeInspectorHandle(IntPtr(native), gcnew Func<IntPtr, NativeStatus>(&CliNativeApi::Destroy));
        }
        catch (Exception^)
        {
            if (native != nullptr) { native_bridge::destroy(native); }
            throw;
        }
        return static_cast<NativeStatus>(status);
    }

    virtual NativeStatus Start(NativeInspectorHandle^ handle, array<double>^ samples,
        double lowerBound, double upperBound, IntPtr progress, IntPtr context)
    {
        ArgumentNullException::ThrowIfNull(handle);
        ArgumentNullException::ThrowIfNull(samples);
        bool retained = false;
        try
        {
            handle->DangerousAddRef(retained);
            // Pin only through Start, which copies the input into the native worker.
            pin_ptr<double> pinned = samples->Length == 0 ? nullptr : &samples[0];
            return static_cast<NativeStatus>(native_bridge::start(
                handle->DangerousGetHandle().ToPointer(),
                pinned, samples->Length, lowerBound, upperBound,
                progress.ToPointer(), context.ToPointer()));
        }
        finally { if (retained) { handle->DangerousRelease(); } }
    }

    virtual NativeStatus RequestStop(NativeInspectorHandle^ handle)
    {
        bool retained = false;
        try
        {
            handle->DangerousAddRef(retained);
            return static_cast<NativeStatus>(native_bridge::stop(handle->DangerousGetHandle().ToPointer()));
        }
        finally { if (retained) { handle->DangerousRelease(); } }
    }

    virtual NativeStatus Wait(NativeInspectorHandle^ handle, [Out] NativeStatus% operationStatus,
        [Out] NativeInspectionResult% result)
    {
        bool retained = false;
        try
        {
            handle->DangerousAddRef(retained);
            int32_t operation = INSPECTION_INTERNAL_ERROR;
            inspection_result native = {};
            int status = native_bridge::wait(handle->DangerousGetHandle().ToPointer(), &operation, &native);
            operationStatus = static_cast<NativeStatus>(operation);
            result.SampleCount = native.nSampleCount;
            result.DefectCount = native.nDefectCount;
            result.Score = native.dScore;
            return static_cast<NativeStatus>(status);
        }
        finally { if (retained) { handle->DangerousRelease(); } }
    }

    virtual UInt64 ProgressCallbacks(NativeInspectorHandle^ handle)
    {
        bool retained = false;
        try
        {
            handle->DangerousAddRef(retained);
            return native_bridge::callbacks(handle->DangerousGetHandle().ToPointer());
        }
        finally { if (retained) { handle->DangerousRelease(); } }
    }
};

public ref class CliInspector sealed : IInspector, IAsyncDisposable
{
private:
    NativeInspector^ _lifetime;

public:
    CliInspector() : CliInspector(NativeFaultMode::None, nullptr) { }
    CliInspector(NativeFaultMode faultMode) : CliInspector(faultMode, nullptr) { }
    CliInspector(Action<NativeInspectionProgress^>^ progress) : CliInspector(NativeFaultMode::None, progress) { }

    CliInspector(NativeFaultMode faultMode, Action<NativeInspectionProgress^>^ progress)
    {
        _lifetime = gcnew NativeInspector(faultMode, progress, gcnew CliNativeApi());
    }

    virtual Task<InspectionAssessment^>^ InspectAsync(InspectionJob^ job, ReadOnlyMemory<double> samples, CancellationToken token)
    {
        return _lifetime->InspectAsync(job, samples, token);
    }

    ~CliInspector() { delete _lifetime; }
    virtual ValueTask DisposeAsync() { return _lifetime->DisposeAsync(); }
    property UInt64 ProgressCallbackCount { UInt64 get() { return _lifetime->ProgressCallbackCount; } }
};

} }
