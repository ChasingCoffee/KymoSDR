/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "wdsp_platform.h"
#include <errno.h>
#if defined(__SSE__)
#include <xmmintrin.h>
#endif

/* Existing WDSP constructors do not propagate thread/lock allocation failures.
 * Fail explicitly instead of continuing with invalid synchronization objects. */
static void checked(int result) {
    if (result) { fprintf(stderr, "WDSP POSIX failure: %s\n", strerror(result)); abort(); }
}
void *wdsp_aligned_malloc(size_t size, size_t alignment) {
    void *p = NULL;
    if (posix_memalign(&p, alignment, size ? size : 1)) return NULL;
    return p;
}
int InitializeCriticalSectionAndSpinCount(CRITICAL_SECTION *cs, DWORD spins) {
    (void)spins;
    pthread_mutexattr_t attr;
    checked(pthread_mutexattr_init(&attr));
    checked(pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE));
    checked(pthread_mutex_init(cs, &attr));
    checked(pthread_mutexattr_destroy(&attr));
    return TRUE;
}
void InitializeCriticalSection(CRITICAL_SECTION *cs) { InitializeCriticalSectionAndSpinCount(cs, 0); }
void EnterCriticalSection(CRITICAL_SECTION *cs) { checked(pthread_mutex_lock(cs)); }
void LeaveCriticalSection(CRITICAL_SECTION *cs) { checked(pthread_mutex_unlock(cs)); }
void DeleteCriticalSection(CRITICAL_SECTION *cs) { checked(pthread_mutex_destroy(cs)); }

/* Condition variables avoid macOS's unavailable unnamed semaphores. All handles
 * are process-local: WDSP's diagnostic names do not create shared IPC objects. */
