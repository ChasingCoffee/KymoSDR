# Native cross-platform CI results

Validated source commit: `3f5931e6cccb1713e9a67f8583a5d194faafcbc4`.
Recorded 2026-09-04 Pacific (2026-09-05 UTC). These are offline engine results,
not a desktop, radio, audio-device or release qualification.

## Passed on GitHub-hosted runners

The [native DSP workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33939081018)
completed successfully, including all three platform jobs and Linux sanitizers.

| Runner observed in logs | C compiler | Native CTest | .NET DSP self-test | Managed tests with native library |
| --- | --- | --- | --- | --- |
| Windows Server 2025, x64 | MSVC 19.51.36256.0 | 2/2 passed | 11/11 passed | 51/51 passed, no skips |
| macOS 26.6.2, arm64 | Apple Clang 21.0.0.21000101 | 3/3 passed | 11/11 passed | 51/51 passed, no skips |
| Ubuntu 24.04.4, x64 | GCC 13.3.0 | 3/3 passed | 11/11 passed | 51/51 passed, no skips |

Windows uses the original Win32 synchronization primitives, so the separate
POSIX-primitives CTest target applies only to macOS/Linux. All platforms build
FFTW double/float, RNNoise, libspecbleach and WDSP from source, restore the pinned
managed dependencies, and build the .NET 10 harness.

The [Linux sanitizer job](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33939081018/job/101232760944)
passed all three native tests with `UBSAN_OPTIONS=halt_on_error=1:print_stacktrace=1`
and `ASAN_OPTIONS=detect_leaks=1:halt_on_error=1`. No sanitizer suppression or
reduced numerical tolerance was needed. External FFTW archives are not
instrumented; these results do not establish general leak/race freedom or full
DSP feature parity.

The [discovery workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33939081008)
also passed on all three operating systems: managed build/tests, CLI help and
local NIC enumeration. It does not send radio discovery packets. Its optional
legacy Windows reference job was not dispatched. Without a native directory,
three Engine tests are intentionally skipped in that separate managed-only
workflow; the native workflow above runs all 51 tests without skips.

## Failures found and fixed

The [initial run](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33938186114)
exposed two failures. The first fix checkpoint, `6f8b0148`, passed native and
managed checks on all three operating systems but exposed teardown leaks in
the [Linux sanitizer run](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33938784929).

- libspecbleach used a variable-length stack array that MSVC cannot compile.
  Its configured five-frame median path now uses fixed scratch space, with a
  checked heap fallback for larger requests. Invalid and overflowing dimensions
  are rejected. Tests cover 1–17 blocks, odd/even medians, preserved maxima/DC,
  input immutability, output canaries and repeated fallback cleanup.
- The RNNoise adapter copied zero bytes from a null registry pointer on first
  initialization. Empty copies are now skipped. Tests exercise three cycles of
  nine simultaneous instances, registry growth, model reload and non-LIFO removal.
- `destroy_nurbs` and `destroy_notchdb` freed their internal arrays but not the
  containing allocations. Their destructors now release both. Tests include
  100 direct object lifecycles and 20 complete receiver/sync-buffer lifecycles.
  The failing Linux run reported 6,320 leaked bytes in 59 allocations; the final
  leak-enabled run passes.

Original notices and the DSP algorithms remain intact. The local macOS arm64
build also passed all three native tests normally and with ASan/UBSan, plus all
51 managed tests after the final code changes.

## Boundaries and next milestone

- macOS dependency inspection reports arm64 Mach-O and only the system library
  dependency; Linux reports x86-64 ELF with libm/libc and the system loader.
  These are fresh-runner source builds, not packaged-runtime certification.
- MSVC currently selects RNNoise's scalar path. This run establishes functional
  coverage, not real-time performance; SIMD and scheduling qualification remain
  future work.
- No Windows ARM, Linux ARM, Intel Mac runtime, Parallels setup or legacy Windows
  application build was tested here. Runner versions above are observations,
  not minimum-supported-OS promises; `*-latest` runner images can change.
- No radio packets, live RX/TX, audio devices, UI, VST3 or FreeDV were involved.

The initial M2 cross-platform build/load/offline-check gate is met. Next is M3:
ChannelMaster's corrected radio-init ABI and no-radio/no-device lifecycle,
before any G2 receive streaming. See the
[implementation plan](CROSS_PLATFORM_IMPLEMENTATION_PLAN.md).
