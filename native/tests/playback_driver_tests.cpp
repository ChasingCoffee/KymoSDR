/* SPDX-License-Identifier: GPL-2.0-or-later */
// Link production playback.cpp against this fake PortAudio backend, NOT the
// real library. Exercises its physical-driver path with no device access.
#include "playback.h"
#include "portaudio.h"
#ifdef __APPLE__
#include "pa_mac_core.h"
#endif
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <initializer_list>
#include <thread>
#include <vector>
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"Driver failure %d: %s\n",__LINE__,#x); std::exit(1); } } while (0)
namespace {
PaStreamCallback *render = nullptr;
PaStreamFinishedCallback *finish = nullptr;
void *context = nullptr;
int initialized = 0, opened = 0, active = 0, starts = 0, opens = 0;
int fail_open = 0, fail_register = 0, fail_start = 0, fail_close = 0;
int stream_token;
int device_channels = 2,opened_channels = 2,reject_channels = 0;
int default_output = 0;
PaHostApiTypeId host_type = paInDevelopment;
int opened_map[128],opened_map_size = 0;
const char *device_name = "Explicit test speakers";
int64_t state[23];
double input[2*4096]; float output[2*512];
void read_state() { state[22] = 12345; CHECK(ThetisAudioState(state,22) == 22 && state[22] == 12345 && state[0] == 2); }
void open_output() {
    CHECK(ThetisAudioOpen(2,0,48000,"Explicit test speakers","Fixture") == 0);
    read_state(); CHECK(state[2] == 1 && state[3] == 1 && state[5] == 1 && state[16] == 1 && state[18] == 3072 && state[20] == 0);
}
void signal() {
    for (double &v : input) v = .125;
    CHECK(ThetisAudioWrite(input,4096) == 4096);
    CHECK(ThetisAudioMute(0) == 0);
    CHECK(render(nullptr,output,512,nullptr,paOutputUnderflow,context) == paContinue);
    for (float v : output) CHECK(std::abs(v-.125f) < 1e-6);
    read_state(); CHECK(state[12] == 1);
}
void faulted(int reason) {
    read_state(); CHECK(state[3] == 0 && state[5] == 1 && state[20] == reason);
    CHECK(ThetisAudioMute(0) == -4 && ThetisAudioWrite(input,4096) == -4);
    CHECK(render(nullptr,output,512,nullptr,0,context) == paAbort);
    for (float v : output) CHECK(v == 0);
    read_state(); CHECK(state[20] == reason && state[5] == 1);
}
}
// All referenced PortAudio entry points, including default-device metadata,
// are local fakes. No input stream, hardware backend or library is linked here.
PaError Pa_Initialize() { CHECK(!initialized); initialized = 1; return 0; }
PaError Pa_Terminate() { CHECK(initialized && !opened); initialized = 0; return 0; }
PaDeviceIndex Pa_GetDeviceCount() { return 1; }
PaDeviceIndex Pa_GetDefaultOutputDevice() { CHECK(initialized); return default_output; }
const PaDeviceInfo *Pa_GetDeviceInfo(PaDeviceIndex index) {
    static PaDeviceInfo d{2,"",0,0,2,0,.02,0,.05,48000};
    d.name = device_name; d.maxOutputChannels = device_channels; return index == 0 ? &d : nullptr;
}
const PaHostApiInfo *Pa_GetHostApiInfo(PaHostApiIndex index) {
    static PaHostApiInfo h{1,paInDevelopment,"Fixture",1,-1,0}; h.type = host_type; return index == 0 ? &h : nullptr;
}
PaError Pa_IsFormatSupported(const PaStreamParameters *in,const PaStreamParameters *out,double rate) {
    CHECK(!in && out && out->device == 0 && out->channelCount >= 2 && out->channelCount <= device_channels && out->sampleFormat == paFloat32);
    if (out->channelCount == reject_channels) return paInvalidChannelCount;
    return rate == 48000 || rate == 44100 || rate == 96000 ? 0 : paInvalidSampleRate;
}
PaError Pa_OpenStream(PaStream **stream,const PaStreamParameters *in,const PaStreamParameters *out,double,
    unsigned long,PaStreamFlags,PaStreamCallback *callback,void *user) {
    CHECK(initialized && !opened && !in && out->device == 0); ++opens;
    if (fail_open) return fail_open;
    opened_channels = out->channelCount; opened_map_size = 0;
#ifdef __APPLE__
    if (out->hostApiSpecificStreamInfo) {
        auto *mac = static_cast<PaMacCoreStreamInfo *>(out->hostApiSpecificStreamInfo);
        CHECK(mac->hostApiType == paCoreAudio && mac->channelMapSize <= 128);
        opened_map_size = static_cast<int>(mac->channelMapSize);
        for (int i = 0; i < opened_map_size; ++i) opened_map[i] = mac->channelMap[i];
    }
#endif
    *stream = &stream_token; render = callback; context = user; finish = nullptr; opened = 1; return 0;
}
PaError Pa_SetStreamFinishedCallback(PaStream *,PaStreamFinishedCallback *callback) {
    CHECK(opened && !active); if (fail_register) return fail_register; finish = callback; return 0;
}
PaError Pa_StartStream(PaStream *) { ++starts; if (fail_start) return fail_start; active = 1; return 0; }
PaError Pa_IsStreamActive(PaStream *) { CHECK(opened); return active; }
PaError Pa_AbortStream(PaStream *) { CHECK(opened); active = 0; if (finish) finish(context); return 0; }
PaError Pa_CloseStream(PaStream *) {
    CHECK(opened); if (fail_close) return fail_close;
    opened = 0; context = nullptr; render = nullptr; finish = nullptr; return 0;
}
const char *Pa_GetErrorText(PaError) { return "Fixture error"; }
#ifdef __APPLE__
void PaMacCore_SetupStreamInfo(PaMacCoreStreamInfo *info,unsigned long flags) {
    *info = {}; info->size = sizeof(*info); info->hostApiType = paCoreAudio; info->version = 1; info->flags = flags;
}
void PaMacCore_SetupChannelMap(PaMacCoreStreamInfo *info,const SInt32 *map,unsigned long size) { info->channelMap = map; info->channelMapSize = size; }
const char *PaMacCore_GetChannelName(int device,int channel,bool input) {
    CHECK(device == 0 && !input); static char name[64]; std::snprintf(name,sizeof(name),"Fixture channel %d",channel+1); return name;
}
#endif

