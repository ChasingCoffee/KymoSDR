/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "playback.h"
#include "pcm_queue.h"
#include <cstdio>
#include <cstdlib>
#include <thread>
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"Playback failure %d: %s\n",__LINE__,#x); std::exit(1); } } while (0)
int main() {
    CHECK(ThetisAudioAbi() == 1);
    int64_t state[17]; state[16] = 12345;
    CHECK(ThetisAudioState(nullptr,16) == -1 && ThetisAudioState(state,15) == -1);
    CHECK(ThetisAudioState(state,16) == 16 && state[1] == 0 && state[16] == 12345);
    CHECK(ThetisAudioOpen(2,-1,48000,nullptr,nullptr) == -1);
    CHECK(ThetisAudioOpen(1,-2,48000,nullptr,nullptr) == -1);
    CHECK(ThetisAudioOpen(1,-1,22050,nullptr,nullptr) == -1);
    for (int rate : {44100,48000,96000}) {
        CHECK(ThetisAudioOpen(1,-1,rate,nullptr,nullptr) == 0);
        CHECK(ThetisAudioOpen(1,-1,rate,nullptr,nullptr) == -2);
        float out[2*960+2]{}; out[2*960] = 123;
        CHECK(ThetisAudioRenderNull(out,960) == 960);
        for (int i = 0; i < 1920; ++i) CHECK(out[i] == 0);
        CHECK(ThetisAudioMute(0) == 0);
        double input[960]; double energy = 0; int positive = 0, last = 0, measured = 0;
        for (int block = 0; block < 200; ++block) {
            for (int i = 0; i < 480; ++i) input[2*i] = input[2*i+1] = .2*std::sin(6.283185307179586*1000*(block*480+i)/48000);
            CHECK(ThetisAudioWrite(input,480) == 480);
            int count = rate/100; CHECK(ThetisAudioRenderNull(out,count) == count);
            if (block > 10) for (int i = 0; i < count; ++i) {
                CHECK(std::isfinite(out[2*i]) && out[2*i] == out[2*i+1]);
                energy += out[2*i]*out[2*i]; ++measured;
                if (out[2*i] > 0 && last <= 0) ++positive; last = out[2*i] > 0 ? 1 : -1;
            }
        }
        double rms = std::sqrt(energy/measured), hz = positive*static_cast<double>(rate)/measured;
        std::printf("Playback %d Hz: RMS %.9f, tone %.3f Hz\n",rate,rms,hz);
        CHECK(std::abs(rms-.2/std::sqrt(2.0)) < .002 && std::abs(hz-1000) < 2);
        CHECK(ThetisAudioState(state,16) == 16 && state[8] == 0 && state[11] == 0 && state[16] == 12345);
        CHECK(ThetisAudioMute(1) == 0 && ThetisAudioRenderNull(out,960) == 960);
        for (int i = 0; i < 1920; ++i) CHECK(out[i] == 0);
        CHECK(out[1920] == 123);
        CHECK(ThetisAudioInterruptNull() == 0 && ThetisAudioState(state,16) == 16 && state[3] == 0 && state[5] == 1);
        CHECK(ThetisAudioClose() == 0 && ThetisAudioClose() == 0);
    }
    // Direct SPSC concurrency exercises the actual device callback queue, not the
    // API's serialized null renderer. Sentinels expose ordering/wrap corruption.
    auto *q = new PcmQueue(48000); q->muted.store(0);
    std::thread producer([&] { double in[480]; for (double &v : in) v = .125;
        for (int i = 0; i < 10000; ++i) { q->write(in,240); std::this_thread::yield(); } });
    float out[512];
    for (int block = 0; block < 10000; ++block) {
        q->render(out,256);
        for (float value : out) CHECK(value == 0 || std::abs(value-.125f) < 1e-6);
        if (block%17 == 0) q->reset.fetch_add(1);
    }
    producer.join(); CHECK(q->written.load()-q->read.load() <= PcmQueue::capacity); delete q;
    PcmQueue finite(48000); double bad[2*8192];
    for (double &v : bad) v = 8; bad[0] = NAN; bad[1] = INFINITY;
    CHECK(finite.write(bad,8192) == 8192 && finite.write(bad,10) == 0);
    finite.muted.store(0); finite.render(out,256);
    for (float v : out) CHECK(std::isfinite(v) && std::abs(v) <= 1);
    CHECK(finite.nonfinite.load() == 2 && finite.clipped.load() > 0 && finite.rejected.load() == 10);
    finite.reset.fetch_add(1); finite.render(out,256);
    for (float v : out) CHECK(v == 0);
    puts("PASS: output-only ABI, resampling, mute, finite/clamp, bounded SPSC, flush and device loss; no devices opened");
}
