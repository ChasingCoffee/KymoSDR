/* SPDX-License-Identifier: GPL-2.0-or-later
 * Test-only white-box check of the real portable CM input ring, not a new ring.
 * Requires an offline core, with all DSP channels disabled and no producer.
 */
#include "cmcomm.h"
#include "cm_spectrum.h"
#include "cm_p2_receive.h"
#define CHECK(x) do { if (!(x)) return __LINE__; } while (0)
PORT int ThetisTestCMGainHistory(void)
{
    // Only this test thread issues controls. Freeze the producer/consumer and DSP
    // under their recursive locks so byte-for-byte history checks cannot race it.
    EnterCriticalSection(&pcm->update[0]);
    EnterCriticalSection(&ch[0].csDSP);
    WCPAGC a = rxa[0].agc.p;
    unsigned char before[sizeof(wcpagc)]; memcpy(before,a,sizeof(before));
    int ok = a->volts > 0 && a->mode == 4 &&
        ThetisReceiveSetGain(1,-20,0,4,60) == 0 && memcmp(before,a,sizeof(before)) == 0 &&
        ThetisReceiveSetGain(1,0,1,4,60) == 0 && memcmp(before,a,sizeof(before)) == 0;
    LeaveCriticalSection(&ch[0].csDSP);
    LeaveCriticalSection(&pcm->update[0]);
    CHECK(ok); return 0;
}
PORT int ThetisTestCMGain(int db, int muted, int mode, int top)
{
    EnterCriticalSection(&ch[0].csDSP);
    WCPAGC a = rxa[0].agc.p;
    int ok = a->run == 1 && a->mode == mode && a->fixed_gain == 1 && a->var_gain == 1 &&
        fabs(a->max_gain - pow(10.0, top / 20.0)) < 1e-8 &&
        fabs(rxa[0].panel.p->gain1 - (muted ? 0 : pow(10.0, db / 20.0))) < 1e-12 &&
        fabs(a->tau_attack - .001) < 1e-12 &&
        fabs(a->tau_decay - (mode == 2 ? .5 : mode == 4 ? .05 : .25)) < 1e-12 &&
        fabs(a->hangtime - (mode == 2 ? 1 : 0)) < 1e-12 &&
        fabs(a->hang_thresh - (mode == 2 ? .25 : 1)) < 1e-12;
    LeaveCriticalSection(&ch[0].csDSP);
    CHECK(ok); return 0;
}
PORT int ThetisTestCMSpectrum(void)
{
    double iq[2 * CM_SPECTRUM_FFT_SIZE];
    float pixels[CM_SPECTRUM_PIXELS + 2];
    const int bins[] = {85, -85, 0, -2047, 2047};
    cm_spectrum_configure(192000);
    pixels[0] = pixels[CM_SPECTRUM_PIXELS + 1] = 12345;
    for (int test = 0; test < 5; ++test)
    {
        for (int i = 0; i < CM_SPECTRUM_FFT_SIZE; ++i)
        {
            double phase = 6.283185307179586 * bins[test] * i / CM_SPECTRUM_FFT_SIZE;
            iq[2 * i] = 0.125 * cos(phase); iq[2 * i + 1] = 0.125 * sin(phase);
        }
        CHECK(cm_spectrum_transform(iq, pixels + 1) == 0);
        CHECK(pixels[0] == 12345 && pixels[CM_SPECTRUM_PIXELS + 1] == 12345);
        int peak = 0;
        for (int i = 0; i < CM_SPECTRUM_PIXELS; ++i)
        {
            CHECK(isfinite(pixels[1 + i]));
            if (pixels[1 + i] > pixels[1 + peak]) peak = i;
        }
        CHECK(peak == CM_SPECTRUM_FFT_SIZE / 2 - 1 + bins[test]);
        CHECK(fabs(pixels[1 + peak] - 20 * log10(0.125)) < 0.01);
        CHECK(pdisp[0]->dispatcher == 0 && *pdisp[0]->pnum_threads == 0);
    }
    memset(iq, 0, sizeof(iq));
    CHECK(cm_spectrum_transform(iq, pixels + 1) == 0);
    for (int i = 1; i <= CM_SPECTRUM_PIXELS; ++i) CHECK(isfinite(pixels[i]) && pixels[i] < -100);
    return 0;
}
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
