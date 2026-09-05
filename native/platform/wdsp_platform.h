/* Thetis POSIX adaptation. SPDX-License-Identifier: GPL-2.0-or-later
 * Private compatibility surface for existing WDSP source, not a Win32 emulator.
 */
#ifndef THETIS_WDSP_PLATFORM_H
#define THETIS_WDSP_PLATFORM_H
#include <stdint.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include <time.h>

typedef int32_t LONG;
typedef uint32_t DWORD;
typedef unsigned char byte;
typedef pthread_mutex_t CRITICAL_SECTION;
typedef CRITICAL_SECTION *LPCRITICAL_SECTION;
typedef struct wdsp_waitable *HANDLE;
#define WINAPI
#define __cdecl
#define __stdcall
#define __forceinline inline __attribute__((always_inline))
#define __declspec(x) WDSP_DECL_##x
#define WDSP_DECL_dllexport __attribute__((visibility("default")))
#define WDSP_DECL_align(n) __attribute__((aligned(n)))
#define FALSE 0
#define TRUE 1
#define TEXT(x) x
#define INFINITE UINT32_MAX
#define WAIT_OBJECT_0 0u
#define WAIT_TIMEOUT 258u
#define WAIT_FAILED UINT32_MAX
#define min(a,b) ((a) < (b) ? (a) : (b))
#define max(a,b) ((a) > (b) ? (a) : (b))
// WDSP's private debug helper predates POSIX dprintf(int, ...).
#define dprintf wdsp_debug_printf
#define OutputDebugStringA(message) ((void)fputs((message), stderr))

/* Preserve Win32 return semantics (the old value, or the previous bit), with
 * sequential consistency. Builtins operate on the actual pointed-to type. */
#define InterlockedAnd(p,v) __atomic_fetch_and((p),(v),__ATOMIC_SEQ_CST)
#define _InterlockedAnd(p,v) InterlockedAnd((p),(v))
#define InterlockedExchange(p,v) __atomic_exchange_n((p),(v),__ATOMIC_SEQ_CST)
#define InterlockedIncrement(p) __atomic_add_fetch((p),1,__ATOMIC_SEQ_CST)
#define InterlockedDecrement(p) __atomic_sub_fetch((p),1,__ATOMIC_SEQ_CST)
#define InterlockedBitTestAndSet(p,b) ((unsigned char)((__atomic_fetch_or((p),UINT32_C(1) << (b),__ATOMIC_SEQ_CST) >> (b)) & 1))
#define InterlockedBitTestAndReset(p,b) ((unsigned char)((__atomic_fetch_and((p),~(UINT32_C(1) << (b)),__ATOMIC_SEQ_CST) >> (b)) & 1))

void *wdsp_aligned_malloc(size_t size, size_t alignment);
#define _aligned_malloc wdsp_aligned_malloc
#define _aligned_free free
void InitializeCriticalSection(CRITICAL_SECTION *cs);
int InitializeCriticalSectionAndSpinCount(CRITICAL_SECTION *cs, DWORD spins);
void EnterCriticalSection(CRITICAL_SECTION *cs);
void LeaveCriticalSection(CRITICAL_SECTION *cs);
void DeleteCriticalSection(CRITICAL_SECTION *cs);
HANDLE CreateSemaphore(void *security, LONG initial, LONG maximum, const char *name);
int ReleaseSemaphore(HANDLE handle, LONG count, LONG *previous);
HANDLE CreateEvent(void *security, int manual, int initial, const char *name);
int SetEvent(HANDLE handle);
int ResetEvent(HANDLE handle);
DWORD WaitForSingleObject(HANDLE handle, DWORD milliseconds);
int CloseHandle(HANDLE handle);
void Sleep(DWORD milliseconds);
uintptr_t _beginthread(void (*function)(void *), unsigned stack_size, void *argument);
void _endthread(void);
int QueueUserWorkItem(DWORD (*function)(void *), void *argument, DWORD flags);
pthread_t wdsp_start_joinable(void (*function)(void *), void *argument);
void wdsp_join(pthread_t thread);
void wdsp_flush_denormals(void);
#endif
