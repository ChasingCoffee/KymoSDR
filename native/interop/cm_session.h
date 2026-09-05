/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_CM_SESSION_H
#define THETIS_CM_SESSION_H
#include <stdint.h>
#if defined(_WIN32) && defined(THETIS_CM_HEADLESS)
#define CM_API __declspec(dllexport)
#elif defined(_WIN32)
#define CM_API
#else
#define CM_API __attribute__((visibility("default")))
#endif
/* Callbacks run synchronously, at completed startup stages only. Return >0 to
 * cancel, <0 to fail. No callback is retained after Open returns. */
typedef int (*cm_checkpoint)(int stage, void *context);
CM_API int ThetisCmOpen(int abi, int rx_rate, int audio_mode, int allow_transmit,
                       cm_checkpoint checkpoint, void *context);
CM_API int ThetisCmClose(void);
/* 16 int32 fields: ABI, open, streams, receivers, subreceivers, transmitters,
 * special streams, RX input rate, audio rate, TX output rate, owned CM workers,
 * scope creates, wave-play creates, wave-record creates, audio mode, TX allowed. */
CM_API int ThetisCmGetState(int32_t *values, int capacity);
CM_API int ThetisCmP2PortBase(int discovery_port, int use_relocated_ports);
#endif
