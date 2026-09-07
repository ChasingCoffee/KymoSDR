/* SPDX-License-Identifier: GPL-2.0-or-later
 * Loopback-only, receive-only P2 integration. Not the full legacy network loop.
 * One bounded packet/control worker owns the socket and never feeds mic/TX.
 */
#include "rnet.h"
#include "radio_init.h"
#include "cmcomm.h"
#include "cm_p2_receive.h"
#include "cm_receive_core.h"
#include "p2_rx_packet.h"

#define AUDIO_CAPACITY 16384
static volatile LONG command_busy;
static CRITICAL_SECTION state_lock;
static int lock_owned, core_owned, rnet_owned, socket_owned, worker_started, opened;
static HANDLE stop_event;
static cm_thread worker;
static int base_port, selected_ddc, sample_rate;
static uint32_t frequency_hz, high_sequence;
static int64_t state[24];
static double audio[2 * AUDIO_CAPACITY];
static int audio_read, audio_write, audio_count;
#ifdef THETIS_TESTING
static int fault;
#define FAIL_AT(s) (fault == (s))
#else
#define FAIL_AT(s) 0
#endif
extern void LoadRouterAll(void *, int, int, int, int, int *, int *, int *);
static int enter(void) { return !InterlockedBitTestAndSet(&command_busy, 0); }
static void leave(void) { InterlockedBitTestAndReset(&command_busy, 0); }
static uint64_t now_ms(void)
{
#ifdef _WIN32
    return GetTickCount64();
#else
    struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t);
    return (uint64_t)t.tv_sec * 1000 + (uint64_t)t.tv_nsec / 1000000;
