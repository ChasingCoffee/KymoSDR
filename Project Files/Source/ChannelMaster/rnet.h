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
#ifndef THETIS_RNET_H
#define THETIS_RNET_H
#include <stdint.h>
#include <stdlib.h>
#include <time.h>
#ifdef _WIN32
#include <winsock2.h>
#include <Windows.h>
#else
#include "wdsp_platform.h"
#endif

#define MAX_ADC					(3)
#define MAX_RX_STREAMS			(12)
#define MAX_TX_STREAMS			(3)
#define MAX_AUDIO_STREAMS		(2)
#define MAX_SYNC_RX             (2)
#define CACHE_ALIGN __declspec (align(16))

#define MAX_IN_SEQ_LOG			(40)
#define MAX_IN_SEQ_SNAPSHOTS	(20)

typedef struct _seqLogSnapshot {
	struct _seqLogSnapshot* next;
	struct _seqLogSnapshot* previous;

	int rx_in_seq_snapshot[MAX_IN_SEQ_LOG];
	char dateTimeStamp[24];
	unsigned int received_seqnum;
	unsigned int last_seqnum;
} _seqLogSnapshot_t;

typedef struct CACHE_ALIGN _radionet
{
	int p2_custom_port_base;			// the base port used for inbound packets from the radio, normally 1025 (P2 only)
	int base_outbound_port;			// the base port for outbound packets to the radio, normally 1024 (P1 + P2)
	double** RxBuff;
	double* RxReadBufp;
	double* TxReadBufp;
	unsigned char* ReadBufp;
	char* OutBufp;
	double* outLRbufp;
	double* outIQbufp;
	//double* syncrxbuff[2];
	int run;
	int wdt;
	int sendHighPriority;
	int num_adc;
	int num_dac;
	int ptt_in;
	int dot_in;
	int dash_in;
	int pll_locked;
	int oc_output;
	int oc_output_extras;
	int supply_volts;
	int user_adc0;
	int user_adc1;
	int user_adc2;
	int user_adc3;
	int user_dig_in;
	int user_dig_out;
	unsigned int cc_seq_no;
	unsigned int cc_seq_err;
#ifdef _WIN32
	HANDLE hReadThreadMain;
	HANDLE hReadThreadInitSem;
	HANDLE hWriteThreadMain;
	HANDLE hWriteThreadInitSem;
	HANDLE hsendLRSem;
	HANDLE hsendIQSem;
	HANDLE hsendEventHandles[2];
	HANDLE hobbuffsRun[2];
	HANDLE hKeepAliveThread;
	HANDLE hTimer;
	LARGE_INTEGER liDueTime;
#endif
	CRITICAL_SECTION udpOUT;
	CRITICAL_SECTION sendOUT;
	CRITICAL_SECTION rcvpkt;
	CRITICAL_SECTION sndpkt;
	CRITICAL_SECTION seqErrors;
	CRITICAL_SECTION rcvpktp1;
#ifdef _WIN32
	WSAEVENT hDataEvent;
	WSANETWORKEVENTS wsaProcessEvents;
#endif

	int hardware_LEDs;

	// puresignal settings
	int puresignal_run;

	// wideband settings
	//int wb_base_port;
	int wb_base_dispid;
	int wb_samples_per_packet;
	int wb_sample_size;
	int wb_update_rate;
	int wb_packets_per_frame;
	volatile LONG wb_enable;

	// L & R audio swap for certain models; fixes firmware bugs
	int lr_audio_swap;

	// CAT over TCP/IP port
	int CATPort;

	struct _adc
	{
		int id;
		int rx_step_attn;
		int tx_step_attn;
		int previous_adc_overload;
		int adc_overload;
		int dither;
		int random;
		// wideband dynamic variables & data (per adc)
		int wb_seqnum;
		int wb_state;
		double* wb_buff;
		uint16_t max_magnitude;
		uint16_t max_magnitude_at_overload;
	} adc[MAX_ADC];

	struct _cw
	{
		int sidetone_level;
		int sidetone_freq;
		int keyer_speed;
		int keyer_weight;
		int hang_delay;
		int rf_delay;
		int edge_length;
#pragma pack(push, 1)
		union
		{
			unsigned char mode_control;
			struct {
				unsigned char eer            : 1, // bit 00
				              cw_enable      : 1, // bit 01
				              rev_paddle     : 1, // bit 02
				              iambic         : 1, // bit 03
				              sidetone       : 1, // bit 04
			                  mode_b         : 1, // bit 05
				              strict_spacing : 1, // bit 06
			                  break_in       : 1; // bit 07
			};
		};
#pragma pack(pop)
	}cw;

	struct _mic
	{
		int line_in_gain;
#pragma pack(push, 1)
		union
		{
			unsigned char mic_control;
			struct {
				unsigned char line_in   : 1, // bit 00
				              mic_boost : 1, // bit 01
				              mic_ptt   : 1, // bit 02
				              mic_trs   : 1, // bit 03
				              mic_bias  : 1, // bit 04
				              mic_xlr   : 1, // bit 05
				                        : 1, // bit 06
				                        : 1; // bit 07
			};
		};
#pragma pack(pop)
		int  spp;			// I-samples per network packet
	} mic;

	struct _rx
	{
		int id;
		int rx_adc;
		int frequency;
		int enable;
		int sync;
		int sampling_rate;
		int bit_depth;
		int preamp;
		unsigned rx_in_seq_no;
		unsigned rx_in_seq_err;
		unsigned rx_out_seq_no;
		time_t time_stamp;
		unsigned bits_per_sample;
		int spp;							// IQ-samples per network packet
		int rx_in_seq_delta[MAX_IN_SEQ_LOG];	// ring buffer that contains a delta expected frame number vs recevied frame number
		int rx_in_seq_delta_index;		// next slot to use in ring
		_seqLogSnapshot_t* snapshots_head;		// simple linked list of snapshots of this ring buffer when a seq error occurs
		_seqLogSnapshot_t* snapshots_tail;		// simple linked list of snapshots of this ring buffer when a seq error occurs
		int snapshot_length;					// len of this snapshot list (used to limit)
		_seqLogSnapshot_t* snapshot;			// used by netInterface to work through the list each call;
	} rx[MAX_RX_STREAMS];

	struct _tx
	{
		int id;
		int frequency;
		int sampling_rate;
		int cwx;
		int dash;
		int dot;
		int ptt_out;
		int drive_level;
		int exciter_power;
		int fwd_power;
		int rev_power;
		int phase_shift;
		int epwm_max;
		int epwm_min;
		int pa;
		unsigned mic_in_seq_no;
		unsigned mic_in_seq_err;
		unsigned mic_out_seq_no;
		int spp;							// IQ-samples per network packet
	} tx[MAX_TX_STREAMS];

	struct _audio
	{
		int  spp;							// LR-samples per network packet
	} audio[MAX_AUDIO_STREAMS];

	struct _discovery
	{
		unsigned char MACAddr[6];
		char BoardType;
		char protocolVersion;
		char fwCodeVersion;
		char MercuryVersion_0;
		char MercuryVersion_1;
		char MercuryVersion_2;
		char MercuryVersion_3;
		char PennyVersion;
		char MetisVersion;
		char numRxs;
	} discovery;

} radionet, *RADIONET;

