/*
 * rnet.c — lifecycle extracted from netInterface.c
 * Copyright (C) 2006,2007  Bill Tracey (bill@ejwt.com) (KD5TFD)
 * Copyright (C) 2010-2020 Doug Wigley (W5WC)
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 */
#include "rnet.h"
#include <string.h>
#ifdef _WIN32
#include <malloc.h>
#endif
#ifdef THETIS_CM_HEADLESS
#define RNET_API
#else
#define RNET_API __declspec(dllexport)
#endif

RADIONET prn;
RBPFILTER prbpfilter;
RBPFILTER2 prbpfilter2;
static int lock_count;
#ifdef THETIS_TESTING
static int fail_after = -1;
void rnet_test_fail_after(int allocations) { fail_after = allocations; }
#endif

static void *allocate(size_t size, int aligned)
{
#ifdef THETIS_TESTING
    if (fail_after == 0) return NULL;
    if (fail_after > 0) --fail_after;
#endif
    void *p = aligned ? _aligned_malloc(size, 16) : malloc(size);
    if (p) memset(p, 0, size);
    return p;
}
static CRITICAL_SECTION *rnet_lock(int i)
{
    CRITICAL_SECTION *locks[] = {&prn->udpOUT, &prn->sendOUT, &prn->rcvpkt,
        &prn->sndpkt, &prn->rcvpktp1, &prn->seqErrors};
    return locks[i];
}
RNET_API void clearSnapshots(void)
{
    if (!prn) return;
    if (lock_count == 6) EnterCriticalSection(&prn->seqErrors);
    for (int i = 0; i < MAX_RX_STREAMS; ++i)
    {
        while (prn->rx[i].snapshots_head)
        {
            _seqLogSnapshot_t *next = prn->rx[i].snapshots_head->next;
            free(prn->rx[i].snapshots_head);
            prn->rx[i].snapshots_head = next;
        }
        prn->rx[i].snapshots_tail = prn->rx[i].snapshot = NULL;
        prn->rx[i].snapshot_length = 0;
    }
    if (lock_count == 6) LeaveCriticalSection(&prn->seqErrors);
}
RNET_API void destroy_rnet(void)
{
    if (!prn) return;
    clearSnapshots();
    while (lock_count) DeleteCriticalSection(rnet_lock(--lock_count));
    for (int i = 0; i < MAX_ADC; ++i) _aligned_free(prn->adc[i].wb_buff);
    if (prn->RxBuff)
        for (int i = 0; i < 8; ++i) free(prn->RxBuff[i]);
    free(prn->RxBuff);
    free(prn->RxReadBufp);
    free(prn->TxReadBufp);
    free(prn->ReadBufp);
    free(prn->OutBufp);
    free(prn->outLRbufp);
    free(prn->outIQbufp);
    _aligned_free(prbpfilter); prbpfilter = NULL;
    _aligned_free(prbpfilter2); prbpfilter2 = NULL;
    _aligned_free(prn); prn = NULL;
}
int create_rnet_checked(void)
{
    if (prn) return -2;
    prn = allocate(sizeof(*prn), 1);
    if (!prn) return -1;
    prn->RxBuff = allocate(8 * sizeof(double *), 0);
    if (!prn->RxBuff) goto failed;
    for (int i = 0; i < 8; ++i)
        if (!(prn->RxBuff[i] = allocate(64 * 2 * sizeof(double), 0))) goto failed;
    if (!(prn->RxReadBufp = allocate(240 * 2 * sizeof(double), 0)) ||
        !(prn->TxReadBufp = allocate(720 * 2 * sizeof(double), 0)) ||
        !(prn->ReadBufp = allocate(1444, 0)) ||
        !(prn->OutBufp = allocate(1440, 0)) ||
        !(prn->outLRbufp = allocate(1440 * sizeof(double), 0)) ||
        !(prn->outIQbufp = allocate(1440 * sizeof(double), 0))) goto failed;
    for (int i = 0; i < MAX_ADC; ++i)
        if (!(prn->adc[i].wb_buff = allocate(1024 * sizeof(double), 1))) goto failed;
    prbpfilter = allocate(sizeof(*prbpfilter), 1);
    prbpfilter2 = allocate(sizeof(*prbpfilter2), 1);
    if (!prbpfilter || !prbpfilter2) goto failed;
    for (; lock_count < 6; ++lock_count)
        if (!InitializeCriticalSectionAndSpinCount(rnet_lock(lock_count), lock_count == 5 ? 0 : 2500)) goto failed;

    /* Original nonzero defaults; everything else is deterministically zero. */
    prn->base_outbound_port = 1024;
    prn->p2_custom_port_base = 1025;
    prn->sendHighPriority = 1;
    prn->num_adc = prn->num_dac = 1;
    prn->cw.edge_length = 7;
    prn->mic.spp = 64;
    prn->wb_base_dispid = 32;
    prn->wb_samples_per_packet = 512;
    prn->wb_sample_size = 16;
    prn->wb_update_rate = 70;
    prn->wb_packets_per_frame = 32;
    for (int i = 0; i < MAX_ADC; ++i)
    {
        prn->adc[i].id = i;
        prn->adc[i].tx_step_attn = 31;
    }
    for (int i = 0; i < MAX_RX_STREAMS; ++i)
    {
        prn->rx[i].id = i;
        prn->rx[i].sampling_rate = 48;
        prn->rx[i].bit_depth = 24;
        prn->rx[i].spp = 238;
    }
    for (int i = 0; i < MAX_TX_STREAMS; ++i)
    {
        prn->tx[i].id = i;
        prn->tx[i].sampling_rate = 192;
        prn->tx[i].spp = 240;
    }
    for (int i = 0; i < MAX_AUDIO_STREAMS; ++i) prn->audio[i].spp = 64;
    prbpfilter->enable = 1;
    prbpfilter2->enable = 2;
    return 0;
failed:
    destroy_rnet();
    return -1;
}
