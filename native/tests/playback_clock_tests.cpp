/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "pcm_queue.h"
#include "playback_health.h"
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <string_view>
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"Clock failure %d: %s\n",__LINE__,#x); std::exit(1); } } while (0)

// One virtual hour per case, including slow/fast clocks, direction changes and
// a repeatable scheduling disturbance. This tests the actual controller law at
// its 10 ms cadence, separately from the full PCM renderer below.
void controller_case(double drift,bool changing) {
    ClockRecovery clock;
    double queued = 3900, low = queued, high = queued, tail = 0;
    for (int tick = 0; tick < 360000; ++tick) {
        double ppm = changing && tick >= 180000 ? -drift : drift;
        double jitter = 300*std::sin(tick*.7) + 150*std::sin(tick*.173);
        double previous = clock.ppm;
        clock.update(queued+jitter);
        queued += 480*(ppm-clock.ppm)/1e6;
        low = std::min(low,queued); high = std::max(high,queued);
        CHECK(std::abs(clock.ppm) <= ClockRecovery::limit_ppm);
        CHECK(std::abs(clock.ppm-previous) <= 2.000001);
        CHECK(queued > 1024 && queued < 6144);
        if (tick%60000 >= 59000) tail += clock.ppm;
        if (tick%60000 == 59999) {
            CHECK(std::abs(tail/1000-ppm) < 5); tail = 0;
            CHECK(std::abs(queued-ClockRecovery::target) < 20);
        }
    }
    std::printf("Virtual 60 min servo: drift %.0f ppm%s, queue %.1f..%.1f, final %.3f ppm\n",drift,changing ? " reversing" : "",low,high,clock.ppm);
    clock.reset(); CHECK(clock.ppm == 0 && clock.filtered == ClockRecovery::target);
}

// Independently scheduled producer packets and callback blocks. Neither clock
// consults queue occupancy. This drives the real SPSC/FIR/servo, not a model of
// it. The source is a stereo 1 kHz/-6 dB tone with distinct channel levels.
void renderer_case(int rate,double ppm,int seconds,bool tracking = true) {
    auto q = std::make_unique<PcmQueue>(rate,tracking);
    q->muted.store(0);
    double input[4096]; float output[4096];
    double producer_time = 0, consumer_time = 0;
    uint64_t source = 0, output_frames = 0, measured = 0, crossings = 0;
    double energy = 0, previous = 0, low = 8192, high = 0, correction_sum = 0;
    int packets = 0, callbacks = 0, correction_count = 0;
    const int batches[] = {240,480,192,1024,128,512};
    const int blocks[] = {128,256,512,96,480,1024};
    while (consumer_time < seconds) {
        if (producer_time <= consumer_time) {
            int count = batches[packets++%6];
            for (int i = 0; i < count; ++i) {
                input[2*i] = .5*std::sin(6.283185307179586*1000*(source+i)/48000);
                input[2*i+1] = input[2*i]*.5;
            }
            q->write(input,count); source += count;
            producer_time += count/(48000*(1+ppm/1e6));
        } else {
            int count = blocks[callbacks++%6];
            q->render(output,count); output_frames += count;
            consumer_time += static_cast<double>(count)/rate;
            if (consumer_time > 180) {
                double depth = static_cast<double>(q->written.load()-q->read.load());
                low = std::min(low,depth); high = std::max(high,depth);
                correction_sum += q->correction_ppb.load()/1000.0; ++correction_count;
                for (int i = 0; i < count; ++i) {
                    double v = output[2*i];
                    CHECK(std::isfinite(v) && std::abs(output[2*i+1]-v*.5) < 1e-7);
                    energy += v*v; ++measured;
                    if (v > 0 && previous <= 0) ++crossings;
                    previous = v;
                }
            }
        }
    }
    if (!tracking) {
        CHECK(q->underruns.load() > 0 || q->rejected.load() > 0);
        std::puts("Negative control: fixed-rate renderer exhausts its queue under clock mismatch");
        return;
    }
    double hz = crossings*static_cast<double>(rate)/measured;
    double rms = std::sqrt(energy/measured), average = correction_sum/correction_count;
    std::printf("PCM virtual %d s at %d Hz / %+.0f ppm: queue %.0f..%.0f, correction %.3f ppm, RMS %.8f, tone %.4f Hz, underruns %llu, rejects %llu\n",
        seconds,rate,ppm,low,high,average,rms,hz,static_cast<unsigned long long>(q->underruns.load()),static_cast<unsigned long long>(q->rejected.load()));
    CHECK(output_frames >= static_cast<uint64_t>(rate)*seconds);
    CHECK(q->underruns.load() == 0 && q->rejected.load() == 0 && q->reprimes.load() == 0);
    CHECK(q->clipped.load() == 0 && q->nonfinite.load() == 0);
    CHECK(low > 1024 && high < 6144);
    CHECK(std::abs(average-ppm) < 20);
    CHECK(std::abs(rms-.5/std::sqrt(2.0)) < .002);
    CHECK(std::abs(hz-1000*(1+ppm/1e6)) < .2);
}

