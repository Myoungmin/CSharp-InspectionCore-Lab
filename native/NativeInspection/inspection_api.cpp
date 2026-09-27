#include "inspection_api.h"

#include <atomic>
#include <cmath>
#include <cstddef>
#include <memory>
#include <new>
#include <stdexcept>

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
};

uint32_t __cdecl inspection_abi_version(void) noexcept { return 1; }
uint32_t __cdecl inspection_result_size(void) noexcept { return sizeof(inspection_result); }
int32_t __cdecl inspection_live_handles(void) noexcept { return g_nLiveHandles.load(); }
uint64_t __cdecl inspection_destroyed_handles(void) noexcept { return g_nDestroyedHandles.load(); }

int32_t __cdecl inspection_create(int32_t nFaultMode, inspection_handle** ppHandle) noexcept
{
    if (ppHandle == nullptr) { return INSPECTION_INVALID_ARGUMENT; }
    *ppHandle = nullptr;
    if (nFaultMode < INSPECTION_FAULT_NONE || nFaultMode > INSPECTION_FAULT_INSPECT)
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

void __cdecl inspection_destroy(inspection_handle* pHandle) noexcept
{
    delete pHandle;
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
