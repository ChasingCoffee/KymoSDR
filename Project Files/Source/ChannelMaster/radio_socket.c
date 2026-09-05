/*
 * radio_socket.c — lifecycle extracted from network.c
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
#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
typedef int socklen_t;
#else
#include <sys/socket.h>
#include <sys/time.h>
#include <arpa/inet.h>
#include <poll.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#endif
#include <string.h>
#include "radio_socket.h"

int cm_socket_address(const char *text, uint32_t *address, int loopback_only)
{
    struct in_addr value;
    if (!text || !address || inet_pton(AF_INET, text, &value) != 1) return -1;
    uint32_t host = ntohl(value.s_addr);
    if (loopback_only && (host >> 24) != 127) return -1;
    *address = value.s_addr;
    return 0;
}
void cm_socket_close(cm_socket *socket)
{
    if (*socket == CM_INVALID_SOCKET) return;
#ifdef _WIN32
    closesocket(*socket);
    WSACleanup();
#else
    close(*socket);
#endif
    *socket = CM_INVALID_SOCKET;
}
int cm_socket_open(const char *local, int port, cm_socket *socket_out, int *bound_port)
{
    struct sockaddr_in endpoint = {0};
    uint32_t address;
    if (!socket_out || *socket_out != CM_INVALID_SOCKET || !bound_port ||
        port < 0 || port > 65535 || cm_socket_address(local, &address, 0)) return -1;
    *bound_port = 0;
#ifdef _WIN32
    WSADATA data;
    if (WSAStartup(MAKEWORD(2, 2), &data)) return -1;
#endif
    cm_socket sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock == CM_INVALID_SOCKET)
    {
#ifdef _WIN32
        WSACleanup();
#endif
        return -1;
    }
    /* Preserve the inherited 1,024,000-byte buffer requests and 500ms timeouts.
     * Kernel clamping/doubling of buffer sizes is platform policy. */
    int bytes = 0xfa000;
#ifdef _WIN32
    DWORD timeout = 500;
    int exclusive = 1;
    if (setsockopt(sock, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char *)&exclusive, sizeof(exclusive))) goto failed;
#else
    struct timeval timeout = {0, 500000};
    int flags = fcntl(sock, F_GETFD);
    if (flags < 0 || fcntl(sock, F_SETFD, flags | FD_CLOEXEC)) goto failed;
#endif
    if (setsockopt(sock, SOL_SOCKET, SO_SNDBUF, (const char *)&bytes, sizeof(bytes)) ||
        setsockopt(sock, SOL_SOCKET, SO_RCVBUF, (const char *)&bytes, sizeof(bytes)) ||
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof(timeout)) ||
        setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char *)&timeout, sizeof(timeout))) goto failed;
    endpoint.sin_family = AF_INET;
    endpoint.sin_addr.s_addr = address;
    endpoint.sin_port = htons((uint16_t)port);
    if (bind(sock, (struct sockaddr *)&endpoint, sizeof(endpoint))) goto failed;
    socklen_t length = sizeof(endpoint);
    if (getsockname(sock, (struct sockaddr *)&endpoint, &length)) goto failed;
    *bound_port = ntohs(endpoint.sin_port);
    *socket_out = sock;
    return 0;
failed:
    cm_socket_close(&sock);
    return -1;
}
int cm_socket_receive_loopback(cm_socket sock, void *buffer, int capacity, int timeout_ms)
{
    if (!buffer || capacity < 1 || timeout_ms < 0) return -3;
#ifdef _WIN32
    WSAPOLLFD pollfd = {sock, POLLRDNORM, 0};
    int ready = WSAPoll(&pollfd, 1, timeout_ms);
    if (ready == SOCKET_ERROR) return WSAGetLastError() == WSAEINTR ? -1 : -3;
#else
    struct pollfd pollfd = {sock, POLLIN, 0};
    int ready = poll(&pollfd, 1, timeout_ms);
    if (ready < 0) return errno == EINTR ? -1 : -3;
#endif
    if (!ready) return -1;
    if (pollfd.revents & (POLLERR | POLLHUP | POLLNVAL)) return -3;
    struct sockaddr_in source = {0};
    socklen_t size = sizeof(source);
#ifdef _WIN32
    int count = recvfrom(sock, buffer, capacity, 0, (struct sockaddr *)&source, &size);
    if (count == SOCKET_ERROR)
    {
        int error = WSAGetLastError();
        if (error == WSAEMSGSIZE) return -2;
        return error == WSAETIMEDOUT || error == WSAEWOULDBLOCK || error == WSAEINTR ? -1 : -3;
    }
#else
    struct iovec iov = {buffer, (size_t)capacity};
    struct msghdr message = {0};
    message.msg_name = &source; message.msg_namelen = size;
    message.msg_iov = &iov; message.msg_iovlen = 1;
    int count = (int)recvmsg(sock, &message, 0);
    if (count < 0) return errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR ? -1 : -3;
    if (message.msg_flags & MSG_TRUNC) return -2;
#endif
    if ((ntohl(source.sin_addr.s_addr) >> 24) != 127) return -4;
    return count;
}
