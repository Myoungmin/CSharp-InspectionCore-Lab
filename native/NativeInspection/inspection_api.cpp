#include "inspection_api.h"

#include <atomic>
#include <cmath>
#include <cstddef>
#include <memory>
#include <new>
#include <stdexcept>
#include <thread>
#include <vector>

static_assert(sizeof(void*) == 8, "Only Windows x64 is supported.");
static_assert(sizeof(double) == 8, "The ABI requires 64-bit doubles.");
static_assert(sizeof(inspection_result) == 16, "Unexpected result layout.");
static_assert(offsetof(inspection_result, dScore) == 8, "Unexpected score offset.");

namespace
{
    std::atomic<int32_t> g_nLiveHandles{0};
    std::atomic<uint64_t> g_nDestroyedHandles{0};
}

struct inspection_handle
{
    explicit inspection_handle(int32_t nMode) noexcept : nFaultMode(nMode)
    {
        ++g_nLiveHandles;
    }

    ~inspection_handle() noexcept
    {
        --g_nLiveHandles;
        ++g_nDestroyedHandles;
    }

    const int32_t nFaultMode;
    std::atomic<bool> bStop{false};
    std::atomic<uint64_t> nProgressCallbacks{0};
    std::thread worker;
    int32_t nOperationStatus = INSPECTION_OK;
    inspection_result result{};
};

uint32_t __cdecl inspection_abi_version(void) noexcept { return 2; }
uint32_t __cdecl inspection_result_size(void) noexcept { return sizeof(inspection_result); }
int32_t __cdecl inspection_live_handles(void) noexcept { return g_nLiveHandles.load(); }
uint64_t __cdecl inspection_destroyed_handles(void) noexcept { return g_nDestroyedHandles.load(); }
uint64_t __cdecl inspection_progress_callbacks(inspection_handle* pHandle) noexcept
{
    return pHandle == nullptr ? 0 : pHandle->nProgressCallbacks.load();
}

int32_t __cdecl inspection_create(int32_t nFaultMode, inspection_handle** ppHandle) noexcept
{
    if (ppHandle == nullptr) { return INSPECTION_INVALID_ARGUMENT; }
    *ppHandle = nullptr;
    if (nFaultMode < INSPECTION_FAULT_NONE || nFaultMode > INSPECTION_FAULT_WAIT)
    {
        return INSPECTION_INVALID_ARGUMENT;
    }
    try
    {
        if (nFaultMode == INSPECTION_FAULT_CREATE) { throw std::runtime_error("Simulated creation failure."); }
        auto pInspector = std::make_unique<inspection_handle>(nFaultMode);
        *ppHandle = pInspector.release();
        return INSPECTION_OK;
    }
    catch (const std::bad_alloc&) { return INSPECTION_OUT_OF_MEMORY; }
    catch (...) { return INSPECTION_INTERNAL_ERROR; }
}

int32_t __cdecl inspection_destroy(inspection_handle* pHandle) noexcept
{
    // Never delete a joinable thread or invalidate an outstanding callback/context.
    if (pHandle != nullptr && pHandle->worker.joinable()) { return INSPECTION_BUSY; }
    delete pHandle;
    return INSPECTION_OK;
}

