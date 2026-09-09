/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_G2_RECEIVE_POLICY_H
#define THETIS_G2_RECEIVE_POLICY_H
#include <stdint.h>
/* Extended duration is selected only by a separate opt-in opener. Never renew
 * a running deadline; watchdog, packet firewall and key trips are unchanged. */
static inline int g2_duration_valid(int seconds, int extended)
{ return seconds >= 1 && seconds <= (extended ? 3600 : 60); }
static inline int g2_deadline_reached(uint64_t start, uint64_t now, int seconds)
{ return now - start >= (uint64_t)seconds * 1000; }
#endif
