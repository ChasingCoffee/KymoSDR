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
 * ABI 2. Device -1 is explicit, sample-driven output; never initializes PortAudio.
 * Physical outputs automatically use bounded adaptive clock recovery. */
AUDIO_API int ThetisAudioAbi(void);
AUDIO_API int ThetisAudioInitialize(void);
AUDIO_API int ThetisAudioTerminate(void);
AUDIO_API int ThetisAudioDeviceCount(void);
/* Default output device in the current enumeration, or -1 when unavailable.
 * Metadata only; never opens a stream. Requires initialization. */
AUDIO_API int ThetisAudioDefaultOutputDevice(void);
/* Five int32: ABI, device index, output channels, default rate, supported rates
 * bitmask 1=44100, 2=48000, 4=96000. Names are UTF-8, including terminator. */
AUDIO_API int ThetisAudioDevice(int index,int *values,int capacity,char *name,int name_capacity,char *host,int host_capacity);
/* Additive routing/meter ABI 1; original playback ABI 2 remains unchanged.
 * Adjacent pairs, zero-based even first channel, at most 128 advertised outputs.
 * Four int32: ABI 1, first channel, callback channel count, supported rate bits.
 * Channel names are driver-reported where available, otherwise numbered. */
AUDIO_API int ThetisAudioRoutingAbi(void);
AUDIO_API int ThetisAudioPair(int device,int first,int *values,int capacity,char *left,int left_capacity,char *right,int right_capacity);
AUDIO_API int ThetisAudioOpenPair(int abi,int device,int rate,int first,int expected_channels,const char *expected_name,const char *expected_host);
/* Six int64: ABI 1, 100 ms window sequence, L/R peak, L/R RMS (amplitude *1e9).
 * Post-mute software samples, not ADC/RF, DAC or monitor readback. Returns 0
 * if publication is in progress; caller may retain its preceding sample. */
AUDIO_API int ThetisAudioLevels(int64_t *values,int capacity);
AUDIO_API int ThetisAudioOpen(int abi,int device,int rate,const char *expected_name,const char *expected_host);
/* No-device fixture with independent consumer clock; same adaptive renderer and
 * watchdog as physical output. Caller must render AND poll state continuously. */
AUDIO_API int ThetisAudioOpenClockedNull(int abi,int rate);
AUDIO_API int ThetisAudioWrite(const double *stereo,int frames);
AUDIO_API int ThetisAudioMute(int muted);
AUDIO_API int ThetisAudioFlush(void);
/* Identical renderer to the device callback. Only valid for no-device output. */
AUDIO_API int ThetisAudioRenderNull(float *stereo,int frames);
AUDIO_API int ThetisAudioInterruptNull(void);
/* 22 int64: ABI, open, physical, active, rate, muted, queued source frames,
 * submitted, rejected, rendered output frames, starvation frames, underrun
 * events, driver underruns, nonfinite input samples, clipped output samples,
 * maximum absolute pre-mute output in millionths (clamped to 1),
 * clock tracking enabled, correction in parts per billion, target source frames,
 * reprimes (flush/starvation), latched fault (0 healthy/1 stopped/2 driver error/
 * 3 callback or polling timeout/4 fixture loss), last PortAudio status. */
AUDIO_API int ThetisAudioState(int64_t *values,int capacity);
AUDIO_API int ThetisAudioClose(void);
AUDIO_API int ThetisAudioError(int code,char *text,int capacity);
#ifdef __cplusplus
}
#endif
#endif