int32_t __cdecl inspection_start(inspection_handle* pHandle, const double* pSamples,
    int32_t nCount, double dLowerBound, double dUpperBound, inspection_progress pProgress, void* pContext) noexcept
{
    if (pHandle == nullptr || pSamples == nullptr || nCount <= 0 ||
        !std::isfinite(dLowerBound) || !std::isfinite(dUpperBound) || dLowerBound > dUpperBound)
    {
        return INSPECTION_INVALID_ARGUMENT;
    }
    if (pHandle->worker.joinable()) { return INSPECTION_BUSY; }
    try
    {
        std::vector<double> samples(pSamples, pSamples + nCount);
        for (double dValue : samples)
        {
            if (!std::isfinite(dValue)) { return INSPECTION_INVALID_ARGUMENT; }
        }
        pHandle->bStop = false;
        pHandle->result = {};
        pHandle->nOperationStatus = INSPECTION_OK;
        pHandle->worker = std::thread([pHandle, samples = std::move(samples), dLowerBound, dUpperBound, pProgress, pContext]() noexcept
        {
            const int32_t nTotal = static_cast<int32_t>(samples.size());
            const auto report = [=](int32_t nCompleted)
            {
                if (pProgress != nullptr)
                {
                    ++pHandle->nProgressCallbacks;
                    pProgress(pContext, nCompleted, nTotal);
                }
            };
            try
            {
                report(0);
                if (pHandle->nFaultMode == INSPECTION_FAULT_INSPECT) { throw std::runtime_error("Simulated inspection failure."); }
                int32_t nDefects = 0;
                for (int32_t nIndex = 0; nIndex < nTotal; ++nIndex)
                {
                    if (pHandle->bStop.load())
                    {
                        // A final notification may already be in flight after RequestStop.
                        report(nIndex);
                        pHandle->nOperationStatus = INSPECTION_CANCELED;
                        return;
                    }
                    const double dValue = samples[nIndex];
                    if (dValue < dLowerBound || dValue > dUpperBound) { ++nDefects; }
                    report(nIndex + 1);
                }
                pHandle->result = {nTotal, nDefects, 100.0 * (nTotal - nDefects) / nTotal};
            }
            catch (const std::bad_alloc&) { pHandle->nOperationStatus = INSPECTION_OUT_OF_MEMORY; }
            catch (...) { pHandle->nOperationStatus = INSPECTION_INTERNAL_ERROR; }
        });
        return INSPECTION_OK;
    }
    catch (const std::bad_alloc&) { return INSPECTION_OUT_OF_MEMORY; }
    catch (...) { return INSPECTION_INTERNAL_ERROR; }
}

int32_t __cdecl inspection_request_stop(inspection_handle* pHandle) noexcept
{
    if (pHandle == nullptr) { return INSPECTION_INVALID_ARGUMENT; }
    try
    {
        if (pHandle->nFaultMode == INSPECTION_FAULT_STOP) { throw std::runtime_error("Simulated stop failure."); }
        pHandle->bStop = true;
        return INSPECTION_OK;
    }
    catch (...) { return INSPECTION_INTERNAL_ERROR; }
}

int32_t __cdecl inspection_wait(inspection_handle* pHandle, int32_t* pOperationStatus, inspection_result* pResult) noexcept
{
    if (pOperationStatus == nullptr || pResult == nullptr) { return INSPECTION_INVALID_ARGUMENT; }
    *pOperationStatus = INSPECTION_INTERNAL_ERROR;
    *pResult = {};
    if (pHandle == nullptr || !pHandle->worker.joinable()) { return INSPECTION_INVALID_ARGUMENT; }
    try
    {
        if (pHandle->nFaultMode == INSPECTION_FAULT_WAIT) { throw std::runtime_error("Simulated wait failure."); }
        pHandle->worker.join();
        *pOperationStatus = pHandle->nOperationStatus;
        *pResult = pHandle->result;
        return INSPECTION_OK;
    }
    catch (...) { return INSPECTION_INTERNAL_ERROR; }
}

int32_t __cdecl inspection_run(inspection_handle* pHandle, const double* pSamples,
    int32_t nCount, double dLowerBound, double dUpperBound, inspection_result* pResult) noexcept
{
    if (pResult == nullptr) { return INSPECTION_INVALID_ARGUMENT; }
    *pResult = {};
    if (pHandle == nullptr || pSamples == nullptr || nCount <= 0 ||
        !std::isfinite(dLowerBound) || !std::isfinite(dUpperBound) || dLowerBound > dUpperBound)
    {
        return INSPECTION_INVALID_ARGUMENT;
    }
    try
    {
        if (pHandle->nFaultMode == INSPECTION_FAULT_INSPECT) { throw std::runtime_error("Simulated inspection failure."); }
        int32_t nDefects = 0;
        for (int32_t nIndex = 0; nIndex < nCount; ++nIndex)
        {
            const double dValue = pSamples[nIndex];
            if (!std::isfinite(dValue)) { return INSPECTION_INVALID_ARGUMENT; }
            if (dValue < dLowerBound || dValue > dUpperBound) { ++nDefects; }
        }
        *pResult = {nCount, nDefects, 100.0 * (nCount - nDefects) / nCount};
        return INSPECTION_OK;
    }
    catch (const std::bad_alloc&) { return INSPECTION_OUT_OF_MEMORY; }
    catch (...) { return INSPECTION_INTERNAL_ERROR; }
}