int main() {
    CHECK(ThetisAudioDefaultOutputDevice() == -3);
    CHECK(ThetisAudioInitialize() == 0);
    CHECK(ThetisAudioDefaultOutputDevice() == 0 && opens == 0);
    default_output = paNoDevice; CHECK(ThetisAudioDefaultOutputDevice() == -1 && opens == 0);
    CHECK(ThetisAudioTerminate() == 0); default_output = 0;
    CHECK(ThetisAudioOpen(1,-1,48000,nullptr,nullptr) == -1);
    CHECK(ThetisAudioState(state,16) == -1); // reject old ABI, never overrun caller
    for (int cycle = 0; cycle < 100; ++cycle) {
        open_output(); signal();
        if (cycle%3 == 0) { finish(context); faulted(1); }
        if (cycle%3 == 1) { active = 0; faulted(1); }
        if (cycle%3 == 2) { active = paDeviceUnavailable; faulted(2); CHECK(state[21] == paDeviceUnavailable); }
        CHECK(ThetisAudioClose() == 0 && !opened && !initialized);
    }
    // A driver that still reports active but stops delivering callbacks.
    open_output(); signal();
    std::this_thread::sleep_for(std::chrono::milliseconds(1100));
    faulted(3); CHECK(ThetisAudioClose() == 0);
    int before = opens;
    device_name = "Different output at same index";
    CHECK(ThetisAudioOpen(2,0,48000,"Explicit test speakers","Fixture") == -1);
    CHECK(opens == before && !initialized && !opened);
    device_name = "Explicit test speakers";
    // Sparse routing to channels 11/12, 13/14 and the bounded upper edge.
    // Never enumerate a real device, and never permit input or default fallback.
    device_channels = 128;
    CHECK(ThetisAudioInitialize() == 0 && ThetisAudioRoutingAbi() == 1);
    int pair[5]; pair[4] = 6789; char left[64],right[64];
    CHECK(ThetisAudioPair(0,10,pair,4,left,64,right,64) == 4 && pair[0] == 1 && pair[1] == 10 && pair[2] == 12 && pair[3] == 7 && pair[4] == 6789);
    CHECK(ThetisAudioPair(0,11,pair,4,left,64,right,64) == -1);
    CHECK(ThetisAudioPair(0,128,pair,4,left,64,right,64) == -1);
    CHECK(ThetisAudioPair(0,10,pair,3,left,64,right,64) == -1);
    reject_channels = 14;
    CHECK(ThetisAudioPair(0,12,pair,4,left,64,right,64) == 4 && pair[3] == 0);
    CHECK(ThetisAudioTerminate() == 0);
    before = opens;
    CHECK(ThetisAudioOpenPair(1,0,48000,12,128,device_name,"Fixture") == paInvalidChannelCount);
    CHECK(opens == before && !initialized); reject_channels = 0;
    CHECK(ThetisAudioOpenPair(1,0,48000,10,32,device_name,"Fixture") == -1 && opens == before && !initialized);
    CHECK(ThetisAudioOpenPair(1,0,48000,11,128,device_name,"Fixture") == -1 && opens == before && !initialized);
    CHECK(ThetisAudioOpenPair(1,-1,48000,0,128,nullptr,nullptr) == -1);
    for (int first : {0,10,12,126}) {
        CHECK(ThetisAudioOpenPair(1,0,48000,first,128,device_name,"Fixture") == 0);
        CHECK(opened_channels == first+2);
        for (int i = 0; i < 4096; ++i) { input[2*i] = .125; input[2*i+1] = -.25; }
        CHECK(ThetisAudioWrite(input,4096) == 4096);
        std::vector<float> routed(opened_channels*513+2,12345.f);
        CHECK(render(nullptr,routed.data()+1,513,nullptr,0,context) == paContinue);
        for (int i = 1; i <= opened_channels*513; ++i) CHECK(routed[i] == 0);
        CHECK(ThetisAudioMute(0) == 0);
        CHECK(render(nullptr,routed.data()+1,513,nullptr,0,context) == paContinue);
        for (int i = 0; i < 513; ++i) for (int c = 0; c < opened_channels; ++c)
            CHECK(std::abs(routed[1+i*opened_channels+c]-(c == first ? .125f : c == first+1 ? -.25f : 0.f)) < 1e-6);
        CHECK(routed.front() == 12345 && routed.back() == 12345);
        CHECK(ThetisAudioClose() == 0);
    }
#ifdef __APPLE__
    host_type = paCoreAudio; device_channels = 32;
    for (int first : {0,10,12,30}) {
        CHECK(ThetisAudioOpenPair(1,0,48000,first,32,device_name,"Fixture") == 0);
        CHECK(opened_channels == 2 && opened_map_size == 32);
        for (int c = 0; c < 32; ++c) CHECK(opened_map[c] == (c == first ? 0 : c == first+1 ? 1 : -1));
        CHECK(ThetisAudioClose() == 0);
    }
#endif
    host_type = paInDevelopment; device_channels = 2;
    for (int *point : {&fail_open,&fail_register,&fail_start}) {
        *point = paDeviceUnavailable;
        CHECK(ThetisAudioOpen(2,0,48000,device_name,"Fixture") == paDeviceUnavailable);
        CHECK(!initialized && !opened); *point = 0;
        open_output(); CHECK(ThetisAudioClose() == 0);
    }
    // A failed close retains callback-owned storage and refuses a second owner.
    open_output(); signal(); fail_close = paUnanticipatedHostError;
    CHECK(ThetisAudioClose() == paUnanticipatedHostError);
    CHECK(ThetisAudioOpen(2,0,48000,device_name,"Fixture") == -2);
    CHECK(render(nullptr,output,512,nullptr,0,context) == paAbort);
    for (float v : output) CHECK(v == 0);
    fail_close = 0; CHECK(ThetisAudioClose() == 0);
    open_output(); read_state(); CHECK(state[5] == 1 && state[17] == 0 && state[19] == 0);
    CHECK(ThetisAudioClose() == 0 && ThetisAudioClose() == 0);
    std::printf("PASS: %d explicit driver opens/%d starts, loss/finished/stall, stale selection, startup rollback, retained failed-close memory; zero physical devices\n",opens,starts);
}
