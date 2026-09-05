/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_CM_TRANSPORT_H
#define THETIS_CM_TRANSPORT_H
#include "cm_session.h"
/* Loopback lifecycle probe, NOT a radio streaming API. Uses the real seven-
 * argument socket initializer, but no packet parser, encoder or radio sender.
 * Checkpoints: 1 RNet, 2 bound socket, 3 stop event, 4 reader, 5 timer worker. */
CM_API int ThetisTransportOpen(int abi, const char *remote, int remote_port,
    const char *local, int local_port, int protocol, int model, int relocate,
    cm_checkpoint checkpoint, void *context);
CM_API int ThetisTransportClose(void);
/* 16 int32: ABI, open, bound local port, remote port, P2 base, protocol, model,
 * RNet owned, workers owned, datagrams, bytes, oversize, socket errors,
 * timer ticks, loopback-only (1), sent datagrams (always 0). */
CM_API int ThetisTransportGetState(int32_t *values, int capacity);
#ifdef THETIS_TESTING
/* Fail BEFORE creating event (3), reader (4), or timer (5). Zero resets. */
CM_API int ThetisTransportTestFault(int stage);
#endif
#endif
