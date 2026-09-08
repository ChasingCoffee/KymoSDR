/* SPDX-License-Identifier: GPL-2.0-or-later
 * Shared loopback-only P1/P2 receive owner. P2-prefixed pull/control exports
 * are retained for ABI compatibility and operate on the single active owner.
 * One bounded packet/control worker owns the socket and never feeds mic/TX.
 */
#include "rnet.h"
#include "radio_init.h"
#include "cmcomm.h"
#include "cm_p2_receive.h"
#include "cm_receive_core.h"
#include "cm_spectrum.h"
#include "p2_rx_packet.h"
#include "p1_rx_packet.h"

#define AUDIO_CAPACITY 16384
static volatile LONG command_busy;
static CRITICAL_SECTION state_lock;
static int lock_owned, core_owned, rnet_owned, socket_owned, worker_started, opened;
static HANDLE stop_event;
static cm_thread worker;
static int base_port, selected_ddc, sample_rate, protocol_one;
static uint32_t frequency_hz, high_sequence;
static int64_t state[24];
static double audio[2 * AUDIO_CAPACITY];
static int audio_read, audio_write, audio_count;
static double spectrum_iq[2 * CM_SPECTRUM_FFT_SIZE];
static float spectrum_pixels[CM_SPECTRUM_PIXELS];
static int64_t spectrum_meta[12], spectrum_sequence, spectrum_generation, spectrum_coalesced;
static int spectrum_count, spectrum_skip, spectrum_ready;
static uint64_t spectrum_resume_ms;
static int rx_mode, filter_low, filter_high;
static int64_t controls_generation;
#ifdef THETIS_TESTING
static int fault;
#define FAIL_AT(s) (fault == (s))
#else
#define FAIL_AT(s) 0
#endif
extern void LoadRouterAll(void *, int, int, int, int, int *, int *, int *);
extern void RXASetPassband(int, double, double);
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
static void observe_iq(int samples, const double *iq)
{
    EnterCriticalSection(&state_lock);
    if (now_ms() < spectrum_resume_ms) { LeaveCriticalSection(&state_lock); return; }
    for (int i = 0; i < samples; ++i)
    {
        if (spectrum_skip) { --spectrum_skip; continue; }
        spectrum_iq[2 * spectrum_count] = iq[2 * i];
        spectrum_iq[2 * spectrum_count + 1] = iq[2 * i + 1];
        if (++spectrum_count != CM_SPECTRUM_FFT_SIZE) continue;
        int error = cm_spectrum_transform(spectrum_iq, spectrum_pixels);
        for (int p = 0; !error && p < CM_SPECTRUM_PIXELS; ++p)
            if (!isfinite(spectrum_pixels[p])) error = -1;
        if (error) { ++state[21]; spectrum_ready = 0; }
        else
        {
            if (spectrum_ready) ++spectrum_coalesced;
            spectrum_meta[0] = 1; spectrum_meta[1] = ++spectrum_sequence;
            spectrum_meta[2] = spectrum_generation; spectrum_meta[3] = (int64_t)now_ms();
            spectrum_meta[4] = frequency_hz; spectrum_meta[5] = selected_ddc;
            spectrum_meta[6] = sample_rate; spectrum_meta[7] = CM_SPECTRUM_FFT_SIZE;
            spectrum_meta[8] = CM_SPECTRUM_PIXELS; spectrum_meta[9] = spectrum_coalesced;
            spectrum_meta[10] = state[9];
            spectrum_meta[11] = InterlockedAnd(&pcm->pcbuff[0]->overruns, -1);
            spectrum_ready = 1;
        }
        spectrum_count = 0;
        int hop = sample_rate / 20; // <=20 frames per input-sample second, no overlap
        spectrum_skip = hop > CM_SPECTRUM_FFT_SIZE ? hop - CM_SPECTRUM_FFT_SIZE : 0;
    }
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
    EnterCriticalSection(&state_lock);
    uint32_t frequency = frequency_hz; int64_t generation = spectrum_generation;
    LeaveCriticalSection(&state_lock);
    int result;
    if (protocol_one)
    {
        if (run) { p1_rx_control(packet, frequency, high_sequence++); result = send_control(0, packet, 1032); }
        else { p1_rx_run(packet, 0); result = send_control(0, packet, 64); }
    }
    else
    {
        p2_rx_high(packet, selected_ddc, frequency, run, high_sequence++);
        result = send_control(3, packet, 1444);
    }
    EnterCriticalSection(&state_lock);
    // There is no sample-accurate P2 tuning acknowledgement. Wait after the
    // matching command was sent; metadata remains REQUESTED center frequency.
    if (!result && run && generation == spectrum_generation && spectrum_resume_ms == UINT64_MAX)
        spectrum_resume_ms = now_ms() + 250;
    LeaveCriticalSection(&state_lock);
    return result;
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
        if (protocol_one && !have_sequence && now >= setup)
        {
            unsigned char config[1032]; p1_rx_setup(config, high_sequence++);
            unsigned char run[64]; p1_rx_run(run, 1);
            if (send_control(0, config, 1032) || send_high(1) || send_control(0, run, 64)) break;
            setup = now + 250; // retry START only until first IQ
        }
        if (!protocol_one && now >= setup)
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
        else if (!protocol_one && port == base_port + 1)
        { count(length == 60 ? 14 : 11, 1); }
        else if (!protocol_one && port == base_port + 2)
        { count(length == 132 ? 13 : 11, 1); } // mic deliberately discarded, never Inbound(TX)
        else if (port != base_port + (protocol_one ? 0 : 11 + selected_ddc)) count(12, 1);
        else
        {
            uint32_t sequence;
            int samples = protocol_one ? p1_rx_decode(prn->ReadBufp, length, prn->RxReadBufp, 476, &sequence)
                : p2_rx_decode(prn->ReadBufp, length, prn->RxReadBufp, 476, &sequence);
            if (samples < 0) count(11, 1);
            else
            {
                uint32_t distance = have_sequence ? sequence - last_sequence - 1u : 0;
                if (have_sequence && distance >= 0x80000000u) count(10, 1);
                else
                {
                    last_sequence = sequence; have_sequence = 1; last_iq = now_ms();
                    EnterCriticalSection(&state_lock);
                    state[9] += distance; ++state[7]; state[8] += samples;
                    if (protocol_one) { ++state[13]; ++state[14]; } // embedded mic/status discarded
                    LeaveCriticalSection(&state_lock);
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
    spectrum_ready = spectrum_count = spectrum_skip = 0;
}
static int valid_controls(int mode, int low, int high)
{ return (mode == 0 || mode == 1) && low >= 0 && high <= 12000 && high > low && high - low >= 100; }
static void apply_controls(int mode, int low, int high)
{
    // Caller owns the lifecycle command gate. CM -> DSP -> state is the same
    // lock order as the receive consumer; never wait for DSP while holding state.
    EnterCriticalSection(&pcm->update[0]);
    EnterCriticalSection(&ch[0].csDSP);
    SetRXAMode(0, mode);
    // Our P2/spectrum contract is I+jQ with exp(+j*w*t) at positive RF offset.
    // WDSP fir_bandpass builds exp(-j*w*t) coefficients (fir.c), so negate and
    // reverse RF edges at this boundary. Never silently mirror the spectrum.
    RXASetPassband(0, mode == 1 ? -high : low, mode == 1 ? -low : high);
    LeaveCriticalSection(&ch[0].csDSP);
    EnterCriticalSection(&state_lock);
    audio_read = audio_write = audio_count = 0; // explicit transition discard, not an overrun
    LeaveCriticalSection(&state_lock);
    LeaveCriticalSection(&pcm->update[0]);
    rx_mode = mode; filter_low = low; filter_high = high;
}
CM_API int ThetisP2ReceiveControlsAbi(void) { return 1; }
CM_API int ThetisP2ReceiveOpen(int abi, const char *remote, int base, int ddc, int rate,
    int frequency, cm_checkpoint checkpoint, void *context)
{ return ThetisP2ReceiveOpenWithControls(abi, remote, base, ddc, rate, frequency, 1, 300, 3000, checkpoint, context); }
CM_API int ThetisP2ReceiveOpenWithControls(int abi, const char *remote, int base, int ddc, int rate,
    int frequency, int mode, int low, int high, cm_checkpoint checkpoint, void *context)
{ return ThetisReceiveOpenWithControls(abi, 2, remote, base, ddc, rate, frequency, mode, low, high, checkpoint, context); }
CM_API int ThetisReceiveProtocolAbi(void) { return 1; }
CM_API int ThetisReceiveOpenWithControls(int abi, int protocol, const char *remote, int base, int ddc, int rate,
    int frequency, int mode, int low, int high, cm_checkpoint checkpoint, void *context)
{
    uint32_t address;
    if (abi != 1 || (protocol != 1 && protocol != 2) || (protocol == 1 && (ddc != 0 || rate != 48000)) ||
        cm_socket_address(remote, &address, 1) || base < 1024 || base > (protocol == 1 ? 65535 : 65515) ||
        ddc < 0 || ddc > 9 || (rate != 48000 && rate != 96000 && rate != 192000 && rate != 384000) ||
        frequency < 0 || frequency > 61440000 || !valid_controls(mode, low, high)) return -1;
    if (!enter()) return -2;
    if (opened || core_owned || prn || listenSock != CM_INVALID_SOCKET) { leave(); return -2; }
    int result = -3;
    memset(state, 0, sizeof(state));
    audio_read = audio_write = audio_count = 0;
    spectrum_ready = spectrum_count = spectrum_skip = 0;
    spectrum_sequence = spectrum_coalesced = 0; spectrum_generation = 1;
    spectrum_resume_ms = UINT64_MAX;
    controls_generation = 1;
    InitializeCriticalSectionAndSpinCount(&state_lock, 2500); lock_owned = 1;
    base_port = base; selected_ddc = ddc; sample_rate = rate; frequency_hz = (uint32_t)frequency; high_sequence = 0;
    protocol_one = protocol == 1;
    for (int stage = 1; stage <= 5; ++stage)
    {
        if (stage == 1)
        {
            result = cm_receive_core_open(rate, observe_audio, observe_iq);
            if (result) goto failed;
            core_owned = 1; result = -3;
            cm_spectrum_configure(rate);
            int streams[10], functions[10] = {0}, calls[10] = {0};
            for (int i = 0; i < 10; ++i) streams[i] = 1; // no divide-by-zero for inactive DDC routes
            functions[ddc] = 1; // selected DDC -> Inbound(CM RX0)
            LoadRouterAll(NULL, 0, 10, 1, 1, streams, functions, calls);
            apply_controls(mode, low, high);
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
            if (nativeInitMetis((char *)remote, base, "127.0.0.1", 0, protocol_one ? USB : ETH,
                protocol_one ? HPSDRModel_HPSDR : HPSDRModel_ANAN_G2, protocol_one ? 0 : 1)) goto failed;
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
    EnterCriticalSection(&state_lock);
    frequency_hz = (uint32_t)frequency; ++spectrum_generation;
    spectrum_ready = spectrum_count = spectrum_skip = 0;
    spectrum_resume_ms = UINT64_MAX;
    LeaveCriticalSection(&state_lock);
    leave(); return 0;
}
CM_API int ThetisP2ReceiveSetControls(int abi, int mode, int low, int high)
{
    if (abi != 1 || !valid_controls(mode, low, high)) return -1;
    if (!enter()) return -2;
    if (!opened || WaitForSingleObject(stop_event, 0) != WAIT_TIMEOUT) { leave(); return -3; }
    if (mode != rx_mode || low != filter_low || high != filter_high)
    { apply_controls(mode, low, high); ++controls_generation; }
    leave(); return 0;
}
CM_API int ThetisP2ReceiveGetControls(int64_t *values, int capacity)
{
    if (!values || capacity < 8) return -1;
    if (!enter()) return -2;
    int64_t result[8] = {1, 0, 0, 0, 0, 0, 0, 0};
    if (opened)
    {
        result[1] = 1; result[2] = rx_mode; result[3] = filter_low; result[4] = filter_high;
        result[5] = rx_mode == 1 ? filter_low : -filter_high;
        result[6] = rx_mode == 1 ? filter_high : -filter_low;
        result[7] = controls_generation;
    }
    memcpy(values, result, sizeof(result)); leave(); return 8;
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
CM_API int ThetisP2ReceiveSpectrumAbi(void) { return 1; }
CM_API int ThetisP2ReceiveReadSpectrum(int abi, float *pixels, int capacity, int64_t *metadata, int metadata_capacity)
{
    if (abi != 1 || !pixels || capacity < CM_SPECTRUM_PIXELS || !metadata || metadata_capacity < 12) return -1;
    if (!enter()) return -2;
    if (!opened) { leave(); return -3; }
    EnterCriticalSection(&state_lock);
    int count = 0;
    if (spectrum_ready)
    {
        memcpy(pixels, spectrum_pixels, sizeof(spectrum_pixels));
        memcpy(metadata, spectrum_meta, sizeof(spectrum_meta));
        spectrum_ready = 0; count = CM_SPECTRUM_PIXELS;
    }
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
