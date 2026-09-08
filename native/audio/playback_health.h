/* SPDX-License-Identifier: GPL-2.0-or-later */
#pragma once
#include <cstdint>

// Control-thread watchdog, never called by the real-time callback. A driver
// may claim to be active after its callbacks cease. Polling must continue.
class PlaybackHealth {
public:
    static constexpr int64_t timeout_ms = 1000;
    void start(int64_t now) { last_poll = last_progress = now; frames = 0; }
    bool stalled(int64_t now,uint64_t rendered) {
        // A suspended host/control pump must not resume old buffered audio.
        bool expired = now-last_poll >= timeout_ms;
        last_poll = now;
        if (rendered != frames) { frames = rendered; last_progress = now; }
        return expired || now-last_progress >= timeout_ms;
    }
private:
    int64_t last_poll = 0, last_progress = 0;
    uint64_t frames = 0;
};
