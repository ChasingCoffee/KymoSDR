/* SPDX-License-Identifier: GPL-2.0-or-later
 * Offline RNet/socket owner. There is intentionally no send operation here.
 * A bounded receive wait and a cancellable timer share a manual-reset stop
 * event; both are joined before sockets, buffers or locks are released.
 */
#include "rnet.h"
#include "radio_init.h"
#include "cm_thread.h"
#include "cm_transport.h"
#include <string.h>

static volatile LONG command_busy;
static volatile LONG datagrams, received_bytes, oversized, errors, ticks;
static HANDLE stop_event;
static cm_thread reader, timer;
static int reader_started, timer_started, rnet_owned, opened;
#ifdef THETIS_TESTING
static int fault;
#define FAIL_AT(stage) (fault == (stage))
#else
#define FAIL_AT(stage) 0
#endif
static int enter(void) { return !InterlockedBitTestAndSet(&command_busy, 0); }
static void leave(void) { InterlockedBitTestAndReset(&command_busy, 0); }
static void receive_main(void *unused)
{
    (void)unused;
    while (WaitForSingleObject(stop_event, 0) == WAIT_TIMEOUT)
    {
        int count = cm_socket_receive_loopback(listenSock, prn->ReadBufp, 1444, 20);
        if (count >= 0)
        {
            InterlockedIncrement(&datagrams);
            /* One writer; atomic store keeps concurrent state reads defined. */
            LONG previous = InterlockedAnd(&received_bytes, -1);
            InterlockedExchange(&received_bytes, previous <= INT32_MAX - count ? previous + count : INT32_MAX);
        }
        else if (count == -2) InterlockedIncrement(&oversized);
        else if (count != -1)
        {
            InterlockedIncrement(&errors);
            SetEvent(stop_event);
            return;
        }
    }
}
static void timer_main(void *unused)
{
    (void)unused;
    /* Tests timer cancellation/ownership; this is NOT a radio keepalive. */
    while (WaitForSingleObject(stop_event, 50) == WAIT_TIMEOUT) InterlockedIncrement(&ticks);
}
static void close_owned(void)
{
    if (stop_event) SetEvent(stop_event);
    if (timer_started) cm_join_thread(timer);
    if (reader_started) cm_join_thread(reader);
    timer_started = reader_started = 0;
    if (stop_event) CloseHandle(stop_event);
    stop_event = NULL;
    if (rnet_owned)
    {
        DeInitMetisSockets();
        destroy_rnet();
    }
    rnet_owned = opened = 0;
    datagrams = received_bytes = oversized = errors = ticks = 0;
}
CM_API int ThetisTransportOpen(int abi, const char *remote, int remote_port,
    const char *local, int local_port, int protocol, int model, int relocate,
    cm_checkpoint checkpoint, void *context)
{
    uint32_t address;
    if (abi != 1 || cm_socket_address(remote, &address, 1) || cm_socket_address(local, &address, 1) ||
        remote_port < 1 || remote_port > 65518 || local_port < 0 || local_port > 65535 ||
        (protocol != USB && protocol != ETH) || model < 0 || model > HPSDRModel_ANAN_G2E ||
        (relocate != 0 && relocate != 1) || (protocol == USB && relocate)) return -1;
    if (!enter()) return -2;
    if (rnet_owned || prn || listenSock != CM_INVALID_SOCKET) { leave(); return -2; }
    int result = -3;
    for (int stage = 1; stage <= 5; ++stage)
    {
        if (stage == 1)
        {
            if (create_rnet_checked()) goto failed;
            rnet_owned = 1;
            prn->sendHighPriority = 0; // no legacy callbacks are installed
        }
        if (stage == 2 && nativeInitMetis((char *)remote, remote_port, (char *)local,
            local_port, protocol, model, relocate)) goto failed;
        if (stage == 3)
        {
            if (FAIL_AT(3) || !(stop_event = CreateEvent(NULL, TRUE, FALSE, NULL))) goto failed;
        }
        if (stage == 4)
        {
            if (FAIL_AT(4) || cm_try_start_thread(&reader, receive_main, NULL)) goto failed;
            reader_started = 1;
        }
        if (stage == 5)
        {
            if (FAIL_AT(5) || cm_try_start_thread(&timer, timer_main, NULL)) goto failed;
            timer_started = 1;
        }
        int rc = checkpoint ? checkpoint(stage, context) : 0;
        if (rc) { result = rc > 0 ? -4 : -5; goto failed; }
    }
    opened = 1;
    leave();
    return 0;
failed:
    close_owned();
    leave();
    return result;
}
CM_API int ThetisTransportClose(void)
{
    if (!enter()) return -2;
    close_owned();
    leave();
    return 0;
}
CM_API int ThetisTransportGetState(int32_t *values, int capacity)
{
    if (!values || capacity < 16) return -1;
    if (!enter()) return -2;
    int32_t state[] = {1, opened, radio_local_port,
        rnet_owned ? prn->base_outbound_port : 0, rnet_owned ? prn->p2_custom_port_base : 0,
        RadioProtocol, HPSDRModel, rnet_owned, reader_started + timer_started,
        InterlockedAnd(&datagrams, -1), InterlockedAnd(&received_bytes, -1),
        InterlockedAnd(&oversized, -1), InterlockedAnd(&errors, -1), InterlockedAnd(&ticks, -1), 1, 0};
    memcpy(values, state, sizeof(state));
    leave();
    return 16;
}
#ifdef THETIS_TESTING
CM_API int ThetisTransportTestFault(int stage)
{
    if (stage != 0 && stage != 3 && stage != 4 && stage != 5) return -1;
    if (!enter()) return -2;
    if (rnet_owned) { leave(); return -2; }
    fault = stage;
    leave();
    return 0;
}
#endif
