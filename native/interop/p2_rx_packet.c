/* SPDX-License-Identifier: GPL-2.0-or-later
 * Validated single-DDC subset of the inherited P2 receive conversion and
 * control layout. DSP, routing and filtering algorithms are not duplicated.
 */
#include "p2_rx_packet.h"
#include <string.h>
static void put16(unsigned char *p, int value) { p[0] = (unsigned char)(value >> 8); p[1] = (unsigned char)value; }
static void put32(unsigned char *p, uint32_t v)
{ p[0] = (unsigned char)(v >> 24); p[1] = (unsigned char)(v >> 16); p[2] = (unsigned char)(v >> 8); p[3] = (unsigned char)v; }
int p2_rx_decode(const unsigned char *p, int length, double *iq, int capacity, uint32_t *sequence)
{
    if (!p || !iq || !sequence || length != 1444 || capacity < 476 ||
        p[12] != 0 || p[13] != 24 || p[14] != 0 || p[15] != 238) return -1;
    *sequence = (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3];
    for (int i = 0; i < 476; ++i)
    {
        const unsigned char *s = p + 16 + 3 * i;
        int32_t sample = (int32_t)((uint32_t)s[0] << 16 | (uint32_t)s[1] << 8 | s[2]);
        if (sample & 0x800000) sample -= 0x1000000;
        iq[i] = sample / 8388608.0; // no signed-left-shift undefined behavior
    }
    return 238;
}
void p2_rx_general(unsigned char p[60], int base)
{
    static const int offsets[] = {1, 2, 3, 1, 4, 5, 11, 2};
    memset(p, 0, 60);
    for (int i = 0; i < 8; ++i) put16(p + 5 + 2 * i, base + offsets[i]);
    p[37] = 8; p[38] = 1; // phase words, watchdog; PA and wideband remain disabled
}
void p2_rx_receivers(unsigned char p[1444], int ddc, int rate)
{
    memset(p, 0, 1444); p[4] = 2;
    p[7 + ddc / 8] = (unsigned char)(1u << (ddc % 8)); // low enable byte first
    put16(p + 18 + 6 * ddc, rate / 1000);
    p[22 + 6 * ddc] = 24; // ADC0, 24-bit; no sync, TX or PureSignal
}
void p2_rx_high(unsigned char p[1444], int ddc, uint32_t frequency, int run, uint32_t sequence)
{
    memset(p, 0, 1444); put32(p, sequence);
    p[4] = run ? 1 : 0; // RUN only; PTT/CWX/drive/DUC/relays all zero
    uint32_t phase = (uint32_t)(frequency * (4294967296.0 / 122880000.0));
    put32(p + 9 + 4 * ddc, phase);
}
