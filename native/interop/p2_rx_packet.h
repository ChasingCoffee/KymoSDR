/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_P2_RX_PACKET_H
#define THETIS_P2_RX_PACKET_H
#include <stdint.h>
/* Isolated receive subset, following inherited network.c's wire order/scaling.
 * No TX encoder or caller-supplied raw command is exposed. */
int p2_rx_decode(const unsigned char *packet, int length, double *iq, int capacity, uint32_t *sequence);
void p2_rx_general(unsigned char packet[60], int base);
void p2_rx_receivers(unsigned char packet[1444], int ddc, int rate);
void p2_rx_high(unsigned char packet[1444], int ddc, uint32_t frequency, int run, uint32_t sequence);
#endif
