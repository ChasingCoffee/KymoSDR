/* SPDX-License-Identifier: GPL-2.0-or-later
 * Confinement adapter for the pinned, UNMODIFIED pihpsdr sources. POSIX only.
 * Socket wrappers change binding/security, not EP2/EP6 or signal generation.
 * P2 is forcibly disabled by the fixed -P1 invocation; its linked code is idle.
 */
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static int fixture_port;
static int fixture_bind(int fd, const struct sockaddr *address, socklen_t length)
{
    if (length != sizeof(struct sockaddr_in) || address->sa_family != AF_INET) { errno = EACCES; return -1; }
    struct sockaddr_in local = *(const struct sockaddr_in *)address;
    local.sin_addr.s_addr = htonl(INADDR_LOOPBACK); local.sin_port = htons((uint16_t)fixture_port);
    int rc = bind(fd,(const struct sockaddr *)&local,sizeof(local));
    if (!rc && !fixture_port)
    {
        socklen_t n = sizeof(local);
        if (getsockname(fd,(struct sockaddr *)&local,&n)) return -1;
        fixture_port = ntohs(local.sin_port);
    }
    return rc;
}
static int fixture_setsockopt(int fd,int level,int name,const void *value,socklen_t size)
{
    if (level == SOL_SOCKET && (name == SO_REUSEADDR || name == SO_REUSEPORT)) return 0;
    return setsockopt(fd,level,name,value,size);
}
static ssize_t fixture_sendto(int fd,const void *data,size_t length,int flags,const struct sockaddr *address,socklen_t size)
{
    if (!address || size != sizeof(struct sockaddr_in) || address->sa_family != AF_INET ||
        (ntohl(((const struct sockaddr_in *)address)->sin_addr.s_addr) >> 24) != 127)
    { errno = EACCES; return -1; }
    return sendto(fd,data,length,flags,address,size);
}
static int fixture_listen(int fd,int backlog)
{
    int rc = listen(fd,backlog);
    if (!rc) { printf("THETIS_P1_READY %d\n",fixture_port); fflush(stdout); }
    return rc;
}
#define bind fixture_bind
#define setsockopt fixture_setsockopt
#define sendto fixture_sendto
#define listen fixture_listen
#define main hpsdrsim_main
#include "hpsdrsim.c"
#undef main
int main(int argc,char **argv)
{
    (void)argv;
    if (argc != 1) { fputs("Owned loopback fixture accepts no arguments.\n",stderr); return 2; }
    setvbuf(stdout,NULL,_IOLBF,0);
    char *args[] = {"hpsdrsim-loopback","-P1","-hermeslite2",NULL};
    return hpsdrsim_main(3,args);
}
