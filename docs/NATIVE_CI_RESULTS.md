# Native cross-platform CI results

## M3a — offline ChannelMaster core

Validated source: `3233270486585bb798f0b4570304102a55177ba8`, recorded
2026-09-04 Pacific / 2026-09-05 UTC.
The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33941049031)
passes on all three platforms, including the separate Linux sanitizer job.
Scope and remaining work: [ChannelMaster offline](CHANNELMASTER_OFFLINE.md).

| Target / compiler | Native CTest | .NET session CLI | DSP CLI | Managed tests |
| --- | --- | --- | --- | --- |
| Windows x64 / MSVC 19.51.36256.0 | 3/3 pass; lifecycle 146.39 s | 100 cycles, 139.889 s | 11/11 | 58/58, no skips |
| macOS arm64 / Apple Clang 21.0.0.21000101 | 4/4 pass; lifecycle 73.72 s | 100 cycles, 63.472 s | 11/11 | 58/58, no skips |
| Linux x64 / GCC 13.3.0 | 4/4 pass; lifecycle 104.54 s | 100 cycles, 98.673 s | 11/11 | 58/58, no skips |

Native lifecycle tests include 100 full 8-stream/5-receiver/2-subreceiver/1-TX
sessions, six startup rollback checkpoints, default/custom P2 port selection,
device/TX rejection and shutdown checks. Windows uses Win32 primitives, so only
POSIX targets run the separate platform-primitives test. This does not exercise
the native socket initializer or transport.

The [Linux sanitizer job](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33941049031/job/101238420812)
passed all four tests with address/undefined-behavior checks and leak detection
enabled, without suppressions (100-cycle lifecycle: 188.92 s). Local macOS
ASan/UBSan also passes all four tests. A separate macOS `leaks --atExit` scan
after the EER/CFIR/sidetone fixes reported **0 leaks / 0 leaked bytes**, versus
423 allocations / 10,462,240 allocator-reported bytes before those fixes.

The first M3a Windows run, at `c4af4e9c`, built and passed the existing DSP tests
but failed an exact OS thread-count assertion. The final run explicitly records
the process count decreasing from 4 to 1, consistent with OS/runtime helper
retirement. The check now rejects counts above the startup ceiling, while native
CM worker ownership must return to exactly zero. WDSP channel and PureSignal
background workers retain joinable handles; this is not permission for growing
application-worker counts. Other active-streaming worker paths remain unaudited.

Memory observations are not hard RSS budgets or real-time benchmarks:

| Target | Native RSS: after cycle 10 / final | .NET CLI RSS: after cycle 10 / final |
| --- | --- | --- |
| Windows | 11,149,312 / 142,946,304 bytes | 29,327,360 / 31,084,544 bytes |
| macOS | 1,206,665,216 / 1,206,697,984 bytes | 1,248,657,408 / 1,250,148,352 bytes |
| Linux | 1,251,778,560 / 1,251,799,040 bytes | 1,285,443,584 / 1,288,699,904 bytes |

Linux sanitizer RSS was 526,778,368 / 528,908,288 bytes. Allocator/OS retention
varies substantially (including the Windows native RSS increase); do not infer
general memory stability from one counter or claim a production memory budget.
The original large analyzer allocations remain. Leak detection, exact ownership
checks and resident-memory observations are separate evidence.

Local native and managed tests also pass at the final source revision. A fresh
CLI process with a missing native directory exits 3; real Ctrl-C exits 130 after
cleanup. The [discovery-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33940628425)
passes on all three OSes at `c4af4e9c`: 52 managed tests pass and six native-only
tests skip, as expected without a native library. The follow-up changes are
native/test-diagnostic only; no legacy Windows reference job was dispatched.

**Status:** M3a offline lifecycle checks pass cross-platform. Full M3 remains
partial: RNet/socket lifecycle, actual radio-init integration, active-streaming
shutdown and longer-run resource/performance qualification are still required.
No G2 packets, RX/TX, audio devices, desktop UI or plugin hosting were used.

## M2 — historical qualification

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
