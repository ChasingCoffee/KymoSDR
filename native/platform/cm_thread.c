/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifdef _WIN32
#include <Windows.h>
#include <process.h>
#else
#include "wdsp_platform.h"
#endif
#include <stdlib.h>
#include "cm_thread.h"
typedef struct { void (*function)(void *); void *argument; } cm_start;
static volatile LONG owned;
#ifdef _WIN32
static unsigned __stdcall run(void *argument)
#else
static void *run(void *argument)
#endif
{
    cm_start start = *(cm_start *)argument;
    free(argument);
#ifndef _WIN32
    wdsp_flush_denormals();
#endif
    start.function(start.argument);
    return 0;
}
int cm_try_start_thread(cm_thread *thread, void (*function)(void *), void *argument)
{
    cm_start *start = malloc(sizeof(*start));
    if (!start) return -1;
    *start = (cm_start){function, argument};
#ifdef _WIN32
    *thread = (HANDLE)_beginthreadex(NULL, 0, run, start, 0, NULL);
    if (!*thread) { free(start); return -1; }
#else
    if (pthread_create(thread, NULL, run, start)) { free(start); return -1; }
#endif
    InterlockedIncrement(&owned);
    return 0;
}
cm_thread cm_start_thread(void (*function)(void *), void *argument)
{
    cm_thread thread;
    if (cm_try_start_thread(&thread, function, argument)) abort();
    return thread;
}
void cm_join_thread(cm_thread thread)
{
#ifdef _WIN32
    if (WaitForSingleObject(thread, INFINITE) != WAIT_OBJECT_0) abort();
    CloseHandle(thread);
#else
    if (pthread_join(thread, NULL)) abort();
#endif
    InterlockedDecrement(&owned);
}
void cm_exit_thread(void)
{
#ifdef _WIN32
    _endthreadex(0);
#else
    pthread_exit(NULL);
#endif
}
int cm_owned_threads(void) { return (int)InterlockedAnd(&owned, -1); }
void cm_wait_inputs(int count, HANDLE *inputs)
{
    for (int i = 0; i < count; ++i)
        if (WaitForSingleObject(inputs[i], INFINITE) != WAIT_OBJECT_0) abort();
}
