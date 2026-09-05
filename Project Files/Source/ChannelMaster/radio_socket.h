/* SPDX-License-Identifier: GPL-2.0-or-later
 * Private IPv4 UDP primitives. System socket headers stay out of CM typedefs.
 */
#ifndef THETIS_RADIO_SOCKET_H
#define THETIS_RADIO_SOCKET_H
#include <stdint.h>
#ifdef _WIN32
typedef uintptr_t cm_socket;
#define CM_INVALID_SOCKET UINTPTR_MAX
#else
typedef int cm_socket;
#define CM_INVALID_SOCKET (-1)
#endif
int cm_socket_address(const char *text, uint32_t *network_order, int loopback_only);
/* Each successful open owns one WSA reference on Windows; close balances it. */
int cm_socket_open(const char *local, int port, cm_socket *socket_out, int *bound_port);
void cm_socket_close(cm_socket *socket);
/* Positive/zero = datagram length, -1 timeout/interrupted, -2 oversize,
 * -3 OS error, -4 non-loopback source. Never sends anything. */
int cm_socket_receive_loopback(cm_socket socket, void *buffer, int capacity, int timeout_ms);
#endif
