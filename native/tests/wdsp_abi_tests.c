/* SPDX-License-Identifier: GPL-2.0-or-later */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
extern int ThetisWdspGetAbiInfo(int32_t *, int);
extern int GetWDSPVersion(void);
extern void ThetisWdspSetPlanningTimeLimit(double);
extern void init_impulse_cache(int);
extern void destroy_impulse_cache(void);
extern int ThetisWdspTestNoise(void);
extern int ThetisWdspTestLifecycle(void);
extern int ThetisWdspTestAnalyzer(void);
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed: %s at %d\n", #x, __LINE__); exit(1); } } while (0)
int main(void) {
    int32_t data[10] = {0}; data[9] = 123456;
    CHECK(ThetisWdspGetAbiInfo(NULL, 9) == -1);
    CHECK(ThetisWdspGetAbiInfo(data, 8) == -1);
    CHECK(ThetisWdspGetAbiInfo(data, 9) == 9 && data[9] == 123456);
    CHECK(data[0] == 1 && data[1] == sizeof(void *) && data[2] == 4 && data[3] == 4);
    CHECK(data[4] == 8 && data[5] == 16 && data[6] == 8 && data[7] == 4 && data[8] == 480);
    CHECK(GetWDSPVersion() == 200);
    init_impulse_cache(0);
    ThetisWdspSetPlanningTimeLimit(0); // bounded offline tests, not a wisdom benchmark
    CHECK(ThetisWdspTestNoise() == 0);
    CHECK(ThetisWdspTestAnalyzer() == 0);
    CHECK(ThetisWdspTestLifecycle() == 0);
    destroy_impulse_cache();
    puts("PASS: WDSP 2.00 ABI, NR3/NR4 processing, 20 receiver and sync-buffer lifecycle cycles");
    return 0;
}
