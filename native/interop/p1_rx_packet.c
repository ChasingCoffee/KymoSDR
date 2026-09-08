/* SPDX-License-Identifier: GPL-2.0-or-later
 * Single-receiver UDP subset of networkproto1.c / pinned pihpsdr hpsdrsim.
 * Two 512-byte Ozy frames: sync(3), control(5), 63 * (I24,Q24,mic16).
 */
#include "p1_rx_packet.h"
#include <string.h>
static void put32(unsigned char *p, uint32_t v)
{ p[0] = (unsigned char)(v >> 24); p[1] = (unsigned char)(v >> 16); p[2] = (unsigned char)(v >> 8); p[3] = (unsigned char)v; }
int p1_rx_decode(const unsigned char *p, int length, double *iq, int capacity, uint32_t *sequence)
{
    if (!p || !iq || !sequence || length != 1032 || capacity < 252 ||
        p[0] != 0xef || p[1] != 0xfe || p[2] != 1 || p[3] != 6) return -1;
    for (int f = 8; f <= 520; f += 512)
        if (p[f] != 0x7f || p[f + 1] != 0x7f || p[f + 2] != 0x7f) return -1;
    *sequence = (uint32_t)p[4] << 24 | (uint32_t)p[5] << 16 | (uint32_t)p[6] << 8 | p[7];
    for (int f = 0; f < 2; ++f)
        for (int i = 0; i < 63; ++i)
            for (int q = 0; q < 2; ++q)
            {
                const unsigned char *s = p + 16 + f * 512 + i * 8 + q * 3;
                int32_t v = (int32_t)((uint32_t)s[0] << 16 | (uint32_t)s[1] << 8 | s[2]);
                if (v & 0x800000) v -= 0x1000000;
                iq[2 * (f * 63 + i) + q] = v / 8388608.0;
            }
    return 126;
}
void p1_rx_control(unsigned char p[1032], uint32_t frequency, uint32_t sequence)
{
    memset(p, 0, 1032);
    p[0] = 0xef; p[1] = 0xfe; p[2] = 1; p[3] = 2; put32(p + 4, sequence);
    for (int f = 8; f <= 520; f += 512) p[f] = p[f + 1] = p[f + 2] = 0x7f;
    // First C0..4=0: 48 kHz, one RX, MOX/relays/duplex off.
    // Second C0=4: RX0 frequency. All host audio/TX payload remains zero.
    p[523] = 4; put32(p + 524, frequency);
}
void p1_rx_run(unsigned char p[64], int run)
{ memset(p, 0, 64); p[0] = 0xef; p[1] = 0xfe; p[2] = 4; p[3] = run ? 1 : 0; }
void p1_rx_setup(unsigned char p[1032], uint32_t sequence)
{
    p1_rx_control(p, 0, sequence);
    p[523] = 28; // explicit RX ADC0 mapping; remaining ADC/TX attenuation fields zero
}
