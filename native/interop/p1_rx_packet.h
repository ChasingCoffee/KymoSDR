/* SPDX-License-Identifier: GPL-2.0-or-later */
#pragma once
#include <stdint.h>
/* One receiver, 48 kHz. Capacity counts doubles; mic/status never enter DSP. */
int p1_rx_decode(const unsigned char *, int length, double *, int capacity, uint32_t *sequence);
void p1_rx_control(unsigned char packet[1032], uint32_t frequency, uint32_t sequence);
void p1_rx_setup(unsigned char packet[1032], uint32_t sequence);
void p1_rx_run(unsigned char packet[64], int run);
