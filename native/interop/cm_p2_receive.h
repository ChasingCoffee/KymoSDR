/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_CM_P2_RECEIVE_H
#define THETIS_CM_P2_RECEIVE_H
#include "cm_session.h"
/* Shared P1/P2 owner; old P2-prefixed pull/control ABI operates on either.
 * Protocol 1 is restricted to one receiver (DDC0), 48000 Hz, loopback UDP. */
CM_API int ThetisReceiveProtocolAbi(void);
CM_API int ThetisReceiveOpenWithControls(int abi, int protocol, const char *remote, int base, int ddc, int rate,
    int frequency, int mode, int low, int high, cm_checkpoint checkpoint, void *context);
/* Single-DDC loopback RX -> CM RX0 -> WDSP SSB audio tap. No TX/audio device.
 * Checkpoints: 1 CM core, 2 RNet, 3 socket, 4 stop event, 5 receive worker.
 * Close joins the producer before CM consumers. Calls reject reentrancy. */
CM_API int ThetisP2ReceiveOpen(int abi, const char *remote, int base, int ddc, int rate,
    int frequency, cm_checkpoint checkpoint, void *context);
CM_API int ThetisP2ReceiveClose(void);
CM_API int ThetisP2ReceiveTune(int frequency);
/* Controls ABI 1: mode 0=LSB, 1=USB. Audio-frequency edges are positive:
 * 0 <= low < high <= 12000 Hz, width >=100 Hz. USB -> [low,high];
 * LSB -> [-high,-low] in the RF/spectrum convention. The bridge translates
 * to WDSP's opposite FIR frequency convention internally.
 * No AGC/gain/TX/radio-routing controls are exposed.
 * The old open entry point retains USB 300..3000 defaults. */
CM_API int ThetisP2ReceiveControlsAbi(void);
CM_API int ThetisP2ReceiveOpenWithControls(int abi, const char *remote, int base, int ddc, int rate,
    int frequency, int mode, int low, int high, cm_checkpoint checkpoint, void *context);
CM_API int ThetisP2ReceiveSetControls(int abi, int mode, int low, int high);
/* 8 int64: ABI, open, mode, low, high, signed low, signed high, generation.
 * Generation starts at 1; identical updates are no-ops, rejected updates do not
 * change state. It is NOT a sample-accurate audio transition marker. Updates
 * discard queued tap audio; WDSP history still needs settling. Pre-demodulation
 * spectrum and its tuning generation are not changed by these controls. */
CM_API int ThetisP2ReceiveGetControls(int64_t *values, int capacity);
/* 24 int64: ABI, open, local port, base, DDC, rate, socket workers,
 * IQ packets, IQ samples, missing, late/duplicate, malformed, foreign,
 * mic discarded, status, socket errors, commands sent, CM overruns,
 * audio queued, audio dropped, audio produced, DSP errors, loopback-only, TX allowed. */
CM_API int ThetisP2ReceiveGetState(int64_t *values, int capacity);
/* Pull interleaved L/R from the post-WDSP RX0/sub0 tap; no managed callback. */
CM_API int ThetisP2ReceiveReadAudio(double *samples, int capacity_frames);
/* Separate spectrum ABI 1 leaves the existing receive state ABI unchanged.
 * Pull latest unread frame: 4095 floats, or 0 without modifying either buffer.
 * Hann FFT4096, no averaging, uncalibrated dB relative to unit complex amplitude
 * (NOT dBm or dB/Hz). Pixel p: requested_center - rate/2 + (p+1)*rate/4096.
 * Metadata: ABI, sequence, tuning generation, host monotonic publish ms,
 * requested center Hz, DDC, sample rate, FFT size, pixel count, coalesced frames,
 * cumulative missing packets, cumulative CM input overruns. Counters are per
 * session, not per tune. Tune discards any pending/partial frame. No hardware
 * tuning acknowledgement or sample timestamp is implied by this metadata. */
CM_API int ThetisP2ReceiveSpectrumAbi(void);
CM_API int ThetisP2ReceiveReadSpectrum(int abi, float *pixels, int capacity, int64_t *metadata, int metadata_capacity);
#ifdef THETIS_TESTING
CM_API int ThetisP2ReceiveTestFault(int stage);
#endif
#endif
