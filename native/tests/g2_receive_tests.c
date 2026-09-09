/* SPDX-License-Identifier: GPL-2.0-or-later
 * G2 hardware profile exercised ONLY via test-only loopback entry point.
 * Standard loopback ports are exclusive; conflicts fail rather than use LAN. */
#include "cm_p2_receive.h"
#include "radio_socket.h"
#include "p2_rx_packet.h"
#include "g2_receive_policy.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#ifdef _WIN32
#include <Windows.h>
#else
#include <time.h>
static void Sleep(int ms) { struct timespec t = {0, ms * 1000000}; nanosleep(&t, NULL); }
#endif
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed %s line %d\n", #x, __LINE__); exit(1); } } while (0)
extern void init_impulse_cache(int);
extern void destroy_impulse_cache(void);
extern void ThetisWdspSetPlanningTimeLimit(double);
extern int test_peer_send(cm_socket, int, const void *, int);
static cm_socket peers[4] = {CM_INVALID_SOCKET,CM_INVALID_SOCKET,CM_INVALID_SOCKET,CM_INVALID_SOCKET};
static const int offsets[4] = {0,1,3,13};
static int sideband_sleep_ms = 5;
static int checkpoint(int stage, void *context) { return stage == *(int *)context; }
static void validate_sent(int *run_seen, int *stop_seen)
{
    unsigned char packet[1444];
    for (int i = 0; i < 3; ++i)
    {
        int length;
        while ((length = cm_socket_receive_loopback(peers[i], packet, sizeof(packet), 0)) != -1)
        {
            CHECK(p2_g2_packet_allowed(offsets[i], packet, length));
            if (i == 2) { if (packet[4]) *run_seen = 1; else ++*stop_seen; }
        }
    }
}
static void verify_sideband(int mode, int offset)
{
    int run_seen = 0, stop_seen = 0;
    validate_sent(&run_seen,&stop_seen); run_seen = stop_seen = 0;
    // Signal correctness is sample-driven, not a timer-speed benchmark. The
    // safety scenarios below independently require actual native deadlines.
    // This long fallback still bounds a broken/stalled fixture.
    CHECK(ThetisG2ReceiveTestOpen(14074000,60,NULL,NULL) == 0);
    CHECK(ThetisP2ReceiveSetControls(1,mode,100,3000) == 0);
    CHECK(ThetisReceiveSetGain(1,-20,0,0,60) == 0); // deterministic unity AGC, no device
    int64_t state[24], safety[8], metadata[12];
    CHECK(ThetisP2ReceiveGetState(state,24) == 24);
    unsigned char packet[1444] = {0}, status[60] = {0}; packet[13] = 24; packet[15] = 238; status[4] = 16;
    double audio[8192], energy = 0; float pixels[4095]; long measured = 0, warmup = 0; int spectra = 0;
    uint32_t sequence = 0; long sample = 0;
    for (int step = 0; step < 2000; ++step)
    {
        validate_sent(&run_seen,&stop_seen);
        // Return PCM credit before refilling input so a delayed wakeup does
        // not need an extra sleep just to notice the previous batch completed.
        int count = ThetisP2ReceiveReadAudio(audio,4096); CHECK(count >= 0);
        // Match the established CM controls/gain fixtures: discard one full
        // second of produced 48 kHz PCM before measuring settled rejection.
        for (int i = 0; i < count; ++i)
        {
            CHECK(isfinite(audio[2*i]) && isfinite(audio[2*i+1]));
            if (warmup++ >= 48000) { energy += audio[2*i]*audio[2*i]; ++measured; }
        }
        int n = ThetisP2ReceiveReadSpectrum(1,pixels,4095,metadata,12); CHECK(n >= 0);
        if (n > 0)
        {
            int peak = 0; for (int i = 1; i < n; ++i) if (pixels[i] > pixels[peak]) peak = i;
            CHECK(fabs((peak-n/2)*192000.0/4096-offset) < 50); ++spectra;
        }
        CHECK(ThetisG2ReceiveGetState(safety,8) == 8);
        if (!safety[2] || measured >= 24000) break;
        if (run_seen)
        {
            // Heartbeats must not wait for a fixed number of possibly slow
            // wakeups. This is fixture traffic; the real watchdog is unchanged.
            CHECK(test_peer_send(peers[1],(int)state[2],status,60) == 60);
            // Credit returned PCM at the fixed 192k -> 48k ratio. Keep at most
            // 4096 complex samples outstanding across UDP, CM, DSP and the tap,
            // below CM's 6144-sample input ring. Each wakeup sends at most 17
            // packets, and cannot accumulate more input while DSP falls behind.
            // This tests settled signal correctness, not physical audio timing.
            while (sample + 238 <= 4 * warmup + 4096)
            {
                packet[0] = (unsigned char)(sequence>>24); packet[1] = (unsigned char)(sequence>>16);
                packet[2] = (unsigned char)(sequence>>8); packet[3] = (unsigned char)sequence++;
                for (int i = 0; i < 238; ++i, ++sample)
                {
                    // Independent legacy/Saturn wire convention: positive RF
                    // offset rotates negatively, unlike the owned P2 simulator.
                    double phase = -6.283185307179586 * offset * sample / 192000.0;
                    for (int part = 0; part < 2; ++part)
                    {
                        uint32_t v = (uint32_t)(int32_t)llround(8388607 * .1 * (part ? sin(phase) : cos(phase)));
                        int k = 16 + i*6 + part*3;
                        packet[k] = (unsigned char)(v>>16); packet[k+1] = (unsigned char)(v>>8); packet[k+2] = (unsigned char)v;
                    }
                }
                CHECK(test_peer_send(peers[3],(int)state[2],packet,1444) == 1444);
            }
        }
        Sleep(sideband_sleep_ms);
    }
    double rms = measured ? sqrt(energy/measured) : 0;
    int wanted = (mode == 1) == (offset > 0);
    CHECK(ThetisP2ReceiveGetState(state,24) == 24);
    printf("G2 sideband mode=%d offset=%d sleep=%dms: RMS=%.9g wanted=%d PCM=%ld measured=%ld spectra=%d missing=%lld input-overruns=%lld audio-drops=%lld worker=%lld stop-reason=%lld status=%lld socket-errors=%lld\n",
        mode,offset,sideband_sleep_ms,rms,wanted,warmup,measured,spectra,(long long)state[9],(long long)state[17],(long long)state[19],
        (long long)safety[2],(long long)safety[3],(long long)state[14],(long long)state[15]);
    CHECK(measured >= 24000 && spectra > 0 && safety[2] && safety[3] == 0 && !safety[4] && !safety[7]);
    CHECK(!state[9] && !state[10] && !state[11] && !state[12] && !state[15] && !state[17] && !state[19] && !state[21]);
    CHECK(wanted ? rms > .003 && rms < .02 : rms < .00001);
    CHECK(ThetisP2ReceiveClose() == 0);
}
int main(int argc, char **argv)
{
    int coarse = argc == 2 && !strcmp(argv[1],"--coarse-timer");
    CHECK(argc == 1 || coarse);
    // Emulate delayed/coalesced fixture wakeups, including a status interval
    // that would exceed the real 3-second watchdog if tied to 30 iterations.
    if (coarse) sideband_sleep_ms = 125;
    CHECK(g2_duration_valid(60,0) && !g2_duration_valid(61,0));
    CHECK(g2_duration_valid(3600,1) && !g2_duration_valid(3601,1) && !g2_duration_valid(0,1));
    CHECK(!g2_deadline_reached(12345,3612344,3600));
    CHECK(g2_deadline_reached(12345,3612345,3600) && g2_deadline_reached(12345,3612346,3600));
    uint32_t address;
    CHECK(!cm_socket_rx_subnet("169.254.187.120", "169.254.47.65", "255.255.0.0", &address));
    const char *bad[][3] = {
        {"127.0.0.1","127.0.0.2","255.0.0.0"}, {"224.0.0.1","224.0.0.2","255.0.0.0"},
        {"192.0.2.255","192.0.2.2","255.255.255.0"}, {"192.0.2.0","192.0.2.2","255.255.255.0"},
        {"192.0.2.1","192.0.2.1","255.255.255.0"}, {"192.0.2.1","192.0.3.1","255.255.255.0"},
        {"0.0.0.1","0.0.0.2","255.0.0.0"}, {"192.0.2.1","192.0.2.2","255.0.255.0"},
        {"192.0.2.1","192.0.2.2","0.0.0.0"}, {"192.0.2.1","192.0.2.2","255.255.255.255"}
    };
    for (unsigned i = 0; i < sizeof(bad)/sizeof(bad[0]); ++i)
    {
        CHECK(cm_socket_rx_subnet(bad[i][0], bad[i][1], bad[i][2], &address) == -1);
        CHECK(ThetisG2ReceiveOpen(1, bad[i][0], bad[i][1], bad[i][2], 14200000, 1, NULL, NULL) == -1);
        CHECK(ThetisG2ReceiveEnduranceOpen(1, bad[i][0], bad[i][1], bad[i][2], 14200000, 3600, NULL, NULL) == -1);
    }
    CHECK(ThetisG2ReceiveOpen(2,"169.254.1.1","169.254.1.2","255.255.0.0",14200000,1,NULL,NULL) == -1);
    CHECK(ThetisG2ReceiveOpen(1,"169.254.1.1","169.254.1.2","255.255.0.0",7000000,1,NULL,NULL) == -1);
    CHECK(ThetisG2ReceiveOpen(1,"169.254.1.1","169.254.1.2","255.255.0.0",14200000,61,NULL,NULL) == -1);
    CHECK(ThetisG2ReceiveEnduranceOpen(2,"169.254.1.1","169.254.1.2","255.255.0.0",14200000,3600,NULL,NULL) == -1);
    CHECK(ThetisG2ReceiveEnduranceOpen(1,"169.254.1.1","169.254.1.2","255.255.0.0",7000000,3600,NULL,NULL) == -1);
    CHECK(ThetisG2ReceiveEnduranceOpen(1,"169.254.1.1","169.254.1.2","255.255.0.0",14200000,3601,NULL,NULL) == -1);
    CHECK(ThetisG2ReceiveEnduranceOpen(1,"169.254.1.1","169.254.1.2","255.255.0.0",14200000,0,NULL,NULL) == -1);
    CHECK(ThetisG2ReceiveGetState(NULL,8) == -1);
    init_impulse_cache(0); ThetisWdspSetPlanningTimeLimit(0);
    // Real production extended export accepts the maximum, but abort at DSP
    // stage 1, BEFORE stage 3 can bind a socket or stage 5 can send anything.
    int before_sockets = 1;
    CHECK(ThetisG2ReceiveEnduranceOpen(1,"192.0.2.1","192.0.2.2","255.255.255.0",14200000,3600,
        checkpoint,&before_sockets) == -4);
    for (int stage = 1; !coarse && stage <= 5; ++stage)
    {
        CHECK(ThetisG2ReceiveTestOpen(14200000,1,checkpoint,&stage) == -4);
        CHECK(ThetisG2ReceiveEnduranceTestOpen(14200000,3600,checkpoint,&stage) == -4);
        int64_t s[24]; CHECK(ThetisP2ReceiveGetState(s,24) == 24 && !s[1] && !s[6] && s[22] == 1);
    }
    for (int i = 0; i < 4; ++i)
    { int bound; CHECK(cm_socket_open("127.0.0.1",1024+offsets[i],&peers[i],&bound) == 0); }
    // Deadline, every key bit, missing status, missing IQ, malformed status.
    const int keybits[] = {0,1,2,4,128,0,0,0};
    for (int scenario = 0; !coarse && scenario < 8; ++scenario)
    {
        int run_seen = 0, stop_seen = 0;
        validate_sent(&run_seen,&stop_seen); run_seen = stop_seen = 0;
        // Exercise the extended owner too: short native deadline and an hour
        // limit interrupted by the same immediate key/status/IQ safety trips.
        CHECK(ThetisG2ReceiveEnduranceTestOpen(14200000, scenario == 0 ? 1 : 3600, NULL, NULL) == 0);
        CHECK(ThetisP2ReceiveTune(7000000) == -1);
        CHECK(ThetisP2ReceiveTune(14300000) == 0);
        int64_t state[24], safety[9]; safety[8] = 987654;
        CHECK(ThetisP2ReceiveGetState(state,24) == 24 && state[22] == 0 && state[23] == 0);
        int port = (int)state[2];
        unsigned char iq[1444] = {0}, status[60] = {0}; iq[13] = 24; iq[15] = 238;
        status[4] = (unsigned char)(16 | keybits[scenario]); // PLL alone must not trip
        double audio[8192];
        int step;
        for (step = 0; step < 3500; ++step)
        {
            validate_sent(&run_seen,&stop_seen);
            if (run_seen)
            {
                uint32_t seq = (uint32_t)step;
                iq[0] = (unsigned char)(seq>>24); iq[1] = (unsigned char)(seq>>16);
                iq[2] = (unsigned char)(seq>>8); iq[3] = (unsigned char)seq;
                if (scenario != 6) CHECK(test_peer_send(peers[3],port,iq,1444) == 1444);
                if (scenario != 5 && step % 30 == 0)
                    CHECK(test_peer_send(peers[1],port,status,scenario == 7 ? 59 : 60) == (scenario == 7 ? 59 : 60));
            }
            int count = ThetisP2ReceiveReadAudio(audio,4096); CHECK(count >= 0);
            for (int i = 0; i < count*2; ++i) CHECK(isfinite(audio[i]));
            CHECK(ThetisG2ReceiveGetState(safety,8) == 8 && safety[8] == 987654);
            if (!safety[2]) break;
            Sleep(2);
        }
        CHECK(step < 3500 && run_seen);
        validate_sent(&run_seen,&stop_seen);
        CHECK(safety[3] == (scenario == 0 ? 1 : scenario < 5 ? 4 : scenario == 5 ? 3 : scenario == 6 ? 2 : 7));
        CHECK(safety[4] == keybits[scenario] && safety[6] >= 5 && !safety[7] && stop_seen >= 5);
        CHECK(ThetisP2ReceiveClose() == 0);
        CHECK(ThetisP2ReceiveGetState(state,24) == 24 && !state[1] && !state[6] && state[22] == 1);
    }
    for (int mode = 0; mode < 2; ++mode)
        for (int offset = -1500; offset <= 1500; offset += 3000) verify_sideband(mode,offset);
    for (int i = 0; i < 4; ++i) cm_socket_close(&peers[i]);
    destroy_impulse_cache();
    puts(coarse ? "PASS: G2 settled sidebands and clean counters with 125 ms fixture wakeups" :
        "PASS: G2 wire firewall, rollback, deadlines, key/status/IQ fail-close, hardware IQ orientation and both sidebands");
    return 0;
}