int main(int argc,char **argv) {
    bool soak = argc == 2 && std::string_view(argv[1]) == "--soak";
    if (argc > 1 && !soak) return 2;
    if (soak) { renderer_case(48000,1000,3600); return 0; }
    for (double ppm : {-1000.,-500.,-100.,0.,100.,500.,1000.}) controller_case(ppm,false);
    controller_case(1000,true);
    for (int rate : {44100,48000,96000}) for (double ppm : {-1000.,1000.}) renderer_case(rate,ppm,240);
    renderer_case(48000,1000,240,false);
    renderer_case(48000,-1000,240,false);
    ClockRecovery bounded;
    for (int i = 0; i < 60000; ++i) bounded.update(8192);
    CHECK(bounded.ppm == ClockRecovery::limit_ppm);
    for (int i = 0; i < 60000; ++i) bounded.update(0);
    CHECK(bounded.ppm == -ClockRecovery::limit_ppm);
    auto interrupted = std::make_unique<PcmQueue>(48000,true);
    double input[2*4096]; float output[2*480];
    for (double &v : input) v = .125;
    interrupted->muted.store(0); CHECK(interrupted->write(input,4096) == 4096);
    for (int i = 0; i < 1000; ++i) {
        interrupted->render(output,480); CHECK(interrupted->write(input,480) == 480);
    }
    CHECK(interrupted->correction_ppb.load() != 0);
    interrupted->reset.fetch_add(1); interrupted->render(output,480);
    for (float v : output) CHECK(v == 0);
    CHECK(interrupted->correction_ppb.load() == 0 && interrupted->reprimes.load() == 1);
    CHECK(interrupted->write(input,2048) == 2048); interrupted->render(output,480);
    for (float v : output) CHECK(v == 0); // adaptive output requires fresh 4096-frame prefill
    CHECK(interrupted->write(input,2048) == 2048); interrupted->render(output,480);
    for (float v : output) CHECK(std::abs(v-.125f) < 1e-6);
    for (int i = 0; i < 100; ++i) interrupted->render(output,480);
    CHECK(interrupted->underruns.load() == 1 && interrupted->correction_ppb.load() == 0);
    CHECK(interrupted->write(input,4096) == 4096); interrupted->render(output,480);
    for (float v : output) CHECK(std::abs(v-.125f) < 1e-6);
    PlaybackHealth health; health.start(100);
    CHECK(!health.stalled(1099,0)); CHECK(health.stalled(1100,0));
    health.start(100); CHECK(!health.stalled(600,100)); CHECK(!health.stalled(1599,200));
    CHECK(health.stalled(2599,300)); // host/control pause, even if callbacks advanced
    std::puts("PASS: clock recovery, anti-windup, bounded slew, stereo PCM and callback watchdog");
}
