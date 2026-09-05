/* Test-only UDP sender. Destination is unconditionally IPv4 loopback. */
#ifdef _WIN32
#include <winsock2.h>
#else
#include <sys/socket.h>
#include <arpa/inet.h>
#endif
#include "radio_socket.h"
int test_peer_send(cm_socket peer, int port, const void *data, int length)
{
    struct sockaddr_in target = {0};
    target.sin_family = AF_INET;
    target.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    target.sin_port = htons((uint16_t)port);
    return (int)sendto(peer, data, length, 0, (struct sockaddr *)&target, sizeof(target));
}
