/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_RADIO_INIT_H
#define THETIS_RADIO_INIT_H
#include "radio_socket.h"
enum _HPSDRModel //from enums.cs
{
	HPSDRModel_HPSDR = 0,
	HPSDRModel_HERMES = 1,
	HPSDRModel_ANAN10 = 2,
	HPSDRModel_ANAN10E = 3,
	HPSDRModel_ANAN100 = 4,
	HPSDRModel_ANAN100B = 5,
	HPSDRModel_ANAN100D = 6,
	HPSDRModel_ANAN200D = 7,
	HPSDRModel_ORIONMKII = 8,
	HPSDRModel_ANAN7000D = 9,
	HPSDRModel_ANAN8000D = 10,
	HPSDRModel_ANAN_G2 = 11,
	HPSDRModel_ANAN_G2_1K = 12,
	HPSDRModel_ANVELINAPRO3 = 13,
	HPSDRModel_HERMESLITE = 14,
	HPSDRModel_REDPITAYA = 15,
	HPSDRModel_ANAN_G2E = 16 //N1GP G2E added
};
extern enum _HPSDRModel HPSDRModel;

enum _RadioProtocol
{
	USB = 0,  // Protocol USB (P1)
	ETH = 1   // Protocol ETH (P2)
};
extern enum _RadioProtocol RadioProtocol;

extern cm_socket listenSock;
extern uint32_t MetisAddr;
extern int radio_local_port;
int nativeInitMetis(char *netaddr, int port, char *localaddr, int localport,
    int protocol, int model_id, int relocate);
void DeInitMetisSockets(void);
#endif