struct wdsp_waitable {
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    LONG count, maximum;
    int event, manual;
};
static HANDLE create_waitable(LONG initial, LONG maximum, int event, int manual) {
    HANDLE h = calloc(1, sizeof(*h));
    if (!h) checked(ENOMEM);
    h->count = initial; h->maximum = maximum; h->event = event; h->manual = manual;
    checked(pthread_mutex_init(&h->mutex, NULL));
    pthread_condattr_t attr;
    checked(pthread_condattr_init(&attr));
#ifndef __APPLE__
    checked(pthread_condattr_setclock(&attr, CLOCK_MONOTONIC));
#endif
    checked(pthread_cond_init(&h->condition, &attr));
    checked(pthread_condattr_destroy(&attr));
    return h;
}
HANDLE CreateSemaphore(void *security, LONG initial, LONG maximum, const char *name) {
    (void)security; (void)name;
    if (initial < 0 || maximum <= 0 || initial > maximum) return NULL;
    return create_waitable(initial, maximum, 0, 0);
}
int ReleaseSemaphore(HANDLE h, LONG count, LONG *previous) {
    if (!h || h->event || count <= 0) return FALSE;
    checked(pthread_mutex_lock(&h->mutex));
    int ok = count <= h->maximum - h->count;
    if (ok) {
        if (previous) *previous = h->count;
        h->count += count;
        checked(pthread_cond_broadcast(&h->condition));
    }
    checked(pthread_mutex_unlock(&h->mutex));
    return ok;
}
HANDLE CreateEvent(void *security, int manual, int initial, const char *name) {
    (void)security; (void)name;
    return create_waitable(!!initial, 1, 1, !!manual);
}
int SetEvent(HANDLE h) {
    if (!h || !h->event) return FALSE;
    checked(pthread_mutex_lock(&h->mutex));
    h->count = 1;
    checked(pthread_cond_broadcast(&h->condition));
    checked(pthread_mutex_unlock(&h->mutex));
    return TRUE;
}
int ResetEvent(HANDLE h) {
    if (!h || !h->event) return FALSE;
    checked(pthread_mutex_lock(&h->mutex)); h->count = 0;
    checked(pthread_mutex_unlock(&h->mutex)); return TRUE;
}
static struct timespec deadline_after(DWORD ms) {
    struct timespec t; checked(clock_gettime(CLOCK_MONOTONIC, &t) ? errno : 0);
    t.tv_sec += ms / 1000;
    t.tv_nsec += (long)(ms % 1000) * 1000000;
    if (t.tv_nsec >= 1000000000) { t.tv_sec++; t.tv_nsec -= 1000000000; }
    return t;
}
DWORD WaitForSingleObject(HANDLE h, DWORD ms) {
    if (!h) return WAIT_FAILED;
    struct timespec deadline = deadline_after(ms == INFINITE ? 0 : ms);
    checked(pthread_mutex_lock(&h->mutex));
    int result = 0;
    while (!h->count && !result) {
        if (!ms) { result = ETIMEDOUT; break; }
        if (ms == INFINITE) result = pthread_cond_wait(&h->condition, &h->mutex);
        else {
#ifdef __APPLE__
            struct timespec now = deadline_after(0), remaining;
            remaining.tv_sec = deadline.tv_sec - now.tv_sec;
            remaining.tv_nsec = deadline.tv_nsec - now.tv_nsec;
            if (remaining.tv_nsec < 0) { remaining.tv_sec--; remaining.tv_nsec += 1000000000; }
            result = remaining.tv_sec < 0 ? ETIMEDOUT :
                pthread_cond_timedwait_relative_np(&h->condition, &h->mutex, &remaining);
#else
            result = pthread_cond_timedwait(&h->condition, &h->mutex, &deadline);
#endif
        }
    }
    DWORD status = WAIT_OBJECT_0;
    if (h->count) { if (!h->manual) h->count--; }
    else status = result == ETIMEDOUT ? WAIT_TIMEOUT : WAIT_FAILED;
    checked(pthread_mutex_unlock(&h->mutex));
    return status;
}
int CloseHandle(HANDLE h) {
    if (!h) return FALSE;
    /* Caller must stop/join users before closing. */
    checked(pthread_cond_destroy(&h->condition));
    checked(pthread_mutex_destroy(&h->mutex)); free(h); return TRUE;
}
void Sleep(DWORD ms) {
    struct timespec t = {(time_t)(ms / 1000), (long)(ms % 1000) * 1000000};
    while (nanosleep(&t, &t) && errno == EINTR) {}
}
void wdsp_flush_denormals(void) {
#if defined(__aarch64__)
    uint64_t fpcr;
    __asm__ volatile("mrs %0, fpcr" : "=r"(fpcr));
    fpcr |= UINT64_C(1) << 24;
    __asm__ volatile("msr fpcr, %0" : : "r"(fpcr));
#elif defined(__SSE__)
    _MM_SET_FLUSH_ZERO_MODE(_MM_FLUSH_ZERO_ON);
#endif
}
struct wdsp_work { void (*function)(void *); DWORD (*work)(void *); void *argument; };
static void *dispatch(void *p) {
    struct wdsp_work work = *(struct wdsp_work *)p;
    free(p); wdsp_flush_denormals();
    if (work.function) work.function(work.argument); else work.work(work.argument);
    return NULL;
}
static pthread_t start(void (*fn)(void *), DWORD (*work)(void *), void *arg, unsigned size, int detached) {
    struct wdsp_work *context = malloc(sizeof(*context));
    if (!context) checked(ENOMEM);
    *context = (struct wdsp_work){fn, work, arg};
    pthread_attr_t attr; pthread_t thread;
    checked(pthread_attr_init(&attr));
    if (size) checked(pthread_attr_setstacksize(&attr, size));
    if (detached) checked(pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED));
    checked(pthread_create(&thread, &attr, dispatch, context));
    checked(pthread_attr_destroy(&attr)); return thread;
}
uintptr_t _beginthread(void (*fn)(void *), unsigned size, void *arg) {
    start(fn, NULL, arg, size, 1);
    return 1; /* Existing callers discard this; not a waitable HANDLE. */
}
void _endthread(void) { pthread_exit(NULL); }
int QueueUserWorkItem(DWORD (*fn)(void *), void *arg, DWORD flags) {
    (void)flags; start(NULL, fn, arg, 0, 1); return TRUE;
}
pthread_t wdsp_start_joinable(void (*fn)(void *), void *arg) { return start(fn, NULL, arg, 0, 0); }
void wdsp_join(pthread_t thread) { checked(pthread_join(thread, NULL)); }
