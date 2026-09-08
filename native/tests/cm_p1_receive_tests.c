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
static void Sleep(int ms) { struct timespec t = {0, ms * 1000000}; nanosleep(&t, NULL); }
#endif
extern void init_impulse_cache(int);
extern void destroy_impulse_cache(void);
extern void ThetisWdspSetPlanningTimeLimit(double);
extern int test_peer_send(cm_socket, int, const void *, int);
#define CHECK(x) do { if (!(x)) { fprintf(stderr,"P1 receive failure %d: %s\n",__LINE__,#x); exit(1); } } while (0)
static int open_p1(int port, int mode, cm_checkpoint checkpoint, void *context)
{ return ThetisReceiveOpenWithControls(1, 1, "127.0.0.1", port, 0, 48000, 14199000, mode, 300, 3000, checkpoint, context); }
static int checkpoint(int stage, void *context)
{
    int target = *(int *)context;
    CHECK(ThetisP2ReceiveClose() == -2);
    return stage == abs(target) ? (target > 0 ? 1 : -1) : 0;
}
static void closed(void)
{
    int64_t s[24]; CHECK(ThetisP2ReceiveGetState(s, 24) == 24);
    CHECK(s[1] == 0 && s[2] == 0 && s[6] == 0 && s[22] == 1 && s[23] == 0);
    int32_t c[16]; CHECK(ThetisCmGetState(c,16) == 16 && c[1] == 0 && c[10] == 0);
}
static int commands(cm_socket peer)
{
    unsigned char p[1444]; int n, stopped = 0;
    while ((n = cm_socket_receive_loopback(peer, p, sizeof(p), 0)) != -1)
    {
        CHECK(p[0] == 0xef && p[1] == 0xfe);
        if (n == 64)
        {
            CHECK(p[2] == 4 && p[3] <= 1); if (!p[3]) stopped = 1;
            for (int i = 4; i < 64; ++i) CHECK(p[i] == 0);
        }
        else
        {
            CHECK(n == 1032 && p[2] == 1 && p[3] == 2);
            for (int f = 8; f <= 520; f += 512)
            {
                CHECK(p[f] == 127 && p[f+1] == 127 && p[f+2] == 127);
                if (f == 8) for (int i = 3; i < 8; ++i) CHECK(p[f+i] == 0);
                else { CHECK(p[f+3] == 4 || p[f+3] == 28); if (p[f+3] == 28) for (int i = 4; i < 8; ++i) CHECK(p[f+i] == 0); }
                for (int i = 8; i < 512; ++i) CHECK(p[f+i] == 0); // all TX/audio zero
            }
        }
    }
    return stopped;
}
int main(void)
{
    CHECK(ThetisReceiveProtocolAbi() == 1);
    CHECK(ThetisReceiveOpenWithControls(1, 0, "127.0.0.1", 51024, 0, 48000, 14199000, 1, 300, 3000, NULL, NULL) == -1);
    CHECK(ThetisReceiveOpenWithControls(1, 1, "192.0.2.1", 51024, 0, 48000, 14199000, 1, 300, 3000, NULL, NULL) == -1);
    CHECK(ThetisReceiveOpenWithControls(1, 1, "127.0.0.1", 51024, 1, 48000, 14199000, 1, 300, 3000, NULL, NULL) == -1);
    CHECK(ThetisReceiveOpenWithControls(1, 1, "127.0.0.1", 51024, 0, 96000, 14199000, 1, 300, 3000, NULL, NULL) == -1);
    init_impulse_cache(0); ThetisWdspSetPlanningTimeLimit(0);
    cm_socket peer = CM_INVALID_SOCKET, stranger = CM_INVALID_SOCKET; int port, unused;
    CHECK(cm_socket_open("127.0.0.1", 0, &peer, &port) == 0);
    CHECK(cm_socket_open("127.0.0.1", 0, &stranger, &unused) == 0);
    for (int target = -5; target <= 5; ++target)
    {
        if (!target) continue;
        CHECK(open_p1(port, 1, checkpoint, &target) == (target > 0 ? -4 : -5)); closed(); commands(peer);
    }
    for (int fault = 4; fault <= 5; ++fault)
    {
        CHECK(ThetisP2ReceiveTestFault(fault) == 0);
        CHECK(open_p1(port,1,NULL,NULL) == -3); closed();
    }
    CHECK(ThetisP2ReceiveTestFault(0) == 0);
    for (int cycle = 0; cycle < 2; ++cycle)
    {
        CHECK(open_p1(port, cycle, NULL, NULL) == 0);
        CHECK(ThetisP2ReceiveOpen(1,"127.0.0.1",port,0,48000,14199000,NULL,NULL) == -2);
        int64_t s[24]; CHECK(ThetisP2ReceiveGetState(s,24) == 24); int target = (int)s[2];
        unsigned char p[1033]; double audio[4096], energy = 0, previous = 0; int sample = 0, frames = 0, skipped = 0, crossings = 0;
        const uint32_t first[] = {0xfffffffeu,0xffffffffu,0,2,2,1};
        for (int block = 0; block < 600; ++block)
        {
            memset(p, 0, sizeof(p)); p[0] = 0xef; p[1] = 0xfe; p[2] = 1; p[3] = 6;
            uint32_t seq = block < 6 ? first[block] : (uint32_t)(block - 3);
            p[4] = (unsigned char)(seq >> 24); p[5] = (unsigned char)(seq >> 16); p[6] = (unsigned char)(seq >> 8); p[7] = (unsigned char)seq;
            for (int f = 0; f < 2; ++f)
            {
                int at = 8 + f*512; p[at] = p[at+1] = p[at+2] = 127; p[at+3] = 7; // incoming key/PTT ignored
                for (int i = 0; i < 63; ++i, ++sample)
                {
                    double phase = 6.283185307179586 * (cycle ? 1000 : -1000) * sample / 48000;
                    for (int q = 0; q < 2; ++q)
                    {
                        uint32_t v = (uint32_t)(int32_t)(.1 * 8388608 * (q ? sin(phase) : cos(phase)));
                        int j = at+8+i*8+q*3; p[j] = (unsigned char)(v>>16); p[j+1] = (unsigned char)(v>>8); p[j+2] = (unsigned char)v;
                    }
                    p[at+8+i*8+6] = 0x7f; p[at+8+i*8+7] = 0xff;
                }
            }
            if (block == 0)
            {
                CHECK(test_peer_send(stranger,target,p,1032) == 1032);
                CHECK(test_peer_send(peer,target,p,1033) == 1033);
                p[520] = 0; CHECK(test_peer_send(peer,target,p,1032) == 1032); p[520] = 127;
            }
            CHECK(test_peer_send(peer,target,p,1032) == 1032); Sleep(3); CHECK(!commands(peer));
            int n = ThetisP2ReceiveReadAudio(audio,2048); CHECK(n >= 0);
            for (int i = 0; i < n; ++i)
            {
                CHECK(isfinite(audio[2*i]) && isfinite(audio[2*i+1]));
                if (skipped++ < 48000) continue;
                double v = audio[2*i]; if (frames && previous <= 0 && v > 0) ++crossings;
                previous = v; energy += v*v; ++frames;
            }
        }
        CHECK(frames > 16000 && fabs(sqrt(energy/frames) - .1/sqrt(2)) < .002);
        CHECK(fabs(crossings*48000.0/(frames-1) - 1000) < 10);
        CHECK(ThetisP2ReceiveGetState(s,24) == 24);
        CHECK(s[7] == 598 && s[8] == 598*126 && s[9] == 1 && s[10] == 2 && s[11] == 2 && s[12] == 1);
        CHECK(s[13] == 598 && s[14] == 598 && s[15] == 0 && s[17] == 0 && s[19] == 0 && s[21] == 0);
        CHECK(ThetisP2ReceiveClose() == 0); closed(); CHECK(commands(peer));
        cm_socket rebound = CM_INVALID_SOCKET; CHECK(cm_socket_open("127.0.0.1",target,&rebound,&unused) == 0); cm_socket_close(&rebound);
        printf("P1 mode=%d RMS=%g frames=%d, wrap/gap/late/bad/foreign/mic exclusion/STOP/rebind passed\n",cycle,sqrt(energy/frames),frames);
    }
    cm_socket_close(&peer); cm_socket_close(&stranger); destroy_impulse_cache();
    puts("PASS: native P1 signal, single owner, rollback, faults, active reconnect and zero TX"); return 0;
}
