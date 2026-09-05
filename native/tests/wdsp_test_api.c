/* SPDX-License-Identifier: GPL-2.0-or-later
 * Built only with BUILD_TESTING; exercises the existing NR3/NR4 adapters.
 */
#include "comm.h"

extern void SetAnalyzer(int, int, int, int, int *, int, int, int, double,
    int, int, double, double, int, int, int, double, double, int);
extern void RNNRloadModel(const char *);

PORT int ThetisWdspTestAnalyzer(void) {
    const int size = 2048, count = 1024;
    int success, flip = 0, ready = 0, failed = 0;
    double references[3] = {-12345, 0, -67890};
    double *input = malloc0(size * sizeof(complex));
    float *pixels = malloc0((count+2) * sizeof(float));
    pixels[0] = -12345; pixels[count+1] = -67890;
    XCreateAnalyzer(0, &success, size, 1, 1, "");
    if (success) return 1;
    SetAnalyzer(0, 1, 1, 1, &flip, size, size, 0, 14, 0, 0, 0, 0, count, 1, 0, 0, 0, size);
    SetPixelRef(0, 14.2);
    for (int i = 0; i < size; ++i) {
        double phase = TWOPI * 1500 * i / 48000;
        input[2*i] = 0.1 * sin(phase); input[2*i+1] = 0.1 * cos(phase); // Q/I
    }
    Spectrum0(1, 0, 0, 0, input);
    for (int wait = 0; wait < 5000 && !ready; ++wait) {
        GetPixels(0, 0, pixels+1, &ready, references+1);
        if (!ready) Sleep(1);
    }
    if (!ready || references[1] != 14.2 || references[0] != -12345 || references[2] != -67890 ||
        pixels[0] != -12345 || pixels[count+1] != -67890) failed = 1;
    for (int i = 1; i <= count; ++i) if (!isfinite(pixels[i])) failed = 1;
    DestroyAnalyzer(0); _aligned_free(pixels); _aligned_free(input);
    return failed;
}

static void sync_callback(void) {}
PORT int ThetisWdspTestLifecycle(void) {
    const int size = 512;
    double *input = malloc0(size * sizeof(complex));
    double *output = malloc0(size * sizeof(complex));
    int failed = 0;
    for (int cycle = 0; cycle < 20; ++cycle) {
        OpenChannel(0, size, size, 48000, 48000, 48000, 0, 1, 0, 0.005, 0, 0.005, 1);
        for (int block = 0; block < 10; ++block) {
            for (int i = 0; i < size; ++i) {
                double phase = TWOPI * 1500 * (block*size+i) / 48000;
                input[2*i] = 0.1 * cos(phase); input[2*i+1] = 0.1 * sin(phase);
            }
            int error;
            fexchange0(0, input, output, &error);
            if (error) failed = 1;
            for (int i = 0; i < size*2; ++i) if (!isfinite(output[i])) failed = 1;
        }
        CloseChannel(0);
        SYNCB sync = create_syncbuffs(1, 1, size, size, size, &output, sync_callback);
        Syncbound(sync, size, &input);
        SetSYNCBRingOutsize(sync, size/2);
        destroy_syncbuffs(sync);
    }
    _aligned_free(output); _aligned_free(input);
    return failed;
}

PORT int ThetisWdspTestNoiseLifecycle(void) {
    enum { size = 480, instances = 9 };
    double *input = malloc0(size * sizeof(complex));
    double *output = malloc0(size * sizeof(complex));
    int failed = 0;
    for (int cycle = 0; cycle < 3; ++cycle) {
        RNNR receivers[instances];
        // Exercise empty -> 4 -> 8 -> 16 registry capacity, then return to empty.
        for (int i = 0; i < instances; ++i) {
            receivers[i] = create_rnnr(1, 0, size, input, output, 48000);
            if (!receivers[i]->st) failed = 1;
        }
        RNNRloadModel(NULL); // Touch every retained entry after both copies.
        for (int i = 0; i < instances; ++i) {
            if (!receivers[i]->st || receivers[i]->run != 1) failed = 1;
            xrnnr(receivers[i], 0);
            for (int j = 0; j < size*2; ++j) if (!isfinite(output[j])) failed = 1;
        }
        // Non-LIFO removal must leave only live entries for the next reload.
        for (int i = 0; i < instances; i += 2) destroy_rnnr(receivers[i]);
        RNNRloadModel(NULL);
        for (int i = 1; i < instances; i += 2) {
            if (!receivers[i]->st || receivers[i]->run != 1) failed = 1;
            destroy_rnnr(receivers[i]);
        }
    }
    _aligned_free(output); _aligned_free(input);
    return failed;
}

PORT int ThetisWdspTestNoise(void) {
    const int size = 480;
    double *input = malloc0(size * sizeof(complex));
    double *output = malloc0(size * sizeof(complex));
    RNNR nr3 = create_rnnr(1, 0, size, input, output, 48000);
    SBNR nr4 = create_sbnr(1, 0, size, input, output, 48000);
    uint32_t seed = 0x12345678;
    double energies[2] = {0, 0};
    int failed = 0;
    for (int kind = 0; kind < 2; ++kind) {
        for (int block = 0; block < 40; ++block) {
            for (int i = 0; i < size; ++i) {
                seed = seed * UINT32_C(1664525) + UINT32_C(1013904223);
                double noise = ((double)(seed >> 8) / 16777216.0 - 0.5) * 0.02;
                input[2*i] = input[2*i+1] = 0.1 * sin(TWOPI * 1000 * (block*size+i) / 48000.0) + noise;
            }
            if (kind == 0) xrnnr(nr3, 0); else xsbnr(nr4, 0);
            for (int i = 0; i < 2*size; ++i) {
                if (!isfinite(output[i]) || fabs(output[i]) > 10) failed = 1;
                if (block >= 10) energies[kind] += output[i] * output[i];
            }
        }
    }
    if (!(energies[0] > 1e-16 && energies[1] > 1e-16)) failed = 1;
    destroy_sbnr(nr4); destroy_rnnr(nr3);
    _aligned_free(output); _aligned_free(input);
    return failed;
}
