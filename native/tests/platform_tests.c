/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "wdsp_platform.h"
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed: %s at %d\n", #x, __LINE__); exit(1); } } while (0)
static double seconds(void) {
    struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + t.tv_nsec * 1e-9;
}
static volatile LONG counter;
static void count(void *unused) {
    (void)unused;
    for (int i = 0; i < 10000; ++i) InterlockedIncrement(&counter);
}
static void release_later(void *p) { Sleep(20); CHECK(ReleaseSemaphore(p, 1, NULL)); }
struct queued { HANDLE gate, done; };
static DWORD queued_work(void *p) {
    struct queued *q = p;
    CHECK(WaitForSingleObject(q->gate, INFINITE) == WAIT_OBJECT_0);
    CHECK(SetEvent(q->done)); return 0;
}
int main(void) {
    CHECK(sizeof(LONG) == 4 && sizeof(DWORD) == 4);
    volatile LONG bits = 8;
    CHECK(InterlockedBitTestAndSet(&bits, 3) == 1);
    CHECK(InterlockedBitTestAndReset(&bits, 3) == 1 && bits == 0);
    CHECK(InterlockedBitTestAndSet(&bits, 0) == 0 && bits == 1);
    CHECK(InterlockedExchange(&bits, -1) == 1);
    CHECK(InterlockedAnd(&bits, 7) == -1 && bits == 7);
    CHECK(InterlockedDecrement(&bits) == 6);
    pthread_t threads[4];
    for (int i = 0; i < 4; ++i) threads[i] = wdsp_start_joinable(count, NULL);
    for (int i = 0; i < 4; ++i) wdsp_join(threads[i]);
    CHECK(counter == 40000);
    void *p = _aligned_malloc(1001, 64);
    CHECK(p && ((uintptr_t)p % 64) == 0); _aligned_free(p);
    CRITICAL_SECTION cs;
    InitializeCriticalSection(&cs);
    EnterCriticalSection(&cs); EnterCriticalSection(&cs);
    LeaveCriticalSection(&cs); LeaveCriticalSection(&cs); DeleteCriticalSection(&cs);
    HANDLE sem = CreateSemaphore(NULL, 2, 3, NULL);
    CHECK(sem && !ReleaseSemaphore(sem, 2, NULL));
    CHECK(WaitForSingleObject(sem, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(sem, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(sem, 0) == WAIT_TIMEOUT);
    double started = seconds();
    CHECK(WaitForSingleObject(sem, 20) == WAIT_TIMEOUT);
    CHECK(seconds() - started >= 0.015 && seconds() - started < 2);
    pthread_t worker = wdsp_start_joinable(release_later, sem);
    CHECK(WaitForSingleObject(sem, 2000) == WAIT_OBJECT_0);
    wdsp_join(worker); CHECK(CloseHandle(sem));
    CHECK(CreateSemaphore(NULL, 2, 1, NULL) == NULL);
    HANDLE automatic = CreateEvent(NULL, FALSE, TRUE, "snap");
    CHECK(WaitForSingleObject(automatic, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(automatic, 0) == WAIT_TIMEOUT);
    CHECK(SetEvent(automatic) && SetEvent(automatic));
    CHECK(WaitForSingleObject(automatic, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(automatic, 0) == WAIT_TIMEOUT);
    CHECK(CloseHandle(automatic));
    HANDLE manual = CreateEvent(NULL, TRUE, FALSE, "snap");
    CHECK(SetEvent(manual));
    CHECK(WaitForSingleObject(manual, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(manual, 0) == WAIT_OBJECT_0);
    CHECK(ResetEvent(manual));
    CHECK(WaitForSingleObject(manual, 0) == WAIT_TIMEOUT);
    CHECK(CloseHandle(manual));
    struct queued q = {CreateEvent(NULL, FALSE, FALSE, NULL), CreateEvent(NULL, FALSE, FALSE, NULL)};
    CHECK(QueueUserWorkItem(queued_work, &q, 0)); // must not execute synchronously
    CHECK(SetEvent(q.gate));
    CHECK(WaitForSingleObject(q.done, 2000) == WAIT_OBJECT_0);
    CHECK(CloseHandle(q.gate)); CHECK(CloseHandle(q.done));
    puts("PASS: widths, atomics, alignment, recursive locks, semaphores, events, asynchronous work, joins");
    return 0;
}
