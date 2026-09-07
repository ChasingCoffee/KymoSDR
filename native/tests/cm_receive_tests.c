/* SPDX-License-Identifier: GPL-2.0-or-later
 * Real native packet -> router -> CM input worker -> WDSP exercise, including
 * under sanitizers. Fixture sockets can only send to IPv4 loopback.
 */
#include "cm_p2_receive.h"
#include "cm_transport.h"
#include "cm_spectrum.h"
#include "radio_socket.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <Windows.h>
#else
#include <time.h>
static void Sleep(int ms) { struct timespec t = {ms / 1000, (ms % 1000) * 1000000}; nanosleep(&t, NULL); }
#endif
extern void init_impulse_cache(int);
extern void destroy_impulse_cache(void);
extern void ThetisWdspSetPlanningTimeLimit(double);
extern int ThetisTestCMInputRing(void);
extern int ThetisTestCMSpectrum(void);
extern int test_process_threads(void);
extern int test_process_threads_after_join(int);
extern int test_peer_send(cm_socket, int, const void *, int);
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed: %s at %d\n", #x, __LINE__); exit(1); } } while (0)
static int baseline;
static void closed(void)
{
    int64_t state[25]; state[24] = 1234567;
    int32_t core[16], transport[16];
    CHECK(ThetisP2ReceiveGetState(state, 24) == 24 && state[24] == 1234567);
    CHECK(state[0] == 1 && state[1] == 0 && state[2] == 0 && state[6] == 0 && state[22] == 1 && state[23] == 0);
    CHECK(ThetisCmGetState(core, 16) == 16 && core[1] == 0 && core[10] == 0);
    CHECK(ThetisTransportGetState(transport, 16) == 16 && transport[7] == 0 && transport[2] == 0);
    int threads = test_process_threads_after_join(baseline);
    CHECK(threads > 0 && threads <= baseline);
}
static int checkpoint(int stage, void *context)
{
    int target = *(int *)context;
    CHECK(ThetisP2ReceiveClose() == -2);
    CHECK(ThetisCmClose() == -2);
    return stage == abs(target) ? (target < 0 ? -1 : 1) : 0;
}
static void put32(unsigned char *p, uint32_t n)
{ p[0] = (unsigned char)(n >> 24); p[1] = (unsigned char)(n >> 16); p[2] = (unsigned char)(n >> 8); p[3] = (unsigned char)n; }
static void tone(unsigned char *p, uint32_t sequence, int *sample)
{
    memset(p, 0, 1444); put32(p, sequence); p[13] = 24; p[15] = 238;
    for (int i = 0; i < 238; ++i, ++*sample)
    {
        double phase = 6.283185307179586 * 1000 * *sample / 48000;
        for (int component = 0; component < 2; ++component)
        {
            int32_t n = (int32_t)(0.1 * 8388608 * (component ? sin(phase) : cos(phase)));
            uint32_t bits = (uint32_t)n;
            unsigned char *s = p + 16 + 6 * i + 3 * component;
            s[0] = (unsigned char)(bits >> 16); s[1] = (unsigned char)(bits >> 8); s[2] = (unsigned char)bits;
        }
    }
}
static int high_controls(cm_socket socket)
{
    unsigned char p[1444]; int saw_stop = 0, length;
    while ((length = cm_socket_receive_loopback(socket, p, sizeof(p), 0)) != -1)
    {
        CHECK(length == 1444 && (p[4] & ~1) == 0);
        if (!p[4]) saw_stop = 1;
        for (int i = 5; i < 1444; ++i)
            if (i < 45 || i > 48) CHECK(p[i] == 0); // DDC9 tuning only, no PTT/CW/DUC/drive/relays
    }
    return saw_stop;
}
int main(void)
{
    baseline = test_process_threads(); CHECK(baseline > 0);
    CHECK(ThetisP2ReceiveOpen(2, "127.0.0.1", 51024, 2, 192000, 14199000, NULL, NULL) == -1);
    CHECK(ThetisP2ReceiveOpen(1, "192.0.2.1", 51024, 2, 192000, 14199000, NULL, NULL) == -1);
    CHECK(ThetisP2ReceiveOpen(1, "0.0.0.0", 51024, 2, 192000, 14199000, NULL, NULL) == -1);
    CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", 65516, 2, 192000, 14199000, NULL, NULL) == -1);
    CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", 51024, 10, 192000, 14199000, NULL, NULL) == -1);
    CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", 51024, 2, 768000, 14199000, NULL, NULL) == -1);
    CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", 51024, 2, 192000, -1, NULL, NULL) == -1);
    CHECK(ThetisP2ReceiveGetState(NULL, 24) == -1);
    float spectrum[CM_SPECTRUM_PIXELS + 1]; int64_t metadata[13];
    spectrum[0] = spectrum[CM_SPECTRUM_PIXELS] = 12345;
    metadata[0] = metadata[12] = 1234567;
    CHECK(ThetisP2ReceiveSpectrumAbi() == 1);
    CHECK(ThetisP2ReceiveReadSpectrum(2, spectrum, CM_SPECTRUM_PIXELS, metadata, 12) == -1);
    CHECK(ThetisP2ReceiveReadSpectrum(1, spectrum, CM_SPECTRUM_PIXELS - 1, metadata, 12) == -1);
    CHECK(ThetisP2ReceiveReadSpectrum(1, spectrum, CM_SPECTRUM_PIXELS, metadata, 11) == -1);
    CHECK(ThetisP2ReceiveReadSpectrum(1, NULL, CM_SPECTRUM_PIXELS, metadata, 12) == -1);
    CHECK(ThetisP2ReceiveReadSpectrum(1, spectrum, CM_SPECTRUM_PIXELS, NULL, 12) == -1);
    CHECK(ThetisP2ReceiveReadSpectrum(1, spectrum, CM_SPECTRUM_PIXELS, metadata, 12) == -3);
    CHECK(spectrum[0] == 12345 && metadata[0] == 1234567);
    CHECK(ThetisP2ReceiveClose() == 0); closed();
    init_impulse_cache(0); ThetisWdspSetPlanningTimeLimit(0);
    CHECK(ThetisCmOpen(1, 192000, 0, 0, NULL, NULL) == 0);
    CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", 51024, 2, 192000, 14199000, NULL, NULL) == -2);
    int ring_result = ThetisTestCMInputRing();
    if (ring_result) fprintf(stderr, "Input ring failure at helper line %d\n", ring_result);
    CHECK(ring_result == 0);
    int spectrum_result = ThetisTestCMSpectrum();
    if (spectrum_result) fprintf(stderr, "Spectrum failure at helper line %d\n", spectrum_result);
    CHECK(spectrum_result == 0);
    CHECK(ThetisCmClose() == 0); closed();
    for (int target = -5; target <= 5; ++target)
    {
        if (!target) continue;
        CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", 51024, 2, 192000, 14199000, checkpoint, &target) == (target < 0 ? -5 : -4));
        closed();
    }
    for (int fault = 4; fault <= 5; ++fault)
    {
        CHECK(ThetisP2ReceiveTestFault(fault) == 0);
        CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", 51024, 2, 192000, 14199000, NULL, NULL) == -3);
        closed();
    }
    CHECK(ThetisP2ReceiveTestFault(0) == 0);
    cm_socket peer[5]; const int offsets[] = {0,1,2,3,20}; int base;
    for (base = 20000; base < 60000; base += 32)
    {
        int ok = 1;
        for (int i = 0; i < 5; ++i) peer[i] = CM_INVALID_SOCKET;
        for (int i = 0; i < 5; ++i)
        {
            int port;
            if (cm_socket_open("127.0.0.1", base + offsets[i], &peer[i], &port)) { ok = 0; break; }
        }
        if (ok) break;
        for (int i = 0; i < 5; ++i) cm_socket_close(&peer[i]);
    }
    CHECK(base < 60000);
    unsigned char packet[1450] = {0}; double audio[4096];
    for (int cycle = 0; cycle < 3; ++cycle)
    {
        int64_t state[25]; state[24] = 1234567;
        CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", base, 9, 48000, 14199000, NULL, NULL) == 0);
        CHECK(ThetisCmClose() == -2);
        CHECK(ThetisP2ReceiveOpen(1, "127.0.0.1", base, 9, 48000, 14199000, NULL, NULL) == -2);
        CHECK(ThetisTransportOpen(1, "127.0.0.1", base, "127.0.0.1", 0, 1, 11, 1, NULL, NULL) == -2);
        CHECK(ThetisP2ReceiveGetState(state, 23) == -1);
        CHECK(ThetisP2ReceiveGetState(state, 24) == 24 && state[24] == 1234567);
        int port = (int)state[2], sample = 0;
        CHECK(ThetisP2ReceiveReadSpectrum(1, spectrum, CM_SPECTRUM_PIXELS, metadata, 12) == 0);
        int64_t last_spectrum = 0, last_publication = 0, coalesced = 0;
        int spectrum_frames = 0;
        memset(packet, 0, sizeof(packet));
        CHECK(test_peer_send(peer[1], port, packet, 60) == 60);
        CHECK(test_peer_send(peer[2], port, packet, 132) == 132);
        CHECK(test_peer_send(peer[0], port, packet, 1444) == 1444); // foreign source port
        CHECK(test_peer_send(peer[4], port, packet, 1444) == 1444); // bad header
        CHECK(test_peer_send(peer[4], port, packet, 1450) == 1450); // truncated by receive cap
        int skipped = 0, frames = 0, crossings = 0; double previous = 0, energy = 0;
        const uint32_t initial[] = {0xfffffffeu,0xffffffffu,0,2,2,1};
        for (int block = 0; block < 330; ++block)
        {
            uint32_t sequence = block < 6 ? initial[block] : (uint32_t)(block - 3);
            tone(packet, sequence, &sample);
            CHECK(test_peer_send(peer[4], port, packet, 1444) == 1444);
            Sleep(5);
            CHECK(!high_controls(peer[3]));
            if (block % 30 == 0) // deliberately slower than production: bounded latest-frame mailbox
            {
                int pixels = ThetisP2ReceiveReadSpectrum(1, spectrum, CM_SPECTRUM_PIXELS, metadata, 12);
                CHECK(pixels == 0 || pixels == CM_SPECTRUM_PIXELS);
                if (pixels)
                {
                    CHECK(metadata[0] == 1 && metadata[1] > last_spectrum && metadata[2] == 1 && metadata[3] > last_publication);
                    CHECK(metadata[4] == 14199000 && metadata[5] == 9 && metadata[6] == 48000);
                    CHECK(metadata[7] == CM_SPECTRUM_FFT_SIZE && metadata[8] == CM_SPECTRUM_PIXELS);
                    CHECK(metadata[9] >= coalesced && metadata[10] == 1 && metadata[11] == 0);
                    CHECK(spectrum[CM_SPECTRUM_PIXELS] == 12345 && metadata[12] == 1234567);
                    int peak = 0;
                    for (int p = 0; p < pixels; ++p)
                    {
                        CHECK(isfinite(spectrum[p]));
                        if (spectrum[p] > spectrum[peak]) peak = p;
                    }
                    CHECK(fabs(-24000 + (peak + 1) * (48000.0 / CM_SPECTRUM_FFT_SIZE) - 1000) < 12);
                    CHECK(fabs(spectrum[peak] + 20) < 1.6);
                    last_spectrum = metadata[1]; last_publication = metadata[3]; coalesced = metadata[9];
                    ++spectrum_frames;
                }
            }
            int count = ThetisP2ReceiveReadAudio(audio, 2048); CHECK(count >= 0);
            for (int i = 0; i < count; ++i)
            {
                CHECK(isfinite(audio[2*i]) && isfinite(audio[2*i+1]));
                if (skipped++ < 48000) continue;
                double value = audio[2*i];
                if (frames && previous <= 0 && value > 0) ++crossings;
                previous = value; energy += value * value; ++frames;
            }
        }
        CHECK(ThetisP2ReceiveGetState(state, 24) == 24);
        printf("Active diagnostics: cycle=%d frames=%d RMS=%g IQ=%lld errors=%lld overruns=%lld audio=%lld\n", cycle + 1,
            frames, frames ? sqrt(energy / frames) : 0, (long long)state[7], (long long)state[21], (long long)state[17], (long long)state[20]);
        fflush(stdout);
        CHECK(frames > 20000 && sqrt(energy / frames) > 0.01);
        CHECK(fabs(crossings * 48000.0 / (frames - 1) - 1000) < 10);
        CHECK(ThetisP2ReceiveGetState(state, 24) == 24);
        CHECK(state[7] == 328 && state[8] == 328 * 238 && state[9] == 1 && state[10] == 2);
        CHECK(state[11] == 2 && state[12] == 1 && state[13] == 1 && state[14] == 1);
        CHECK(state[15] == 0 && state[17] == 0 && state[19] == 0 && state[21] == 0);
        CHECK(spectrum_frames >= 5 && coalesced > 0);
        printf("Spectrum: %d pulls, sequence=%lld coalesced=%lld, signed frequency/level and bounded ABI passed\n",
            spectrum_frames, (long long)last_spectrum, (long long)coalesced);
        CHECK(ThetisP2ReceiveReadAudio(audio, 16385) == -1);
        CHECK(ThetisP2ReceiveTune(14198500) == 0);
        spectrum[0] = 12345; metadata[0] = 1234567;
        CHECK(ThetisP2ReceiveReadSpectrum(1, spectrum, CM_SPECTRUM_PIXELS, metadata, 12) == 0);
        CHECK(spectrum[0] == 12345 && metadata[0] == 1234567); // no pre-tune frame or buffer writes
        CHECK(ThetisP2ReceiveClose() == 0 && ThetisP2ReceiveClose() == 0); closed();
        CHECK(high_controls(peer[3])); // worker's final RUN0 reached the peer before close returned
        cm_socket rebound = CM_INVALID_SOCKET; int rebound_port;
        CHECK(cm_socket_open("127.0.0.1", port, &rebound, &rebound_port) == 0); cm_socket_close(&rebound);
        printf("P2 active cycle %d: %d audio frames, %.2f Hz; packet accounting, STOP and port rebind passed\n", cycle + 1, frames, crossings * 48000.0 / (frames - 1));
    }
    for (int i = 0; i < 5; ++i) cm_socket_close(&peer[i]);
    destroy_impulse_cache();
    puts("PASS: real RX pipeline, wrap/gap/late/malformed packets, bounded CM ring, 10 rollback checkpoints, event/thread faults, three active cycles");
    return 0;
}