#endif
}
static void count(int index, int64_t amount)
{ EnterCriticalSection(&state_lock); state[index] += amount; LeaveCriticalSection(&state_lock); }
static void observe_audio(int frames, const double *samples, int error)
{
    EnterCriticalSection(&state_lock);
    if (error) ++state[21];
    for (int i = 0; i < frames; ++i)
    {
        double left = samples[2 * i], right = samples[2 * i + 1];
        if (!isfinite(left) || !isfinite(right)) { ++state[21]; left = right = 0; }
        if (audio_count == AUDIO_CAPACITY)
        { audio_read = (audio_read + 1) % AUDIO_CAPACITY; --audio_count; ++state[19]; }
        audio[2 * audio_write] = left; audio[2 * audio_write + 1] = right;
        audio_write = (audio_write + 1) % AUDIO_CAPACITY; ++audio_count;
    }
    state[20] += frames;
    LeaveCriticalSection(&state_lock);
}
static int send_control(int offset, const unsigned char *packet, int length)
{
    int sent = cm_socket_send_loopback(listenSock, MetisAddr, base_port + offset, packet, length);
    count(sent == length ? 16 : 15, 1);
    return sent == length ? 0 : -1;
}
static int send_high(int run)
{
    unsigned char packet[1444];
    EnterCriticalSection(&state_lock); uint32_t frequency = frequency_hz; LeaveCriticalSection(&state_lock);
    p2_rx_high(packet, selected_ddc, frequency, run, high_sequence++);
    return send_control(3, packet, 1444);
}
static void receive_main(void *unused)
{
    (void)unused;
    uint64_t start = now_ms(), heartbeat = 0, setup = 0, last_iq = start;
    uint32_t last_sequence = 0;
    int have_sequence = 0;
    while (WaitForSingleObject(stop_event, 0) == WAIT_TIMEOUT)
    {
        uint64_t now = now_ms();
        if (now >= setup)
        {
            unsigned char general[60], rx[1444];
            p2_rx_general(general, base_port); p2_rx_receivers(rx, selected_ddc, sample_rate);
            if (send_control(0, general, 60) || send_control(1, rx, 1444)) break;
            setup = now + 1000;
        }
        if (now >= heartbeat)
        { if (send_high(1)) break; heartbeat = now + 100; }
        uint32_t source; int port;
        int length = cm_socket_receive_peer(listenSock, prn->ReadBufp, 1444, 20, &source, &port);
        if (length == -1) { /* bounded wait */ }
        else if (length == -2) count(11, 1);
        else if (length == -4) count(12, 1);
        else if (length < 0) { count(15, 1); break; }
        else if (source != MetisAddr) count(12, 1);
        else if (port == base_port + 1)
        { count(length == 60 ? 14 : 11, 1); }
        else if (port == base_port + 2)
        { count(length == 132 ? 13 : 11, 1); } // mic deliberately discarded, never Inbound(TX)
        else if (port != base_port + 11 + selected_ddc) count(12, 1);
        else
        {
            uint32_t sequence;
            int samples = p2_rx_decode(prn->ReadBufp, length, prn->RxReadBufp, 476, &sequence);
            if (samples < 0) count(11, 1);
            else
            {
                uint32_t distance = have_sequence ? sequence - last_sequence - 1u : 0;
                if (have_sequence && distance >= 0x80000000u) count(10, 1);
                else
                {
                    count(9, distance); last_sequence = sequence; have_sequence = 1;
                    last_iq = now_ms(); count(7, 1); count(8, samples);
                    xrouter(NULL, 0, selected_ddc, samples, prn->RxReadBufp);
                }
            }
        }
        // Missing/stalled peer cannot leave an endless heartbeat sender behind.
        if (now_ms() - last_iq >= 3000) { count(15, 1); break; }
    }
    SetEvent(stop_event);
    send_high(0);
}
static void close_owned(void)
{
    if (stop_event) SetEvent(stop_event);
    if (worker_started) cm_join_thread(worker);
    worker_started = 0;
    if (stop_event) CloseHandle(stop_event);
    stop_event = NULL;
    if (socket_owned) DeInitMetisSockets();
    socket_owned = 0;
    if (rnet_owned) destroy_rnet();
    rnet_owned = 0;
    if (core_owned) cm_receive_core_close(); // joins CM workers before observer state is freed
    core_owned = 0;
    if (lock_owned) DeleteCriticalSection(&state_lock);
    lock_owned = opened = 0;
    memset(state, 0, sizeof(state)); audio_count = audio_read = audio_write = 0;
}
CM_API int ThetisP2ReceiveOpen(int abi, const char *remote, int base, int ddc, int rate,
    int frequency, cm_checkpoint checkpoint, void *context)
{
    uint32_t address;
    if (abi != 1 || cm_socket_address(remote, &address, 1) || base < 1024 || base > 65515 ||
        ddc < 0 || ddc > 9 || (rate != 48000 && rate != 96000 && rate != 192000 && rate != 384000) ||
        frequency < 0 || frequency > 61440000) return -1;
    if (!enter()) return -2;
    if (opened || core_owned || prn || listenSock != CM_INVALID_SOCKET) { leave(); return -2; }
    int result = -3;
    memset(state, 0, sizeof(state));
    audio_read = audio_write = audio_count = 0;
    InitializeCriticalSectionAndSpinCount(&state_lock, 2500); lock_owned = 1;
    base_port = base; selected_ddc = ddc; sample_rate = rate; frequency_hz = (uint32_t)frequency; high_sequence = 0;
    for (int stage = 1; stage <= 5; ++stage)
    {
        if (stage == 1)
        {
            result = cm_receive_core_open(rate, observe_audio);
            if (result) goto failed;
            core_owned = 1; result = -3;
            int streams[10], functions[10] = {0}, calls[10] = {0};
            for (int i = 0; i < 10; ++i) streams[i] = 1; // no divide-by-zero for inactive DDC routes
            functions[ddc] = 1; // selected DDC -> Inbound(CM RX0)
            LoadRouterAll(NULL, 0, 10, 1, 1, streams, functions, calls);
            SetRXAMode(0, 1); SetRXABandpassFreqs(0, 300, 3000);
            SetRXAAGCMode(0, 0); SetRXAAGCFixed(0, 0);
            SetRXAPanelGain1(0, 1); // deterministic unity gain, not the legacy default x4
            SetChannelState(0, 1, 0); // RX0/sub0 only; TX and all other channels remain off
        }
        if (stage == 2)
        {
            if (create_rnet_checked()) goto failed;
            rnet_owned = 1; prn->sendHighPriority = 0;
        }
        if (stage == 3)
        {
            if (nativeInitMetis((char *)remote, base, "127.0.0.1", 0, ETH, HPSDRModel_ANAN_G2, 1)) goto failed;
            socket_owned = 1;
        }
        if (stage == 4 && (FAIL_AT(4) || !(stop_event = CreateEvent(NULL, TRUE, FALSE, NULL)))) goto failed;
        if (stage == 5)
        {
            if (FAIL_AT(5) || cm_try_start_thread(&worker, receive_main, NULL)) goto failed;
            worker_started = 1;
        }
        int rc = checkpoint ? checkpoint(stage, context) : 0;
        if (rc) { result = rc > 0 ? -4 : -5; goto failed; }
    }
    opened = 1; leave(); return 0;
failed:
    close_owned(); leave(); return result;
}
CM_API int ThetisP2ReceiveClose(void)
{ if (!enter()) return -2; close_owned(); leave(); return 0; }
CM_API int ThetisP2ReceiveTune(int frequency)
{
    if (frequency < 0 || frequency > 61440000) return -1;
    if (!enter()) return -2;
    if (!opened || WaitForSingleObject(stop_event, 0) != WAIT_TIMEOUT) { leave(); return -3; }
    EnterCriticalSection(&state_lock); frequency_hz = (uint32_t)frequency; LeaveCriticalSection(&state_lock);
    leave(); return 0;
}
CM_API int ThetisP2ReceiveGetState(int64_t *values, int capacity)
{
    if (!values || capacity < 24) return -1;
    if (!enter()) return -2;
    if (lock_owned) EnterCriticalSection(&state_lock);
    memcpy(values, state, sizeof(state));
    values[0] = 1; values[1] = opened; values[2] = socket_owned ? radio_local_port : 0;
    values[3] = opened ? base_port : 0; values[4] = opened ? selected_ddc : 0;
    values[5] = opened ? sample_rate : 0; values[6] = worker_started;
    values[17] = core_owned ? InterlockedAnd(&pcm->pcbuff[0]->overruns, -1) : 0;
    values[18] = audio_count; values[22] = 1; values[23] = 0;
    if (lock_owned) LeaveCriticalSection(&state_lock);
    leave(); return 24;
}
CM_API int ThetisP2ReceiveReadAudio(double *samples, int capacity_frames)
{
    if (!samples || capacity_frames < 1 || capacity_frames > AUDIO_CAPACITY) return -1;
    if (!enter()) return -2;
    if (!opened) { leave(); return -3; }
    EnterCriticalSection(&state_lock);
    int count = audio_count < capacity_frames ? audio_count : capacity_frames;
    for (int i = 0; i < count; ++i)
    {
        samples[2 * i] = audio[2 * audio_read]; samples[2 * i + 1] = audio[2 * audio_read + 1];
        audio_read = (audio_read + 1) % AUDIO_CAPACITY;
    }
    audio_count -= count;
    LeaveCriticalSection(&state_lock); leave(); return count;
}
#ifdef THETIS_TESTING
CM_API int ThetisP2ReceiveTestFault(int stage)
{
    if (stage != 0 && stage != 4 && stage != 5) return -1;
    if (!enter()) return -2;
    if (opened || core_owned) { leave(); return -2; }
    fault = stage; leave(); return 0;
}
#endif
