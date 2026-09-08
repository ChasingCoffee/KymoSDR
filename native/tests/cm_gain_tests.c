/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "cm_p2_receive.h"
#include "radio_socket.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <Windows.h>
#else
#include <time.h>
static void Sleep(int ms) { struct timespec t = {0,ms*1000000}; nanosleep(&t,NULL); }
#endif
extern void init_impulse_cache(int);
extern void destroy_impulse_cache(void);
extern void ThetisWdspSetPlanningTimeLimit(double);
extern int ThetisTestCMGain(int,int,int,int);
extern int ThetisTestCMGainHistory(void);
extern int test_peer_send(cm_socket,int,const void *,int);
#define CHECK(x) do { if (!(x)) { fprintf(stderr,"Gain failure %d: %s\n",__LINE__,#x); exit(1); } } while (0)
static void closed(void)
{
    int64_t s[12]; s[11] = 12345;
    CHECK(ThetisReceiveGetGain(s,11) == 11 && s[0] == 1 && s[11] == 12345);
    for (int i = 1; i < 11; ++i) CHECK(s[i] == 0);
}
static int checkpoint(int stage,void *context)
{
    CHECK(ThetisReceiveSetGain(1,0,0,4,60) == -2);
    return stage == *(int *)context ? 1 : 0;
}
static void commands(cm_socket peer,int high)
{
    unsigned char p[1444]; int n;
    while ((n = cm_socket_receive_loopback(peer,p,sizeof(p),0)) != -1)
    {
        CHECK(n == 60 || n == 1444);
        if (high)
        {
            CHECK(n == 1444 && (p[4]&~1) == 0);
            for (int i = 5; i < 1444; ++i) if (i < 9 || i > 12) CHECK(p[i] == 0);
        }
    }
}
int main(void)
{
    CHECK(ThetisReceiveGainAbi() == 1);
    int64_t invalid[11]; CHECK(ThetisReceiveGetGain(NULL,11) == -1); CHECK(ThetisReceiveGetGain(invalid,10) == -1);
    CHECK(ThetisReceiveSetGain(2,0,0,0,60) == -1); CHECK(ThetisReceiveSetGain(1,0,0,0,60) == -3); closed();
    const int bad[][4] = {{1,0,0,60},{-61,0,0,60},{0,2,0,60},{0,0,1,60},{0,0,5,60},{0,0,0,-1},{0,0,0,81}};
    for (int i = 0; i < 7; ++i)
    {
        CHECK(ThetisReceiveSetGain(1,bad[i][0],bad[i][1],bad[i][2],bad[i][3]) == -1);
        CHECK(ThetisReceiveOpenWithGain(1,2,"127.0.0.1",51024,0,48000,14199000,1,300,3000,
            bad[i][0],bad[i][1],bad[i][2],bad[i][3],NULL,NULL) == -1); closed();
    }
    init_impulse_cache(0); ThetisWdspSetPlanningTimeLimit(0);
    cm_socket peer[5]; const int offsets[] = {0,1,2,3,11}; int base;
    for (base = 20000; base < 60000; base += 32)
    {
        int ok = 1;
        for (int i = 0; i < 5; ++i) peer[i] = CM_INVALID_SOCKET;
        for (int i = 0; i < 5; ++i) { int port; if (cm_socket_open("127.0.0.1",base+offsets[i],&peer[i],&port)) { ok = 0; break; } }
        if (ok) break;
        for (int i = 0; i < 5; ++i) cm_socket_close(&peer[i]);
    }
    CHECK(base < 60000);
    for (int at = 1; at <= 5; ++at)
    {
        CHECK(ThetisReceiveOpenWithGain(1,2,"127.0.0.1",base,0,48000,14199000,1,300,3000,-20,1,2,40,checkpoint,&at) == -4); closed();
        for (int i = 0; i < 4; ++i) commands(peer[i],i == 3);
    }
    CHECK(ThetisReceiveOpenWithGain(1,2,"127.0.0.1",base,0,48000,14199000,1,300,3000,0,0,4,60,NULL,NULL) == 0);
    int64_t state[24]; CHECK(ThetisP2ReceiveGetState(state,24) == 24); int port = (int)state[2];
    // Verify actual native fields, including fast -> slow restoration.
    const int modes[] = {2,4,2,3,2,0,4};
    for (int i = 0; i < 7; ++i) { CHECK(ThetisReceiveSetGain(1,0,0,modes[i],60) == 0); CHECK(ThetisTestCMGain(0,0,modes[i],60) == 0); }
    unsigned char p[1444]; double audio[4096]; int sample = 0; uint32_t sequence = 0;
    const int settings[][4] = {{0,0,4,60},{0,0,4,60},{0,0,4,60},{0,1,4,60},{-20,0,0,60},{0,0,4,20}};
    const double amplitude[] = {.005,.25,.005,.25,.25,.0001};
    for (int phase = 0; phase < 6; ++phase)
    {
        if (phase == 3) CHECK(ThetisTestCMGainHistory() == 0);
        const int *s = settings[phase]; CHECK(ThetisReceiveSetGain(1,s[0],s[1],s[2],s[3]) == 0);
        CHECK(ThetisTestCMGain(s[0],s[1],s[2],s[3]) == 0);
        int64_t gain[12]; gain[11] = 54321;
        CHECK(ThetisReceiveGetGain(gain,11) == 11 && gain[11] == 54321);
        int64_t generation = gain[10]; CHECK(ThetisReceiveSetGain(1,s[0],s[1],s[2],s[3]) == 0);
        CHECK(ThetisReceiveGetGain(gain,11) == 11 && gain[10] == generation);
        int skip = 48000, frames = 0; double energy = 0, peak = 0;
        for (int block = 0; block < 330; ++block)
        {
            memset(p,0,sizeof(p)); uint32_t seq = sequence++;
            p[0] = (unsigned char)(seq>>24); p[1] = (unsigned char)(seq>>16); p[2] = (unsigned char)(seq>>8); p[3] = (unsigned char)seq; p[13] = 24; p[15] = 238;
            for (int i = 0; i < 238; ++i,++sample)
            {
                double angle = 6.283185307179586*1000*sample/48000;
                for (int q = 0; q < 2; ++q)
                {
                    uint32_t v = (uint32_t)(int32_t)(amplitude[phase]*8388608*(q ? sin(angle) : cos(angle)));
                    int at = 16+6*i+3*q; p[at] = (unsigned char)(v>>16); p[at+1] = (unsigned char)(v>>8); p[at+2] = (unsigned char)v;
                }
            }
            CHECK(test_peer_send(peer[4],port,p,1444) == 1444); Sleep(5);
            for (int i = 0; i < 4; ++i) commands(peer[i],i == 3);
            int n = ThetisP2ReceiveReadAudio(audio,2048); CHECK(n >= 0);
            for (int i = 0; i < n; ++i)
            {
                double v = audio[2*i]; CHECK(isfinite(v) && isfinite(audio[2*i+1]));
                if (s[1]) CHECK(v == 0 && audio[2*i+1] == 0); // includes transition tail
                peak = fmax(peak,fabs(v));
                if (skip > 0) { --skip; continue; } energy += v*v; ++frames;
            }
        }
        CHECK(frames > 20000); double rms = sqrt(energy/frames);
        double expected = phase < 3 ? (1-exp(-4))*.9999/sqrt(2) : phase == 3 ? 0 : phase == 4 ? .025/sqrt(2) : .001/sqrt(2);
        printf("Gain phase %d: RMS=%g expected=%g peak=%g\n",phase,rms,expected,peak);
        CHECK(fabs(rms-expected) < fmax(1e-6,expected*.03) && peak < 1.5);
        CHECK(ThetisP2ReceiveGetState(state,24) == 24 && state[9] == 0 && state[15] == 0 && state[17] == 0 && state[19] == 0 && state[21] == 0);
    }
    CHECK(ThetisP2ReceiveClose() == 0); closed();
    for (int i = 0; i < 5; ++i) cm_socket_close(&peer[i]);
    destroy_impulse_cache(); puts("PASS: gain/mute/AGC native signal, preset restore, ABI bounds and rollback"); return 0;
}
