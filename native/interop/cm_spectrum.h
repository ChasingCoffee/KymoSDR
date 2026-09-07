/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_CM_SPECTRUM_H
#define THETIS_CM_SPECTRUM_H
#define CM_SPECTRUM_FFT_SIZE 4096
#define CM_SPECTRUM_PIXELS 4095
/* Private, RX0 analyzer owned by the receive core. Configure before input;
 * transform only on its joined CM worker, with complete interleaved I,Q FFTs.
 * Never mix this synchronous path with legacy Spectrum calls. */
void cm_spectrum_configure(int rate);
int cm_spectrum_transform(const double *iq, float *pixels);
#endif
