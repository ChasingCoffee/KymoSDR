/* SPDX-License-Identifier: GPL-2.0-or-later
 * A synchronous adapter to WDSP's existing analyzer kernels. No second FFT
 * implementation, dispatcher or detached thread-pool work. The receive owner
 * serializes calls and joins the CM worker before DestroyAnalyzer runs.
 */
#include "comm.h"
#include "cm_spectrum.h"

extern void SetAnalyzer(int, int, int, int, int *, int, int, int, double, int, int,
    double, double, int, int, int, double, double, int);
extern void SetDisplaySampleRate(int, int);
extern void SetDisplayDetectorMode(int, int, int);
extern void SetDisplayAverageMode(int, int, int);
extern DWORD WINAPI Cspectra(void *);

void cm_spectrum_configure(int rate)
{
    int flip = 0;
    SetDisplaySampleRate(0, rate);
    /* N-1 pixels selects WDSP's interpolation branch with bin_per_pix == 1:
     * exact FFT bin centers. Celiminate omits the negative Nyquist bin.
     * Hann, no averaging, no clipping, neutral calibration, unit-complex dB. */
    SetAnalyzer(0, 1, 1, 1, &flip, CM_SPECTRUM_FFT_SIZE, CM_SPECTRUM_FFT_SIZE,
        2, 14.0, 0, 0, 0, 0, CM_SPECTRUM_PIXELS, 1, 0, 0, 0, CM_SPECTRUM_FFT_SIZE);
    SetDisplayDetectorMode(0, 0, 0);
    SetDisplayAverageMode(0, 0, 0);
}

int cm_spectrum_transform(const double *iq, float *pixels)
{
    DP a = pdisp[0];
    EnterCriticalSection(&a->SetAnalyzerSection);
    if (a->size != CM_SPECTRUM_FFT_SIZE || a->num_pixels != CM_SPECTRUM_PIXELS ||
        a->num_fft != 1 || a->num_stitch != 1 || a->type != 1 || a->stop ||
        InterlockedAnd(&a->dispatcher, 1) || InterlockedAnd(a->pnum_threads, -1))
    { LeaveCriticalSection(&a->SetAnalyzerSection); return -1; }
    for (int i = 0; i < CM_SPECTRUM_FFT_SIZE; ++i)
    {
        // Native P2/CM is I,Q; legacy Spectrum0's public input is Q,I.
        a->I_samples[0][0][i] = (dINREAL)iq[2 * i];
        a->Q_samples[0][0][i] = (dINREAL)iq[2 * i + 1];
    }
    a->IQO_idx[0][0] = 0;
    InterlockedIncrement(a->pnum_threads);
    int completed = (int)Cspectra(NULL); // encoded display/span/LO are all zero
    int ready = 0; double reference;
    GetPixels(0, 0, pixels, &ready, &reference);
    LeaveCriticalSection(&a->SetAnalyzerSection);
    return completed && ready ? 0 : -1;
}
