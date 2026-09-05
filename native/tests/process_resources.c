/* SPDX-License-Identifier: GPL-2.0-or-later
 * Test-only OS observations, separate from ChannelMaster's legacy typedefs.
 */
#include <stdint.h>
#include <stdio.h>
#ifdef _WIN32
#include <Windows.h>
#include <TlHelp32.h>
#include <Psapi.h>
#elif defined(__APPLE__)
#include <mach/mach.h>
#else
#include <string.h>
#endif
#ifndef _WIN32
#include <dirent.h>
#endif

int test_process_descriptors(void)
{
#ifdef _WIN32
    DWORD count;
    return GetProcessHandleCount(GetCurrentProcess(), &count) ? (int)count : -1;
#else
#ifdef __APPLE__
    DIR *directory = opendir("/dev/fd");
#else
    DIR *directory = opendir("/proc/self/fd");
#endif
    if (!directory) return -1;
    int count = 0;
    struct dirent *entry;
    while ((entry = readdir(directory))) if (entry->d_name[0] != '.') ++count;
    closedir(directory);
    return count - 1; // exclude this observer's directory descriptor
#endif
}

int test_process_threads(void)
{
#ifdef _WIN32
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return -1;
    THREADENTRY32 entry = {0}; entry.dwSize = sizeof(entry);
    int count = 0;
    if (!Thread32First(snapshot, &entry)) { CloseHandle(snapshot); return -1; }
    do { if (entry.th32OwnerProcessID == GetCurrentProcessId()) ++count; } while (Thread32Next(snapshot, &entry));
    CloseHandle(snapshot);
    return count;
#elif defined(__APPLE__)
    thread_act_array_t threads;
    mach_msg_type_number_t count;
    if (task_threads(mach_task_self(), &threads, &count) != KERN_SUCCESS) return -1;
    for (unsigned i = 0; i < count; ++i) mach_port_deallocate(mach_task_self(), threads[i]);
    vm_deallocate(mach_task_self(), (vm_address_t)threads, count * sizeof(*threads));
    return (int)count;
#else
    FILE *file = fopen("/proc/self/status", "r");
    if (!file) return -1;
    char line[256]; int count = -1;
    while (fgets(line, sizeof(line), file)) if (sscanf(line, "Threads: %d", &count) == 1) break;
    fclose(file);
    return count;
#endif
}
uint64_t test_resident_bytes(void)
{
#ifdef _WIN32
    PROCESS_MEMORY_COUNTERS info = {0}; info.cb = sizeof(info);
    if (!GetProcessMemoryInfo(GetCurrentProcess(), &info, sizeof(info))) return 0;
    return (uint64_t)info.WorkingSetSize;
#elif defined(__APPLE__)
    mach_task_basic_info_data_t info;
    mach_msg_type_number_t count = MACH_TASK_BASIC_INFO_COUNT;
    if (task_info(mach_task_self(), MACH_TASK_BASIC_INFO, (task_info_t)&info, &count) != KERN_SUCCESS) return 0;
    return (uint64_t)info.resident_size;
#else
    FILE *file = fopen("/proc/self/status", "r");
    if (!file) return 0;
    char line[256]; unsigned long long kilobytes = 0;
    while (fgets(line, sizeof(line), file)) if (sscanf(line, "VmRSS: %llu", &kilobytes) == 1) break;
    fclose(file);
    return (uint64_t)kilobytes * 1024;
#endif
}
