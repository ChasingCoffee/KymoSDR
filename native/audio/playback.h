/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef KYMO_PLAYBACK_H
#define KYMO_PLAYBACK_H
#include <stdint.h>
#ifdef _WIN32
#define AUDIO_API __declspec(dllexport)
#else
#define AUDIO_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* All control/write calls are serialized. Native callback never takes this lock.
 * Stereo float64 input at 48 kHz. Output float32 at 44.1/48/96 kHz; finite/clamped.
 * ABI 1. Device -1 is explicit, no-device output; never initializes PortAudio. */
AUDIO_API int ThetisAudioAbi(void);
AUDIO_API int ThetisAudioInitialize(void);
AUDIO_API int ThetisAudioTerminate(void);
AUDIO_API int ThetisAudioDeviceCount(void);
/* Five int32: ABI, device index, output channels, default rate, supported rates
 * bitmask 1=44100, 2=48000, 4=96000. Names are UTF-8, including terminator. */
AUDIO_API int ThetisAudioDevice(int index,int *values,int capacity,char *name,int name_capacity,char *host,int host_capacity);
AUDIO_API int ThetisAudioOpen(int abi,int device,int rate,const char *expected_name,const char *expected_host);
AUDIO_API int ThetisAudioWrite(const double *stereo,int frames);
AUDIO_API int ThetisAudioMute(int muted);
AUDIO_API int ThetisAudioFlush(void);
/* Identical renderer to the device callback. Only valid for no-device output. */
AUDIO_API int ThetisAudioRenderNull(float *stereo,int frames);
AUDIO_API int ThetisAudioInterruptNull(void);
/* 16 int64: ABI, open, physical, active, rate, muted, queued source frames,
 * submitted, rejected, rendered output frames, starvation frames, underrun
 * events, driver underruns, nonfinite input samples, clipped output samples,
 * maximum absolute pre-mute output in millionths (clamped to 1). */
AUDIO_API int ThetisAudioState(int64_t *values,int capacity);
AUDIO_API int ThetisAudioClose(void);
AUDIO_API int ThetisAudioError(int code,char *text,int capacity);
#ifdef __cplusplus
}
#endif
#endif
