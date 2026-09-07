/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_CM_RECEIVE_CORE_H
#define THETIS_CM_RECEIVE_CORE_H
/* Private headless native tap, never a retained managed callback. */
typedef void (*cm_audio_observer)(int count, const double *samples, int error);
typedef void (*cm_iq_observer)(int count, const double *samples);
int cm_receive_core_open(int rate, cm_audio_observer observer, cm_iq_observer spectrum);
int cm_receive_core_close(void);
void cm_observe_rx(int stream, int count, const double *samples, int error);
void cm_observe_iq(int stream, int count, const double *samples);
#endif
