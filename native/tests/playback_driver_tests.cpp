/* SPDX-License-Identifier: GPL-2.0-or-later */
// Link production playback.cpp against this fake PortAudio backend, NOT the
// real library. Exercises its physical-driver path with no device access.
#include "playback.h"
#include "portaudio.h"
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <initializer_list>
#include <thread>
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"Driver failure %d: %s\n",__LINE__,#x); std::exit(1); } } while (0)
namespace {
PaStreamCallback *render = nullptr;
PaStreamFinishedCallback *finish = nullptr;
void *context = nullptr;
int initialized = 0, opened = 0, active = 0, starts = 0, opens = 0;
int fail_open = 0, fail_register = 0, fail_start = 0, fail_close = 0;
int stream_token;
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
// All referenced PortAudio entry points are locally implemented. No default
// device function, input stream, hardware backend or library is linked here.
PaError Pa_Initialize() { CHECK(!initialized); initialized = 1; return 0; }
PaError Pa_Terminate() { CHECK(initialized && !opened); initialized = 0; return 0; }
PaDeviceIndex Pa_GetDeviceCount() { return 1; }
const PaDeviceInfo *Pa_GetDeviceInfo(PaDeviceIndex index) {
    static PaDeviceInfo d{2,"",0,0,2,0,.02,0,.05,48000};
    d.name = device_name; return index == 0 ? &d : nullptr;
}
const PaHostApiInfo *Pa_GetHostApiInfo(PaHostApiIndex index) {
    static PaHostApiInfo h{1,paInDevelopment,"Fixture",1,-1,0}; return index == 0 ? &h : nullptr;
}
PaError Pa_IsFormatSupported(const PaStreamParameters *in,const PaStreamParameters *out,double rate) {
    CHECK(!in && out && out->device == 0 && out->channelCount == 2 && out->sampleFormat == paFloat32);
    return rate == 48000 || rate == 44100 || rate == 96000 ? 0 : paInvalidSampleRate;
}
PaError Pa_OpenStream(PaStream **stream,const PaStreamParameters *in,const PaStreamParameters *out,double,
    unsigned long,PaStreamFlags,PaStreamCallback *callback,void *user) {
    CHECK(initialized && !opened && !in && out->device == 0); ++opens;
    if (fail_open) return fail_open;
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

int main() {
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
