/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_P2_RX_PACKET_H
#define THETIS_P2_RX_PACKET_H
#include <stdint.h>
/* Deliberately narrow G2 ANT1/ADC0/DDC2, 192 kHz, 20m RX profile.
 * These are not arbitrary radio-packet APIs. Validator is also used at send. */
int p2_g2_frequency_valid(int frequency);
void p2_g2_general(unsigned char p[60]);
void p2_g2_high(unsigned char p[1444], uint32_t frequency, int run, uint32_t sequence);
int p2_g2_packet_allowed(int offset, const unsigned char *p, int length);
/* Isolated receive subset, following inherited network.c's wire order/scaling.
 * No TX encoder or caller-supplied raw command is exposed. */
int p2_rx_decode(const unsigned char *packet, int length, double *iq, int capacity, uint32_t *sequence);
/* Saturn wire orientation is conjugated relative to the simulator/FFT RF
 * convention. Normalize exactly once, before both spectrum and WDSP. */
int p2_g2_decode(const unsigned char *packet, int length, double *iq, int capacity, uint32_t *sequence);
void p2_rx_general(unsigned char packet[60], int base);
void p2_rx_receivers(unsigned char packet[1444], int ddc, int rate);
void p2_rx_high(unsigned char packet[1444], int ddc, uint32_t frequency, int run, uint32_t sequence);
#endif
