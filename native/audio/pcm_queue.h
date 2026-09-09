/* SPDX-License-Identifier: GPL-2.0-or-later */
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include "clock_recovery.h"
#include "level_meter.h"

// One producer, one consumer. Callback does no allocation, locking, managed
// calls, I/O or DSP configuration. Storage and rate tables are prepared on open.
class PcmQueue {
public:
    static constexpr uint64_t capacity = 8192;
    static constexpr int taps = 32, phases = 1024, prime = 1024;
    static_assert(std::atomic<uint64_t>::is_always_lock_free, "Playback requires lock-free 64-bit atomics");
    static_assert(std::atomic<int64_t>::is_always_lock_free && std::atomic<int>::is_always_lock_free,
        "Callback control and diagnostics must be lock-free");
    std::atomic<uint64_t> written{0}, read{0}, submitted{0}, rejected{0}, rendered{0};
    std::atomic<uint64_t> starvation{0}, underruns{0}, driver_underruns{0}, nonfinite{0}, clipped{0}, peak{0};
    std::atomic<int> muted{1}, active{1};
    std::atomic<uint64_t> reset{0};
    std::atomic<int64_t> correction_ppb{0};
    std::atomic<uint64_t> reprimes{0};
    std::atomic<int> fault{0}; // 0 healthy, 1 stopped, 2 driver error, 3 callback timeout, 4 fixture loss
    const int rate;
    const bool clock_tracking;
    OutputLevelMeter levels;
    explicit PcmQueue(int output_rate,bool tracking = false) : rate(output_rate), clock_tracking(tracking), levels(output_rate), ratio(48000.0 / output_rate) {
        const double cutoff = .90 * std::min(1.0,output_rate / 48000.0);
        for (int p = 0; p < phases; ++p) {
            double sum = 0;
            for (int k = 0; k < taps; ++k) {
                double x = k - 15.0 - static_cast<double>(p)/phases;
                double sinc = std::abs(x) < 1e-12 ? cutoff : std::sin(pi*cutoff*x)/(pi*x);
                double window = .42 - .5*std::cos(2*pi*k/(taps-1)) + .08*std::cos(4*pi*k/(taps-1));
                coefficients[p][k] = sinc*window; sum += coefficients[p][k];
            }
            for (double &c : coefficients[p]) c /= sum;
        }
    }
    void fail(int reason) {
        int expected = 0; fault.compare_exchange_strong(expected,reason);
        muted.store(1); active.store(0);
    }
    int write(const double *input,int frames) {
        uint64_t w = written.load(std::memory_order_relaxed), r = read.load(std::memory_order_acquire);
        int count = static_cast<int>(std::min<uint64_t>(frames,capacity-(w-r)));
        for (int i = 0; i < count; ++i) for (int c = 0; c < 2; ++c) {
            double value = input[2*i+c];
            if (!std::isfinite(value)) { value = 0; nonfinite.fetch_add(1,std::memory_order_relaxed); }
            // Large finite input cannot overflow the interpolation accumulator.
            samples[2*((w+i)&(capacity-1))+c] = std::clamp(value,-16.0,16.0);
        }
        written.store(w+count,std::memory_order_release);
        submitted.fetch_add(count,std::memory_order_relaxed);
        rejected.fetch_add(frames-count,std::memory_order_relaxed);
        return count;
    }
    void render(float *output,int frames) {
        uint64_t r = read.load(std::memory_order_relaxed), w = written.load(std::memory_order_acquire);
        uint64_t zeros = 0, clips = 0, max_peak = peak.load(std::memory_order_relaxed);
        for (int i = 0; i < frames; ++i) {
            const uint64_t generation = reset.load(std::memory_order_acquire);
            if (generation != seen_reset) {
                r = w; fraction = 0; priming = true; seen_reset = generation;
                reset_clock(); reprimes.fetch_add(1,std::memory_order_relaxed);
            }
            if (priming && w-r >= static_cast<uint64_t>(clock_tracking ? 4096 : prime)) priming = false;
            if (!active.load(std::memory_order_relaxed) || priming || w-r < taps+2) {
                if (!priming && active.load(std::memory_order_relaxed)) {
                    underruns.fetch_add(1,std::memory_order_relaxed); reprimes.fetch_add(1,std::memory_order_relaxed);
                    priming = true; r = w; fraction = 0; reset_clock();
                }
                output[2*i] = output[2*i+1] = 0; levels.frame(0,0); ++zeros; continue;
            }
            if (clock_tracking) {
                depth_sum += static_cast<double>(w-r);
                if (++clock_frames == rate/100) {
                    double ppm = clock.update(depth_sum/clock_frames);
                    ratio = (48000.0/rate)*(1+ppm/1e6);
                    correction_ppb.store(static_cast<int64_t>(std::llround(ppm*1000)),std::memory_order_relaxed);
                    depth_sum = 0; clock_frames = 0;
                }
            }
            int phase = static_cast<int>(fraction*phases);
            for (int c = 0; c < 2; ++c) {
                double value = 0;
                for (int k = 0; k < taps; ++k) value += coefficients[phase][k]*samples[2*((r+k)&(capacity-1))+c];
                if (std::abs(value) > 1) { ++clips; value = std::clamp(value,-1.0,1.0); }
                max_peak = std::max(max_peak,static_cast<uint64_t>(std::abs(value)*1000000));
                output[2*i+c] = muted.load(std::memory_order_relaxed) ? 0 : static_cast<float>(value);
            }
            levels.frame(output[2*i],output[2*i+1]);
            fraction += ratio;
            auto advance = static_cast<uint64_t>(fraction); r += advance; fraction -= advance;
        }
        read.store(r,std::memory_order_release);
        rendered.fetch_add(frames,std::memory_order_relaxed); starvation.fetch_add(zeros,std::memory_order_relaxed);
        clipped.fetch_add(clips,std::memory_order_relaxed); peak.store(max_peak,std::memory_order_relaxed);
    }
    // Portable numbered-channel routing for WASAPI/ALSA. CoreAudio uses an
    // explicit HAL map and retains the direct stereo callback. No heap allocation.
    void render_routed(float *output,int frames,int channels,int first) {
        if (channels == 2 && first == 0) { render(output,frames); return; }
        std::array<float,2*256> stereo{};
        for (int offset = 0; offset < frames; offset += 256) {
            int count = std::min(256,frames-offset); render(stereo.data(),count);
            float *destination = output + static_cast<size_t>(offset)*channels;
            std::fill_n(destination,static_cast<size_t>(count)*channels,0.f);
            for (int i = 0; i < count; ++i) {
                destination[i*channels+first] = stereo[2*i];
                destination[i*channels+first+1] = stereo[2*i+1];
            }
        }
    }
private:
    static constexpr double pi = 3.14159265358979323846;
    std::array<double,2*capacity> samples{};
    std::array<std::array<double,taps>,phases> coefficients{};
    double ratio, fraction = 0;
    ClockRecovery clock;
    double depth_sum = 0;
    int clock_frames = 0;
    void reset_clock() {
        clock.reset(); depth_sum = 0; clock_frames = 0; ratio = 48000.0/rate;
        correction_ppb.store(0,std::memory_order_relaxed);
    }
    bool priming = true;
    uint64_t seen_reset = 0;
};
