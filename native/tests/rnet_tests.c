/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "rnet.h"
#include "radio_init.h"
#include <stdio.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed: %s at %d\n", #x, __LINE__); exit(1); } } while (0)
int main(void)
{
    for (int allocation = 0; allocation < 21; ++allocation)
    {
        rnet_test_fail_after(allocation);
        CHECK(create_rnet_checked() == -1);
        CHECK(!prn && !prbpfilter && !prbpfilter2);
        destroy_rnet();
    }
    rnet_test_fail_after(-1);
    for (int cycle = 0; cycle < 100; ++cycle)
    {
        CHECK(create_rnet_checked() == 0);
        CHECK(create_rnet_checked() == -2);
        CHECK(prn->base_outbound_port == 1024 && prn->p2_custom_port_base == 1025);
        CHECK(prn->num_adc == 1 && prn->num_dac == 1 && !prn->run);
        CHECK(prn->cw.edge_length == 7 && prn->mic.spp == 64);
        CHECK(prn->wb_base_dispid == 32 && prn->wb_samples_per_packet == 512 &&
            prn->wb_sample_size == 16 && prn->wb_update_rate == 70 && prn->wb_packets_per_frame == 32);
        CHECK(prbpfilter->enable == 1 && prbpfilter2->enable == 2);
        for (int i = 0; i < 8; ++i) CHECK(prn->RxBuff[i] && prn->RxBuff[i][127] == 0);
        for (int i = 0; i < MAX_ADC; ++i) CHECK(prn->adc[i].id == i && prn->adc[i].tx_step_attn == 31 && prn->adc[i].wb_buff[1023] == 0);
        for (int i = 0; i < MAX_RX_STREAMS; ++i)
        {
            CHECK(prn->rx[i].id == i && prn->rx[i].sampling_rate == 48 && prn->rx[i].bit_depth == 24 && prn->rx[i].spp == 238);
            _seqLogSnapshot_t *node = calloc(1, sizeof(*node));
            CHECK(node);
            prn->rx[i].snapshots_head = prn->rx[i].snapshots_tail = prn->rx[i].snapshot = node;
            prn->rx[i].snapshot_length = 1;
        }
        clearSnapshots(); clearSnapshots();
        for (int i = 0; i < MAX_RX_STREAMS; ++i)
            CHECK(!prn->rx[i].snapshot && !prn->rx[i].snapshots_tail && !prn->rx[i].snapshot_length);
        CHECK(nativeInitMetis("192.0.2.1", 1024, "127.0.0.1", 0, 1, 11, 0) == -1);
        CHECK(nativeInitMetis("127.0.0.1", 1024, "0.0.0.0", 0, 1, 11, 0) == -1);
        CHECK(nativeInitMetis("127.0.0.1", 1024, "127.0.0.1", 0, 1, 17, 0) == -1);
        CHECK(listenSock == CM_INVALID_SOCKET && radio_local_port == 0);
        int port = cycle % 2 ? 5000 : 1024;
        int relocate = (cycle / 2) % 2;
        CHECK(nativeInitMetis("127.0.0.1", port, "127.0.0.1", 0, 1, 11, relocate) == 0);
        CHECK(radio_local_port > 0 && RadioProtocol == ETH && HPSDRModel == HPSDRModel_ANAN_G2);
        CHECK(prn->base_outbound_port == port && prn->p2_custom_port_base == (relocate ? port + 1 : 1025));
        CHECK(nativeInitMetis("127.0.0.1", 1024, "127.0.0.1", 0, 1, 11, 0) == -2);
        DeInitMetisSockets(); DeInitMetisSockets();
        CHECK(nativeInitMetis("127.0.0.1", 1024, "127.0.0.1", 0, 0, 14, 0) == 0);
        CHECK(RadioProtocol == USB && HPSDRModel == HPSDRModel_HERMESLITE);
        DeInitMetisSockets();
        destroy_rnet(); destroy_rnet();
        CHECK(!prn && !prbpfilter && !prbpfilter2);
    }
    puts("PASS: 21 allocation failure boundaries; RNet defaults/snapshots; 100 seven-argument socket initializer cycles, P1/P2 models and P2 ports; loopback restriction");
    return 0;
}