extern RADIONET prn;

#pragma pack(push, 1)
typedef struct _rbpfilter // radio band pass filter
{
	int  enable;
	union
	{
		unsigned bpfilter;
		struct {
			unsigned char  _rx_yellow_led : 1, // bit 00
			               _13MHz_HPF     : 1, // bit 01
			               _20MHz_HPF     : 1, // bit 02
			               _6M_preamp     : 1, // bit 03
			               _9_5MHz_HPF    : 1, // bit 04
			               _6_5MHz_HPF    : 1, // bit 05
			               _1_5MHz_HPF    : 1, // bit 06
			                              : 1, // bit 07

			               _XVTR_Rx_In    : 1, // bit 08
			               _Rx_2_In       : 1, // bit 09 EXT1
			               _Rx_1_In       : 1, // bit 10 EXT2
			               _Rx_1_Out      : 1, // bit 11 K36 RL17
			               _Bypass        : 1, // bit 12
			               _20_dB_Atten   : 1, // bit 13
			               _10_dB_Atten   : 1, // bit 14 (RX MASTER IN SEL RL22)
			               _rx_red_led    : 1, // bit 15

			                              : 1, // bit 16
			                              : 1, // bit 17
			               _trx_status    : 1, // bit 18
			               _tx_yellow_led : 1, // bit 19
			               _30_20_LPF     : 1, // bit 20
			               _60_40_LPF     : 1, // bit 21
			               _80_LPF        : 1, // bit 22
			               _160_LPF       : 1, // bit 23

			               _ANT_1         : 1, // bit 24
			               _ANT_2         : 1, // bit 25
			               _ANT_3         : 1, // bit 26
			               _TR_Relay      : 1, // bit 27
			               _tx_red_led    : 1, // bit 28
			               _6_LPF         : 1, // bit 29
			               _12_10_LPF     : 1, // bit 30
			               _17_15_LPF     : 1; // bit 31
		};
	};
}rbpfilter, *RBPFILTER;
#pragma pack(pop)
extern RBPFILTER prbpfilter;

#pragma pack(push, 1)
typedef struct _rbpfilter2 // radio band pass filter
{
	int  enable;
	union
	{
		unsigned bpfilter;
		struct {
			unsigned char  _rx_yellow_led : 1, // bit 00
			               _13MHz_HPF     : 1, // bit 01
			               _20MHz_HPF     : 1, // bit 02
			               _6M_preamp     : 1, // bit 03
			               _9_5MHz_HPF    : 1, // bit 04
			               _6_5MHz_HPF    : 1, // bit 05
			               _1_5MHz_HPF    : 1, // bit 06
			                              : 1, // bit 07

			               _rx2_gnd       : 1, // bit 08
			                              : 1, // bit 09
			                              : 1, // bit 10
			                              : 1, // bit 11
			               _Bypass        : 1, // bit 12
			                              : 1, // bit 13
			                              : 1, // bit 14
			               _rx_red_led    : 1, // bit 15

			                              : 1, // bit 16
			                              : 1, // bit 17
			               _trx_status    : 1, // bit 18
			               _tx_yellow_led : 1, // bit 19
			               _30_20_LPF     : 1, // bit 20
			               _60_40_LPF     : 1, // bit 21
			               _80_LPF        : 1, // bit 22
			               _160_LPF       : 1, // bit 23

			               _TXANT_1       : 1, // bit 24
			               _TXANT_2       : 1, // bit 25
			               _TXANT_3       : 1, // bit 26
			               _TR_Relay      : 1, // bit 27
			               _tx_red_led    : 1, // bit 28
			               _6_LPF         : 1, // bit 29
			               _12_10_LPF     : 1, // bit 30
			               _17_15_LPF     : 1; // bit 31
		};
	};
}rbpfilter2, *RBPFILTER2;
#pragma pack(pop)
extern RBPFILTER2 prbpfilter2;

/* Pure allocation: no ChannelMaster callbacks, workers or sockets. */
int create_rnet_checked(void);
void destroy_rnet(void);
void clearSnapshots(void);
#ifdef THETIS_TESTING
void rnet_test_fail_after(int allocations);
#endif
#endif
