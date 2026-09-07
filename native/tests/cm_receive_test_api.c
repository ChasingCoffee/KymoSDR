/* SPDX-License-Identifier: GPL-2.0-or-later
 * Test-only white-box check of the real portable CM input ring, not a new ring.
 * Requires an offline core, with all DSP channels disabled and no producer.
 */
#include "cmcomm.h"
#define CHECK(x) do { if (!(x)) return __LINE__; } while (0)
PORT int ThetisTestCMInputRing(void)
{
    CMB a = pcm->pcbuff[0];
    double input[480], output[4096];
    stop_cmbuffs(0); flush_cmbuffs(0);
    InterlockedBitTestAndSet(&a->run, 0); // manual consumer, no worker
    InterlockedBitTestAndSet(&a->accept, 0);
    // Fill to capacity without a consumer. A rejected infusion cannot change data.
    for (int at = 0; at < a->r1_active_buffsize; at += 64)
    {
        for (int i = 0; i < 128; ++i) input[i] = at * 2 + i;
        Inbound(0, 64, input);
    }
    CHECK(a->queued_samples == a->r1_active_buffsize);
    Inbound(0, 64, input); Inbound(0, -1, input); Inbound(0, 241, input);
    CHECK(a->overruns == 3 && a->queued_samples == a->r1_active_buffsize);
    for (int at = 0; at < a->r1_active_buffsize; at += a->r1_outsize)
    {
        CHECK(WaitForSingleObject(a->Sem_BuffReady, 0) == WAIT_OBJECT_0);
        cmdata(0, output);
        for (int i = 0; i < a->r1_outsize * 2; ++i) CHECK(output[i] == at * 2 + i);
    }
    CHECK(a->queued_samples == 0 && WaitForSingleObject(a->Sem_BuffReady, 0) == WAIT_TIMEOUT);
    // Non-block-aligned packets wrap the ring and produce exact, ordered blocks.
    int consumed = 0;
    for (int block = 0; block < 100; ++block)
    {
        for (int i = 0; i < 476; ++i) input[i] = block * 476 + i;
        Inbound(0, 238, input);
        while (WaitForSingleObject(a->Sem_BuffReady, 0) == WAIT_OBJECT_0)
        {
            cmdata(0, output);
            for (int i = 0; i < a->r1_outsize * 2; ++i) CHECK(output[i] == consumed++);
        }
        CHECK(a->queued_samples >= 0 && a->queued_samples < a->r1_outsize);
    }
    CHECK(a->overruns == 3);
    InterlockedBitTestAndReset(&a->accept, 0);
    // Restart the real worker repeatedly, including stop with pending input.
    memset(input, 0, sizeof(input));
    for (int cycle = 0; cycle < 10; ++cycle)
    {
        SetCMRingOutsize(0, a->r1_outsize);
        for (int block = 0; block < 100; ++block) Inbound(0, 238, input);
        stop_cmbuffs(0);
        CHECK(a->queued_samples >= 0 && a->queued_samples <= a->r1_active_buffsize);
    }
    return 0;
}
