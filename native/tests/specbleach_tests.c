/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "spectral_utils.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failed: %s at %d\n", #x, __LINE__); exit(1); } } while (0)

int main(void) {
    enum { bins = 3, max_blocks = 17 };
    float input[max_blocks * bins];
    float original[max_blocks * bins];
    for (int block = 0; block < max_blocks; ++block) {
        input[block*bins] = -999; // DC is deliberately excluded by the helper.
        input[block*bins+1] = (float)(block+1);
        input[block*bins+2] = 2; // Repeated values, below the existing maximum.
    }
    memcpy(original, input, sizeof(input));

    // Odd/even, single-frame, configured stack workspace and larger heap cases.
    // Repetition exercises fallback cleanup under the leak-enabled sanitizer job.
    for (int repeat = 0; repeat < 100; ++repeat) {
        for (uint32_t blocks = 1; blocks <= max_blocks; ++blocks) {
            float guarded[] = {-12345, 777, -100, 10, -67890};
            CHECK(get_rolling_median_spectrum(guarded+1, input, blocks, bins));
            CHECK(guarded[0] == -12345 && guarded[4] == -67890);
            CHECK(guarded[1] == 777 && guarded[2] == (blocks+1)*0.5F && guarded[3] == 10);
            CHECK(memcmp(input, original, sizeof(input)) == 0);
        }
    }

    float output[] = {777, -100, 10};
    const float unchanged[] = {777, -100, 10};
    CHECK(!get_rolling_median_spectrum(NULL, input, 1, bins));
    CHECK(!get_rolling_median_spectrum(output, NULL, 1, bins));
    CHECK(!get_rolling_median_spectrum(output, input, 0, bins));
    CHECK(!get_rolling_median_spectrum(output, input, 1, 0));
    CHECK(!get_rolling_median_spectrum(output, input, UINT32_MAX, UINT32_MAX));
    CHECK(memcmp(output, unchanged, sizeof(output)) == 0);
    CHECK(get_rolling_median_spectrum(output, input, 1, 1));
    CHECK(memcmp(output, unchanged, sizeof(output)) == 0);
    puts("PASS: median odd/even counts, stack/heap workspaces, canaries and invalid dimensions");
    return 0;
}
