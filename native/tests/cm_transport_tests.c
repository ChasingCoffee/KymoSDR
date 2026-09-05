/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "cm_transport.h"
#include "radio_socket.h"
#include <stdio.h>
#include <stdlib.h>
#ifdef _WIN32
#include <Windows.h>
#else
#include <time.h>
#endif
extern int test_peer_send(cm_socket, int, const void *, int);
extern int test_process_threads(void);
extern int test_process_descriptors(void);
extern uint64_t test_resident_bytes(void);
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed: %s at %d\n", #x, __LINE__); exit(1); } } while (0)
static void pause_ms(void)
{
#ifdef _WIN32
    Sleep(10);
#else
    struct timespec delay = {0, 10000000}; nanosleep(&delay, NULL);
#endif
}
static int open_probe(int local_port, cm_checkpoint callback, void *context)
{ return ThetisTransportOpen(1, "127.0.0.1", 5000, "127.0.0.1", local_port, 1, 11, 1, callback, context); }
static void closed(void)
{
    int32_t state[17] = {0}; state[16] = 6789;
    CHECK(ThetisTransportGetState(state, 16) == 16);
    CHECK(state[0] == 1 && state[14] == 1 && state[16] == 6789);
    for (int i = 1; i < 14; ++i) CHECK(state[i] == 0);
    CHECK(state[15] == 0);
}
static int checkpoint(int stage, void *context)
{
    int target = *(int *)context;
    CHECK(ThetisTransportClose() == -2); // no callback reentrancy deadlock
    CHECK(open_probe(0, NULL, NULL) == -2);
    return stage == abs(target) ? (target < 0 ? -1 : 1) : 0;
}
int main(void)
{
    CHECK(ThetisTransportGetState(NULL, 16) == -1);
    int32_t state[16];
    CHECK(ThetisTransportGetState(state, 15) == -1);
    CHECK(ThetisTransportOpen(1, "192.0.2.1", 1024, "127.0.0.1", 0, 1, 11, 0, NULL, NULL) == -1);
    CHECK(ThetisTransportOpen(1, "127.0.0.1", 1024, "0.0.0.0", 0, 1, 11, 0, NULL, NULL) == -1);
    CHECK(ThetisTransportOpen(2, "127.0.0.1", 1024, "127.0.0.1", 0, 1, 11, 0, NULL, NULL) == -1);
    CHECK(ThetisTransportClose() == 0); closed();
    for (int target = -5; target <= 5; ++target)
    {
        if (!target) continue;
        CHECK(open_probe(0, checkpoint, &target) == (target < 0 ? -5 : -4));
        closed();
    }
    for (int fault = 3; fault <= 5; ++fault)
    {
        CHECK(ThetisTransportTestFault(fault) == 0);
        CHECK(open_probe(0, NULL, NULL) == -3);
        closed();
    }
    CHECK(ThetisTransportTestFault(0) == 0);
    cm_socket peer = CM_INVALID_SOCKET;
    int peer_port;
    CHECK(cm_socket_open("127.0.0.1", 0, &peer, &peer_port) == 0);
    CHECK(open_probe(peer_port, NULL, NULL) == -3); // exclusive bind conflict
    closed();
    cm_socket_close(&peer);
    int warm_threads = 0, warm_descriptors = 0;
    uint64_t warm_bytes = 0;
    char payload[2048] = {0};
    for (int cycle = 0; cycle < 110; ++cycle) // ten warmups, 100 measured cycles
    {
        CHECK(cm_socket_open("127.0.0.1", 0, &peer, &peer_port) == 0);
        for (int retry = 0; peer_port > 65518; ++retry)
        {
            CHECK(retry < 100);
            cm_socket_close(&peer);
            CHECK(cm_socket_open("127.0.0.1", 0, &peer, &peer_port) == 0);
        }
        CHECK(ThetisTransportOpen(1, "127.0.0.1", peer_port, "127.0.0.1", 0, 1, 11, 0, NULL, NULL) == 0);
        CHECK(open_probe(0, NULL, NULL) == -2);
        CHECK(ThetisTransportGetState(state, 16) == 16);
        int port = state[2];
        CHECK(port > 0 && state[3] == peer_port && state[4] == 1025 && state[5] == 1 && state[6] == 11 && state[7] == 1 && state[8] == 2);
        CHECK(test_peer_send(peer, port, payload, 32) == 32);
        CHECK(test_peer_send(peer, port, payload, 1444) == 1444);
        CHECK(test_peer_send(peer, port, payload, 0) == 0);
        CHECK(test_peer_send(peer, port, payload, 2048) == 2048);
        int attempts;
        for (attempts = 0; attempts < 200; ++attempts)
        {
            CHECK(ThetisTransportGetState(state, 16) == 16);
            if (state[9] == 3 && state[11] == 1 && state[13] > 0) break;
            pause_ms();
        }
        CHECK(attempts < 200 && state[10] == 1476 && state[12] == 0 && state[15] == 0);
        CHECK(cm_socket_receive_loopback(peer, payload, sizeof(payload), 0) == -1); // probe never replies, including timer ticks
        // Leave a backlog while closing; owners must join before buffers go away.
        for (int i = 0; i < 16; ++i) CHECK(test_peer_send(peer, port, payload, 32) == 32);
        CHECK(ThetisTransportClose() == 0);
        CHECK(ThetisTransportClose() == 0);
        closed();
        cm_socket reclaimed = CM_INVALID_SOCKET;
        int rebound;
        CHECK(cm_socket_open("127.0.0.1", port, &reclaimed, &rebound) == 0 && rebound == port);
        cm_socket_close(&reclaimed); cm_socket_close(&peer);
        if (cycle == 9)
        {
            warm_threads = test_process_threads();
            warm_descriptors = test_process_descriptors();
            warm_bytes = test_resident_bytes();
            CHECK(warm_threads > 0 && warm_descriptors > 0);
        }
    }
    int final_threads = test_process_threads(), final_descriptors = test_process_descriptors();
    printf("Threads warm/final: %d/%d; descriptors or Windows handles: %d/%d; resident bytes: %llu/%llu\n",
        warm_threads, final_threads, warm_descriptors, final_descriptors,
        (unsigned long long)warm_bytes, (unsigned long long)test_resident_bytes());
    CHECK(final_threads > 0 && final_threads <= warm_threads);
    CHECK(final_descriptors > 0 && final_descriptors <= warm_descriptors);
    puts("PASS: 100 loopback lifecycles; ten startup rollback checkpoints; partial event/worker failure; port conflict/rebind; datagram limits; no outgoing packets");
    return 0;
}
