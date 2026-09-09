/* SPDX-License-Identifier: GPL-2.0-or-later */
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>

// One callback writer; control-side snapshot readers. Fixed 100 ms windows of
// actual post-mute software output, including priming/starvation silence.
class OutputLevelMeter {
public:
    explicit OutputLevelMeter(int rate) : window(rate/10) {}
    void frame(double left,double right) {
        const double samples[] = {left,right};
        for (int c = 0; c < 2; ++c) {
            maxima[c] = std::max(maxima[c],std::abs(samples[c]));
            squares[c] += samples[c]*samples[c];
        }
        if (++frames != window) return;
        version.fetch_add(1); // odd while publishing; no non-atomic shared data
        for (int c = 0; c < 2; ++c) {
            values[c].store(static_cast<int64_t>(std::llround(maxima[c]*1e9)));
            values[c+2].store(static_cast<int64_t>(std::llround(std::sqrt(squares[c]/frames)*1e9)));
            maxima[c] = squares[c] = 0;
        }
        frames = 0; version.fetch_add(1);
    }
    bool snapshot(int64_t *result) const {
        // Bounded retries: diagnostics cannot stall the control owner if the
        // callback is suspended during publication. Caller retains last sample.
        for (int attempt = 0; attempt < 4; ++attempt) {
            auto before = version.load(); if (before & 1) continue;
            for (int c = 0; c < 4; ++c) result[c+2] = values[c].load();
            if (before == version.load()) { result[0] = 1; result[1] = before/2; return true; }
        }
        return false;
    }
private:
    const int window;
    int frames = 0;
    double maxima[2]{},squares[2]{};
    std::atomic<int64_t> version{0};
    std::array<std::atomic<int64_t>,4> values{};
};
