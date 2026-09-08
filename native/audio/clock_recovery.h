/* SPDX-License-Identifier: GPL-2.0-or-later */
#pragma once
#include <algorithm>
#include <cmath>

// Consumer-owned, sample-clocked PI servo. Queue depth is averaged over 10 ms
// by the renderer, then low-pass filtered here. No wall clock, locks or memory
// allocation on the callback. Positive ppm consumes source samples faster.
class ClockRecovery {
public:
    static constexpr double target = 3072, limit_ppm = 2000, slew_ppm_per_second = 200;
    double ppm = 0, filtered = target;
    void reset() { ppm = integral = 0; filtered = target; }
    double update(double depth) {
        constexpr double dt = .01;
        filtered += (depth-filtered)*(dt/(1+dt));
        double error = filtered-target;
        double candidate = std::clamp(integral + .05*error*dt,-limit_ppm,limit_ppm);
        // Conditional integration: no wind-up while the actuator is saturated.
        double demand = 2*error+candidate;
        if (std::abs(demand) <= limit_ppm || demand*error < 0) integral = candidate;
        demand = std::clamp(2*error+integral,-limit_ppm,limit_ppm);
        ppm += std::clamp(demand-ppm,-slew_ppm_per_second*dt,slew_ppm_per_second*dt);
        return ppm;
    }
private:
    double integral = 0;
};
