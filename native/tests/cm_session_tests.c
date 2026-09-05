/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "cm_session.h"
#include <stdio.h>
#include <stdlib.h>
extern void init_impulse_cache(int);
extern void destroy_impulse_cache(void);
extern void ThetisWdspSetPlanningTimeLimit(double);
extern int StartAudioIVAC(int);
extern int test_process_threads(void);
extern uint64_t test_resident_bytes(void);
static int baseline_threads;
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed: %s at %d\n", #x, __LINE__); exit(1); } } while (0)
static int at_stage(int stage, void *context)
{
    int target = *(int *)context;
    /* Reentrant commands must fail instead of deadlocking. */
    CHECK(ThetisCmClose() == -2);
    return stage == abs(target) ? (target < 0 ? -1 : 1) : 0;
}
static void closed(void)
{
    int32_t state[17] = {0}; state[16] = 12345;
    CHECK(ThetisCmGetState(state, 16) == 16);
    CHECK(state[0] == 1 && state[1] == 0 && state[2] == 0 && state[10] == 0 && state[16] == 12345);
    CHECK(test_process_threads() == baseline_threads);
}
int main(void)
{
    baseline_threads = test_process_threads();
    CHECK(baseline_threads > 0);
    uint64_t warm_bytes = 0;
    CHECK(ThetisCmP2PortBase(1024, 0) == 1025);
    CHECK(ThetisCmP2PortBase(1024, 1) == 1025);
    CHECK(ThetisCmP2PortBase(5000, 0) == 1025);
    CHECK(ThetisCmP2PortBase(5000, 1) == 5001);
    CHECK(ThetisCmP2PortBase(65519, 1) == -1);
    CHECK(ThetisCmP2PortBase(1024, 2) == -1);
    CHECK(ThetisCmGetState(NULL, 16) == -1);
    CHECK(ThetisCmOpen(2, 192000, 0, 0, NULL, NULL) == -1);
    CHECK(ThetisCmOpen(1, 12345, 0, 0, NULL, NULL) == -1);
    CHECK(ThetisCmOpen(1, 192000, 1, 0, NULL, NULL) == -3);
    CHECK(ThetisCmOpen(1, 192000, 0, 1, NULL, NULL) == -1);
    CHECK(ThetisCmClose() == 0);
    closed();
    init_impulse_cache(0);
    ThetisWdspSetPlanningTimeLimit(0);
    for (int target = -3; target <= 3; ++target)
    {
        if (!target) continue;
        CHECK(ThetisCmOpen(1, 192000, 0, 0, at_stage, &target) == (target < 0 ? -5 : -4));
        closed();
    }
    for (int cycle = 0; cycle < 100; ++cycle)
    {
        int32_t state[16];
        CHECK(ThetisCmOpen(1, 192000, 0, 0, NULL, NULL) == 0);
        CHECK(ThetisCmOpen(1, 192000, 0, 0, NULL, NULL) == -2);
        CHECK(ThetisCmGetState(state, 15) == -1);
        CHECK(ThetisCmGetState(state, 16) == 16);
        CHECK(state[1] == 1 && state[2] == 8 && state[3] == 5 && state[4] == 2 && state[5] == 1 && state[6] == 2);
        CHECK(state[7] == 192000 && state[8] == 48000 && state[9] == 192000);
        CHECK(state[10] == 18 && state[11] == 1 && state[12] == 5 && state[13] == 5);
        CHECK(state[14] == 0 && state[15] == 0);
        CHECK(StartAudioIVAC(0) < 0);
        /* ASIO entry point is private in the combined module; managed audio
         * mode rejection and StartAudioIVAC verify the no-device contract. */
        CHECK(ThetisCmClose() == 0);
        CHECK(ThetisCmClose() == 0);
        closed();
        if (cycle == 9) warm_bytes = test_resident_bytes();
        if ((cycle + 1) % 10 == 0)
        {
            printf("ChannelMaster cycles: %d/100, threads: %d, resident bytes: %llu\n",
                cycle + 1, test_process_threads(), (unsigned long long)test_resident_bytes());
            fflush(stdout);
        }
    }
    destroy_impulse_cache();
    printf("Resident bytes after warmup/final: %llu/%llu (diagnostic; Linux LeakSanitizer checks leaks)\n",
        (unsigned long long)warm_bytes, (unsigned long long)test_resident_bytes());
    puts("PASS: offline 8/5/2/1 topology, 100 cycles, six rollback checkpoints, disabled devices/TX, port selection");
    return 0;
}
