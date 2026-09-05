/*
 * radio_init.c — lifecycle extracted from network.c
 * Copyright (C) 2015-2020 Doug Wigley (W5WC)
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 */
#include "rnet.h"
#include "radio_init.h"
#include "radio_ports.h"
#ifdef THETIS_CM_HEADLESS
#define RADIO_API
#else
#define RADIO_API __declspec(dllexport)
#endif

cm_socket listenSock = CM_INVALID_SOCKET;
uint32_t MetisAddr;
int radio_local_port;
enum _HPSDRModel HPSDRModel;
enum _RadioProtocol RadioProtocol;

/* Caller must stop/join socket users first. This operation does not send a
 * radio stop command; portable lifecycle tests never start radio streaming. */
RADIO_API void DeInitMetisSockets(void)
{
    cm_socket_close(&listenSock);
    radio_local_port = 0;
#ifdef THETIS_CM_HEADLESS
    MetisAddr = 0;
    HPSDRModel = HPSDRModel_HPSDR;
    RadioProtocol = USB;
    if (prn)
    {
        prn->base_outbound_port = 1024;
        prn->p2_custom_port_base = 1025;
    }
#endif
}
RADIO_API int nativeInitMetis(char *netaddr, int port, char *localaddr, int localport,
    int protocol, int model_id, int relocate)
{
    uint32_t remote, local;
#ifdef THETIS_CM_HEADLESS
    /* Deliberate native-side hardware safety gate, not merely CLI validation. */
    const int loopback_only = 1;
#else
    const int loopback_only = 0;
#endif
    if (!prn || port < 1 || port > (protocol == ETH ? 65518 : 65535) || localport < 0 || localport > 65535 ||
        (protocol != USB && protocol != ETH) || model_id < 0 || model_id > HPSDRModel_ANAN_G2E ||
        (relocate != 0 && relocate != 1) ||
        cm_socket_address(netaddr, &remote, loopback_only) ||
        cm_socket_address(localaddr, &local, loopback_only) || remote == 0 || remote == UINT32_MAX) return -1;
    if (listenSock != CM_INVALID_SOCKET) return -2;
    int bound_port;
    if (cm_socket_open(localaddr, localport, &listenSock, &bound_port)) return -3;
    /* Publish configuration only after bind succeeds. No ARP preflight: the old
     * Windows SendARP result was unused and initialization must not send traffic. */
    MetisAddr = remote;
    radio_local_port = bound_port;
    RadioProtocol = (enum _RadioProtocol)protocol;
    HPSDRModel = (enum _HPSDRModel)model_id;
    prn->base_outbound_port = port;
    prn->p2_custom_port_base = protocol == ETH ? cm_p2_port_base(port, relocate) : 1025;
    return 0;
}
