/* SPDX-License-Identifier: GPL-2.0-or-later */
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>

// One producer, one consumer. Callback does no allocation, locking, managed
// calls, I/O or DSP configuration. Storage and rate tables are prepared on open.
class PcmQueue {
public:
    static constexpr uint64_t capacity = 8192;
    static constexpr int taps = 32, phases = 1024, prime = 1024;
    static_assert(std::atomic<uint64_t>::is_always_lock_free, "Playback requires lock-free 64-bit atomics");
    std::atomic<uint64_t> written{0}, read{0}, submitted{0}, rejected{0}, rendered{0};
    std::atomic<uint64_t> starvation{0}, underruns{0}, driver_underruns{0}, nonfinite{0}, clipped{0}, peak{0};
    std::atomic<int> muted{1}, active{1};
    std::atomic<uint64_t> reset{0};
    const int rate;
    explicit PcmQueue(int output_rate) : rate(output_rate), ratio(48000.0 / output_rate) {
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
            }
            if (priming && w-r >= prime) priming = false;
            if (!active.load(std::memory_order_relaxed) || priming || w-r < taps+2) {
                if (!priming) { underruns.fetch_add(1,std::memory_order_relaxed); priming = true; r = w; fraction = 0; }
                output[2*i] = output[2*i+1] = 0; ++zeros; continue;
            }
            int phase = static_cast<int>(fraction*phases);
            for (int c = 0; c < 2; ++c) {
                double value = 0;
                for (int k = 0; k < taps; ++k) value += coefficients[phase][k]*samples[2*((r+k)&(capacity-1))+c];
                if (std::abs(value) > 1) { ++clips; value = std::clamp(value,-1.0,1.0); }
                max_peak = std::max(max_peak,static_cast<uint64_t>(std::abs(value)*1000000));
                output[2*i+c] = muted.load(std::memory_order_relaxed) ? 0 : static_cast<float>(value);
            }
            fraction += ratio;
            auto advance = static_cast<uint64_t>(fraction); r += advance; fraction -= advance;
        }
        read.store(r,std::memory_order_release);
        rendered.fetch_add(frames,std::memory_order_relaxed); starvation.fetch_add(zeros,std::memory_order_relaxed);
        clipped.fetch_add(clips,std::memory_order_relaxed); peak.store(max_peak,std::memory_order_relaxed);
    }
private:
    static constexpr double pi = 3.14159265358979323846;
    std::array<double,2*capacity> samples{};
    std::array<std::array<double,taps>,phases> coefficients{};
    double ratio, fraction = 0;
    bool priming = true;
    uint64_t seen_reset = 0;
};
