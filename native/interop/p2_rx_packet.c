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
int p2_g2_decode(const unsigned char *p, int length, double *iq, int capacity, uint32_t *sequence)
{
    int samples = p2_rx_decode(p, length, iq, capacity, sequence);
    // Legacy Thetis feeds this wire order to positive-edge WDSP USB filters.
    // Our simulator/FFT contract instead uses exp(+j*w*t) above the VFO and
    // translates those RF edges to WDSP. Conjugate only physical G2 ingress,
    // keeping tuning, both sidebands and spectrum in one common convention.
    for (int i = 0; i < samples; ++i) iq[2 * i + 1] = -iq[2 * i + 1];
    return samples;
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
int p2_g2_frequency_valid(int frequency) { return frequency >= 14000000 && frequency <= 14350000; }
void p2_g2_general(unsigned char p[60])
{
    p2_rx_general(p, 1024);
    p[59] = 3; // Alex0/1 enabled; PA remains disabled at byte 58.
}
void p2_g2_high(unsigned char p[1444], uint32_t frequency, int run, uint32_t sequence)
{
    p2_rx_high(p, 2, frequency, run, sequence);
    p[1400] = 2; // mute radio speaker; no transverter or ATU tune
    p[1431] = 2; // RX2 20/15m BPF (unused ADC1)
    p[1432] = 1; // shared antenna selector ANT1, TX relay bit 3 stays clear
    p[1433] = 0x10; // 30/20m LPF; no PureSignal
    p[1435] = 2; // RX1 20/15m BPF, normal ANT input (not EXT/XVTR)
    p[1442] = 31; p[1443] = 10; // ADC1/ADC0 RX attenuation, dB
}
int p2_g2_packet_allowed(int offset, const unsigned char *p, int length)
{
    if (!p) return 0;
    /* Exact allowlist independent of mutable legacy radio/TX state.
     * No TX-specific, audio, DUC IQ, discovery or programming command. */
    if (offset == 0 && length == 60)
    {
        const unsigned char ports[] = {4,1,4,2,4,3,4,1,4,4,4,5,4,11,4,2};
        for (int i = 0; i < 60; ++i)
        {
            int expected = i >= 5 && i <= 20 ? ports[i-5] : i == 37 ? 8 : i == 38 ? 1 : i == 59 ? 3 : 0;
            if (p[i] != expected) return 0;
        }
        return 1;
    }
    if (offset == 1 && length == 1444)
    {
        for (int i = 0; i < 1444; ++i)
            if (p[i] != (i == 4 ? 2 : i == 7 ? 4 : i == 31 ? 192 : i == 34 ? 24 : 0)) return 0;
        return 1;
    }
    if (offset != 3 || length != 1444 || p[4] > 1) return 0;
    uint32_t phase = (uint32_t)p[17] << 24 | (uint32_t)p[18] << 16 | (uint32_t)p[19] << 8 | p[20];
    if (phase < (uint32_t)(14000000.0 * (4294967296.0 / 122880000.0)) ||
        phase > (uint32_t)(14350000.0 * (4294967296.0 / 122880000.0))) return 0;
    for (int i = 5; i < 1444; ++i)
    {
        if (i >= 17 && i <= 20) continue;
        int expected = i == 1400 || i == 1431 || i == 1435 ? 2 : i == 1432 ? 1 :
            i == 1433 ? 16 : i == 1442 ? 31 : i == 1443 ? 10 : 0;
        if (p[i] != expected) return 0;
    }
    return 1;
}
