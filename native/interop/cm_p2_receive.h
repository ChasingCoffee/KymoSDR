/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_CM_P2_RECEIVE_H
#define THETIS_CM_P2_RECEIVE_H
#include "cm_session.h"
/* Single-DDC loopback RX -> CM RX0 -> WDSP USB audio tap. No TX/audio device.
 * Checkpoints: 1 CM core, 2 RNet, 3 socket, 4 stop event, 5 receive worker.
 * Close joins the producer before CM consumers. Calls reject reentrancy. */
CM_API int ThetisP2ReceiveOpen(int abi, const char *remote, int base, int ddc, int rate,
    int frequency, cm_checkpoint checkpoint, void *context);
CM_API int ThetisP2ReceiveClose(void);
CM_API int ThetisP2ReceiveTune(int frequency);
/* 24 int64: ABI, open, local port, base, DDC, rate, socket workers,
 * IQ packets, IQ samples, missing, late/duplicate, malformed, foreign,
 * mic discarded, status, socket errors, commands sent, CM overruns,
 * audio queued, audio dropped, audio produced, DSP errors, loopback-only, TX allowed. */
CM_API int ThetisP2ReceiveGetState(int64_t *values, int capacity);
/* Pull interleaved L/R from the post-WDSP RX0/sub0 tap; no managed callback. */
CM_API int ThetisP2ReceiveReadAudio(double *samples, int capacity_frames);
#ifdef THETIS_TESTING
CM_API int ThetisP2ReceiveTestFault(int stage);
#endif
#endif
