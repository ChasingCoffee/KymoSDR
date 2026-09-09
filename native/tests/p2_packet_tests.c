/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "p2_rx_packet.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed: %s at %d\n", #x, __LINE__); exit(1); } } while (0)
static unsigned get16(const unsigned char *p) { return (unsigned)p[0] * 256 + p[1]; }
static uint32_t get32(const unsigned char *p)
{ return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3]; }
int main(void)
{
    unsigned char packet[1445] = {0}; double out[478] = {0}; uint32_t sequence = 42;
    packet[0] = 0xfe; packet[1] = 0xdc; packet[2] = 0xba; packet[3] = 0x98;
    packet[13] = 24; packet[15] = 238;
    const unsigned char samples[] = {0x80,0,0, 0x7f,0xff,0xff, 0xff,0xff,0xff, 0,0,1};
    memcpy(packet + 16, samples, sizeof(samples));
    out[0] = out[477] = 1234567;
    CHECK(p2_rx_decode(packet, 1444, out + 1, 476, &sequence) == 238);
    CHECK(sequence == 0xfedcba98u && out[0] == 1234567 && out[477] == 1234567);
    CHECK(out[1] == -1 && out[2] == 8388607.0 / 8388608 && out[3] == -1.0 / 8388608 && out[4] == 1.0 / 8388608);
    for (int i = 5; i <= 476; ++i) CHECK(out[i] == 0);
    CHECK(p2_g2_decode(packet, 1444, out + 1, 476, &sequence) == 238);
    CHECK(out[1] == -1 && out[2] == -8388607.0 / 8388608 && out[3] == -1.0 / 8388608 && out[4] == -1.0 / 8388608);
    CHECK(out[0] == 1234567 && out[477] == 1234567 && sequence == 0xfedcba98u);
    CHECK(p2_g2_decode(NULL, 1444, out, 476, &sequence) == -1);
    CHECK(p2_g2_decode(packet, 1443, out, 476, &sequence) == -1);
    CHECK(p2_g2_decode(packet, 1444, out, 475, &sequence) == -1);
    for (int size = 0; size <= 1445; ++size)
        if (size != 1444) CHECK(p2_rx_decode(packet, size, out + 1, 476, &sequence) == -1);
    CHECK(p2_rx_decode(NULL, 1444, out, 476, &sequence) == -1);
    CHECK(p2_rx_decode(packet, 1444, NULL, 476, &sequence) == -1);
    CHECK(p2_rx_decode(packet, 1444, out, 476, NULL) == -1);
    CHECK(p2_rx_decode(packet, 1444, out, 475, &sequence) == -1);
    for (int i = 12; i <= 15; ++i)
    {
        packet[i] ^= 1;
        CHECK(p2_rx_decode(packet, 1444, out + 1, 476, &sequence) == -1);
        packet[i] ^= 1;
    }
    CHECK(out[0] == 1234567 && out[477] == 1234567);
    for (int base = 1024; base <= 65515; base += 64491)
    {
        unsigned char general[61]; general[60] = 99;
        p2_rx_general(general, base);
        const int offsets[] = {1,2,3,1,4,5,11,2};
        for (int i = 0; i < 8; ++i) CHECK(get16(general + 5 + 2 * i) == (unsigned)(base + offsets[i]));
        for (int i = 0; i < 60; ++i)
            if (i < 5 || i > 20) CHECK(general[i] == (i == 37 ? 8 : i == 38 ? 1 : 0));
        CHECK(general[60] == 99);
    }
    for (int ddc = 0; ddc < 10; ++ddc)
    {
        packet[1444] = 99;
        p2_rx_receivers(packet, ddc, 384000);
        CHECK(packet[7] + 256 * packet[8] == (1 << ddc));
        CHECK(get16(packet + 18 + 6 * ddc) == 384 && packet[22 + 6 * ddc] == 24);
        for (int i = 0; i < 1444; ++i)
            if (i != 4 && i != 7 && i != 8 && i != 18 + 6 * ddc && i != 19 + 6 * ddc && i != 22 + 6 * ddc)
                CHECK(packet[i] == 0);
        for (int run = 0; run <= 1; ++run)
        {
            p2_rx_high(packet, ddc, 61440000, run, 0xffffffffu);
            CHECK(get32(packet) == 0xffffffffu && packet[4] == run);
            CHECK(get32(packet + 9 + 4 * ddc) == 0x80000000u);
            for (int i = 5; i < 1444; ++i)
                if (i != 9 + 4 * ddc) CHECK(packet[i] == 0); // all TX/CW/relay fields zero
            CHECK(packet[1444] == 99);
        }
    }
    // Hardware packet firewall: mutate EVERY non-variable bit, not just known TX bits.
    for (int offset = 0; offset <= 3; ++offset)
    {
        if (offset == 2) continue;
        int length = offset == 0 ? 60 : 1444;
        if (offset == 0) p2_g2_general(packet);
        else if (offset == 1) p2_rx_receivers(packet, 2, 192000);
        else p2_g2_high(packet, 14200000, 1, 123);
        CHECK(p2_g2_packet_allowed(offset, packet, length));
        CHECK(!p2_g2_packet_allowed(offset, packet, length-1));
        CHECK(!p2_g2_packet_allowed(offset, packet, length+1));
        for (int i = 0; i < length; ++i)
        {
            if (offset == 3 && (i < 4 || (i >= 17 && i <= 20))) continue;
            for (int bit = 0; bit < 8; ++bit)
            {
                packet[i] ^= (unsigned char)(1u << bit);
                CHECK(p2_g2_packet_allowed(offset, packet, length) == (offset == 3 && i == 4 && bit == 0));
                packet[i] ^= (unsigned char)(1u << bit);
            }
        }
    }
    CHECK(!p2_g2_packet_allowed(2, packet, 60) && !p2_g2_packet_allowed(4, packet, 260) && !p2_g2_packet_allowed(5, packet, 1444));
    CHECK(!p2_g2_packet_allowed(0, NULL, 60));
    for (int hz = 14000000; hz <= 14350000; hz += 350000)
    {
        for (int run = 0; run < 2; ++run)
        {
            p2_g2_high(packet, (uint32_t)hz, run, 0xffffffffu);
            CHECK(p2_g2_packet_allowed(3, packet, 1444));
            CHECK(packet[4] == run && packet[5] == 0 && get32(packet+329) == 0 && packet[345] == 0);
            CHECK(get32(packet+1432) == 0x01100002u && packet[1400] == 2);
        }
    }
    p2_g2_high(packet, 13999999, 1, 0); CHECK(!p2_g2_packet_allowed(3, packet, 1444));
    p2_g2_high(packet, 14350001, 1, 0); CHECK(!p2_g2_packet_allowed(3, packet, 1444));
    puts("PASS: signed24, simulator codecs and G2 ANT1 RX packet firewall, exhaustive fixed-bit mutations");
    return 0;
}
