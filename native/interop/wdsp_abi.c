/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "comm.h"

/* Versioned fixed-width record: ABI revision, pointer/LONG/DWORD/double sizes,
 * complex-double bytes, analyzer input/output element bytes, RNNoise frame size.
 * No private compiler-dependent DSP structs cross the managed boundary. */
PORT int ThetisWdspGetAbiInfo(int32_t *values, int capacity) {
    if (!values || capacity < 9) return -1;
    const int32_t info[] = {1, sizeof(void *), sizeof(LONG), sizeof(DWORD),
        sizeof(double), sizeof(complex), sizeof(dINREAL), sizeof(dOUTREAL), rnnoise_get_frame_size()};
    memcpy(values, info, sizeof(info));
    return 9;
}

PORT void ThetisWdspSetPlanningTimeLimit(double seconds) {
    // Caller serializes planning/configuration; -1 restores FFTW's unlimited default.
    fftw_set_timelimit(seconds);
    fftwf_set_timelimit(seconds);
}
