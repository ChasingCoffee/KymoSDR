/*  network.h

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2015-2020 Doug Wigley, W5WC

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

*/
#pragma once

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <ws2tcpip.h>
#include <Mswsock.h>
#include <VersionHelpers.h>
#include "analyzer.h"
#include "cmcomm.h"

#include "rnet.h"

extern __declspec(dllexport) void create_rnet();
extern __declspec(dllexport) void destroy_rnet();
void WriteUDPFrame(int id, char *bufp, int buflen);
int sendPacket(SOCKET sock, char *data, int length, int port);
void CmdGeneral(void);
void CmdHighPriority(void);
extern __declspec(dllexport) void CmdRx(void);
void CmdTx(void);
DWORD WINAPI ReadThreadMain(LPVOID);
DWORD WINAPI KeepAliveMain(LPVOID);
void ReadThreadMainLoop();
void KeepAliveLoop();
void PrintTimeHack();
void PeakFwdPower(float fwd);
void PeakRevPower(float rev);
void UpdateRadioProtocolSampleSize();
int IOThreadStop(void);
int StartReadThread(void);
void StopReadThread(void);
__declspec (dllexport) int SendStartToMetis(void);
int io_keep_running;
int IOThreadRunning;   // non zero if IOThread is running
int XmitBit;
unsigned char ControlBytesIn[5];
int HaveSync;
int ADC_cntrl1;
int ADC_cntrl2;
int nreceivers;
int xvtr_enable;
int atu_tune; // controls J16 pin 10 on Orion MKII board
int audioamp_enable; // constrol audio amp on ?? board //MW0LGE_22b
int AlexHPFMask;
int AlexLPFMask;
int Alex1LPFMask;
int AlexTRRelay;
int Alex2HPFMask;
int Alex2LPFMask;
int Alex3HPFMask;
int Alex3LPFMask;
int Alex4HPFMask;
int Alex4LPFMask;
int mkiibpf;
float RevPower;
float FwdPower;
int ApolloFilt;
int ApolloFiltSelect;
int ApolloTuner;
int ApolloATU;

#include "radio_init.h"
SYSTEMTIME lt;
static const double const_1_div_2147483648_ = 1.0 / 2147483648.0;

enum HPSDRHW
{
	Atlas = 0,          // Metis in PowerSDR, but Atlas in Thetis
	Hermes = 1,         // ANAN-10 ANAN100
	HermesII = 2,       // ANAN-10E ANAN-100B HeremesII
	Angelia = 3,        // ANAN-100D
	Orion = 4,          // ANAN-200D
	OrionMKII = 5,      // AMAM-7000DLE 7000DLEMkII ANAN-8000DLE OrionMkII Anvelina-Pro3 RedPitaya
	HermesLite = 6,     // MI0BOT
	Saturn = 10,        // ANAN-G2: added G8NJJ
	SaturnMKII = 11,    // ANAN-G2: MKII board?
	HermesC10 = 20      // ANAN-G2E //N1GP G2E added (HermesC10)
};

// Protocol 1 USB
DWORD WINAPI MetisReadThreadMain(LPVOID n);
void WriteMainLoop(char* bufp);
void MetisReadThreadMainLoop(void);
DWORD WINAPI  sendProtocol1Samples(LPVOID n);
 int MetisReadDirect(unsigned char* bufp);
 int MetisWriteFrame(int endpoint, char* bufp);
 void ForceCandCFrame(int);
 extern __declspec(dllexport) int SendStopToMetis();
 int FPGAReadBufSize;
 int FPGAWriteBufSize;
 unsigned char* FPGAReadBufp;
 char* FPGAWriteBufp;
 int P1ddcconfig;		// DDCconfig for P1 (h/w and mode dependent)
 int P1_en_diversity;		// true if diversity enabled
 int P1_rxcount;
 int P1_adc_cntrl;
 int nddc;
 int XmitBit;
 unsigned char SampleRateIn2Bits;
 int mic_decimation_factor;
 int mic_decimation_count;
