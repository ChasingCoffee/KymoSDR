/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "playback.h"
#include "pcm_queue.h"
#include "portaudio.h"
#include <cstring>
#include <mutex>
#include <new>

namespace {
std::mutex gate;
PcmQueue *queue = nullptr;
PaStream *stream = nullptr;
bool initialized = false, physical = false;
bool valid_rate(int rate) { return rate == 44100 || rate == 48000 || rate == 96000; }
int initialize() {
    if (initialized) return 0;
    int rc = Pa_Initialize(); if (rc == 0) initialized = true; return rc;
}
int terminate() {
    if (queue) return -2;
    int rc = initialized ? Pa_Terminate() : 0;
    if (rc == 0) initialized = false;
    return rc;
}
int copy_text(const char *from,char *to,int capacity) {
    if (!from || !to || capacity <= 0 || std::strlen(from) >= static_cast<size_t>(capacity)) return -1;
    std::memcpy(to,from,std::strlen(from)+1); return 0;
}
int callback(const void *,void *output,unsigned long count,const PaStreamCallbackTimeInfo *,PaStreamCallbackFlags flags,void *context) {
    auto *q = static_cast<PcmQueue *>(context);
    if (flags & paOutputUnderflow) q->driver_underruns.fetch_add(1,std::memory_order_relaxed);
    q->render(static_cast<float *>(output),static_cast<int>(count));
    return paContinue;
}
int close() {
    if (!queue) return terminate();
    queue->muted.store(1); queue->active.store(0);
    if (stream) {
        Pa_AbortStream(stream);
        int rc = Pa_CloseStream(stream);
        // Never free memory still potentially used by a failed driver callback.
        if (rc != 0) return rc;
        stream = nullptr;
    }
    delete queue; queue = nullptr; physical = false;
    return terminate();
}
}
int ThetisAudioAbi(void) { return 1; }
int ThetisAudioInitialize(void) { std::lock_guard<std::mutex> lock(gate); return initialize(); }
int ThetisAudioTerminate(void) { std::lock_guard<std::mutex> lock(gate); return terminate(); }
int ThetisAudioDeviceCount(void) { std::lock_guard<std::mutex> lock(gate); return initialized ? Pa_GetDeviceCount() : -3; }
int ThetisAudioDevice(int index,int *values,int capacity,char *name,int nc,char *host,int hc) {
    std::lock_guard<std::mutex> lock(gate);
    if (!values || capacity < 5 || !name || !host || nc < 1 || hc < 1 || index < 0) return -1;
    if (!initialized) return -3;
    const PaDeviceInfo *d = Pa_GetDeviceInfo(index); if (!d) return -1;
    const PaHostApiInfo *h = Pa_GetHostApiInfo(d->hostApi); if (!h) return -1;
    if (copy_text(d->name,name,nc) || copy_text(h->name,host,hc)) return -1;
    int bits = 0; const int rates[] = {44100,48000,96000};
    if (d->maxOutputChannels >= 2) {
        PaStreamParameters output{index,2,paFloat32,d->defaultLowOutputLatency,nullptr};
        for (int i = 0; i < 3; ++i) if (Pa_IsFormatSupported(nullptr,&output,rates[i]) == paFormatIsSupported) bits |= 1<<i;
    }
    int result[] = {1,index,d->maxOutputChannels,static_cast<int>(d->defaultSampleRate),bits};
    std::memcpy(values,result,sizeof(result)); return 5;
}
int ThetisAudioOpen(int abi,int device,int rate,const char *expected_name,const char *expected_host) {
    std::lock_guard<std::mutex> lock(gate);
    if (abi != 1 || device < -1 || !valid_rate(rate) || (device >= 0 && (!expected_name || !expected_host))) return -1;
    if (queue) return -2;
    if (device >= 0) {
        int rc = initialize(); if (rc != 0) return rc;
        const PaDeviceInfo *d = Pa_GetDeviceInfo(device);
        const PaHostApiInfo *h = d ? Pa_GetHostApiInfo(d->hostApi) : nullptr;
        if (!d || !h || d->maxOutputChannels < 2 || std::strcmp(d->name,expected_name) || std::strcmp(h->name,expected_host)) { terminate(); return -1; }
        PaStreamParameters output{device,2,paFloat32,std::max(.02,d->defaultLowOutputLatency),nullptr};
        rc = Pa_IsFormatSupported(nullptr,&output,rate); if (rc != 0) { terminate(); return rc; }
        queue = new(std::nothrow) PcmQueue(rate); if (!queue) { terminate(); return paInsufficientMemory; }
        physical = true;
        rc = Pa_OpenStream(&stream,nullptr,&output,rate,paFramesPerBufferUnspecified,paClipOff,callback,queue);
        if (rc == 0) rc = Pa_StartStream(stream);
        if (rc != 0) { close(); return rc; }
    } else {
        queue = new(std::nothrow) PcmQueue(rate); if (!queue) return paInsufficientMemory;
    }
    return 0;
}
int ThetisAudioWrite(const double *stereo,int frames) {
    std::lock_guard<std::mutex> lock(gate);
    if (!stereo || frames < 1 || frames > 16384) return -1;
    return queue ? queue->write(stereo,frames) : -3;
}
int ThetisAudioMute(int muted) {
    std::lock_guard<std::mutex> lock(gate);
    if (muted != 0 && muted != 1) return -1;
    if (!queue) return -3;
    queue->muted.store(muted); return 0;
}
int ThetisAudioFlush(void) {
    std::lock_guard<std::mutex> lock(gate);
    if (!queue) return -3;
    queue->reset.fetch_add(1,std::memory_order_release); return 0;
}
int ThetisAudioRenderNull(float *stereo,int frames) {
    std::lock_guard<std::mutex> lock(gate);
    if (!stereo || frames < 1 || frames > 4096) return -1;
    if (!queue || physical) return -3;
    queue->render(stereo,frames); return frames;
}
int ThetisAudioInterruptNull(void) {
    std::lock_guard<std::mutex> lock(gate);
    if (!queue || physical) return -3;
    queue->muted.store(1); queue->active.store(0); return 0;
}
int ThetisAudioState(int64_t *values,int capacity) {
    std::lock_guard<std::mutex> lock(gate);
    if (!values || capacity < 16) return -1;
    int64_t result[16] = {1};
    if (queue) {
        if (physical && Pa_IsStreamActive(stream) != 1) { queue->muted.store(1); queue->active.store(0); }
        result[1] = 1; result[2] = physical; result[3] = queue->active.load(); result[4] = queue->rate; result[5] = queue->muted.load();
        auto r = queue->read.load(std::memory_order_acquire), w = queue->written.load(std::memory_order_acquire);
        result[6] = static_cast<int64_t>(w-r);
        result[7] = queue->submitted.load(); result[8] = queue->rejected.load(); result[9] = queue->rendered.load();
        result[10] = queue->starvation.load(); result[11] = queue->underruns.load(); result[12] = queue->driver_underruns.load();
        result[13] = queue->nonfinite.load(); result[14] = queue->clipped.load(); result[15] = queue->peak.load();
    }
    std::memcpy(values,result,sizeof(result)); return 16;
}
int ThetisAudioClose(void) { std::lock_guard<std::mutex> lock(gate); return close(); }
int ThetisAudioError(int code,char *text,int capacity) {
    const char *message = code == -1 ? "Invalid audio argument, stale device selection or unsupported stereo output" :
        code == -2 ? "An audio owner is already active" : code == -3 ? "Audio owner is closed or has the wrong output kind" : Pa_GetErrorText(code);
    return copy_text(message,text,capacity);
}
