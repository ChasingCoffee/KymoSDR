/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "p1_rx_packet.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "P1 codec failure line %d\n", __LINE__); exit(1); } } while (0)
int main(void)
{
    unsigned char p[1033]; double iq[253]; uint32_t sequence = 123;
    p1_rx_control(p, 0x12345678, 0xfffffffeu);
    CHECK(!memcmp(p, "\xef\xfe\x01\x02\xff\xff\xff\xfe", 8));
    CHECK(!memcmp(p + 523, "\x04\x12\x34\x56\x78", 5));
    for (int f = 8; f <= 520; f += 512)
    {
        CHECK(!memcmp(p + f, "\x7f\x7f\x7f", 3));
        if (f == 8) for (int i = 3; i < 8; ++i) CHECK(p[f + i] == 0);
        for (int i = 8; i < 512; ++i) CHECK(p[f + i] == 0); // no host audio or TX IQ
    }
    p[3] = 6;
    for (int f = 0; f < 2; ++f)
        for (int i = 0; i < 63; ++i)
            memcpy(p + 16 + f * 512 + i * 8, "\x80\x00\x00\x7f\xff\xff\x7f\xff", 8);
    iq[252] = 456;
    CHECK(p1_rx_decode(p, 1032, iq, 252, &sequence) == 126 && sequence == 0xfffffffeu && iq[252] == 456);
    for (int i = 0; i < 126; ++i) CHECK(iq[2*i] == -1 && iq[2*i+1] == 8388607.0/8388608.0);
    CHECK(p1_rx_decode(p, 1031, iq, 252, &sequence) == -1);
    CHECK(p1_rx_decode(p, 1033, iq, 252, &sequence) == -1);
    CHECK(p1_rx_decode(p, 1032, iq, 251, &sequence) == -1);
    CHECK(p1_rx_decode(NULL, 1032, iq, 252, &sequence) == -1);
    CHECK(p1_rx_decode(p, 1032, NULL, 252, &sequence) == -1);
    CHECK(p1_rx_decode(p, 1032, iq, 252, NULL) == -1);
    const int corrupt[] = {0,1,2,3,8,9,10,520,521,522};
    for (int i = 0; i < 10; ++i)
    {
        int j = corrupt[i]; unsigned char saved = p[j]; p[j] ^= 1;
        sequence = 123; iq[0] = 456;
        CHECK(p1_rx_decode(p, 1032, iq, 252, &sequence) == -1 && sequence == 123 && iq[0] == 456);
        p[j] = saved;
    }
    for (int run = 0; run <= 1; ++run)
    {
        p1_rx_run(p, run); CHECK(p[0] == 0xef && p[1] == 0xfe && p[2] == 4 && p[3] == run);
        for (int i = 4; i < 64; ++i) CHECK(p[i] == 0);
    }
    p1_rx_setup(p, 1); CHECK(p[523] == 28);
    for (int i = 524; i < 1032; ++i) CHECK(p[i] == 0);
    puts("PASS: P1 golden framing, signed extrema, mic exclusion, both-frame validation, bounds, RX-only controls");
    return 0;
}
