#pragma once

#include <stdint.h>

#ifdef NATIVE_INSPECTION_EXPORTS
#define INSPECTION_API __declspec(dllexport)
#else
#define INSPECTION_API __declspec(dllimport)
#endif

#ifdef __cplusplus
#define INSPECTION_NOEXCEPT noexcept
extern "C" {
#else
#define INSPECTION_NOEXCEPT
#endif

typedef struct inspection_handle inspection_handle;

typedef struct inspection_result
{
    int32_t nSampleCount;
    int32_t nDefectCount;
    double dScore;
} inspection_result;

enum inspection_status
{
    INSPECTION_OK = 0,
    INSPECTION_INVALID_ARGUMENT = 1,
    INSPECTION_OUT_OF_MEMORY = 2,
    INSPECTION_INTERNAL_ERROR = 3
};

/* Fault modes exist only to exercise exception boundaries in this simulated DLL. */
enum inspection_fault_mode
{
    INSPECTION_FAULT_NONE = 0,
    INSPECTION_FAULT_CREATE = 1,
    INSPECTION_FAULT_INSPECT = 2
};

INSPECTION_API uint32_t __cdecl inspection_abi_version(void) INSPECTION_NOEXCEPT;
INSPECTION_API uint32_t __cdecl inspection_result_size(void) INSPECTION_NOEXCEPT;
INSPECTION_API int32_t __cdecl inspection_create(int32_t nFaultMode, inspection_handle** ppHandle) INSPECTION_NOEXCEPT;
/* The caller owns one valid handle and destroys it once, after all calls return. */
INSPECTION_API void __cdecl inspection_destroy(inspection_handle* pHandle) INSPECTION_NOEXCEPT;
/* Samples are borrowed only for this synchronous call; count is in elements. */
INSPECTION_API int32_t __cdecl inspection_run(inspection_handle* pHandle, const double* pSamples,
    int32_t nCount, double dLowerBound, double dUpperBound, inspection_result* pResult) INSPECTION_NOEXCEPT;
/* Process-local diagnostics used by integration tests, never ownership controls. */
INSPECTION_API int32_t __cdecl inspection_live_handles(void) INSPECTION_NOEXCEPT;
INSPECTION_API uint64_t __cdecl inspection_destroyed_handles(void) INSPECTION_NOEXCEPT;

#ifdef __cplusplus
}
#endif
