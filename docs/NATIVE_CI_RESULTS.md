# Native cross-platform CI results

## Receive-endurance qualification: fixes under validation

The new [receive-soak campaign](RECEIVE_SOAK.md) exposed a simulator scheduling
defect: after a host pause it replayed 32 packets before rebasing its clock.
At 238 samples per packet, that 7616-sample burst exceeds the 6144-sample CM
input ring. A local sustained run stopped at 676.05 seconds with eight input
overruns and one simulator pacing resync; native socket/DSP errors, missing
packets and audio drops remained zero. Cleanup still disposed both owners,
observed STOP and rebound the native port. A deterministic 100 ms clock-jump
test reproduces the 32-packet burst without sockets.

The simulator now rebases **before** emitting an over-budget replay, with a
maximum eight-packet catch-up for small jitter. The clock-jump regression now
expects one packet, contiguous sequence numbers and continuous synthetic signal
phase; another test retains normal small-jitter catch-up. Pacing resyncs remain
visible in reports. Native input-overrun assertions and ring capacity are
unchanged. A fresh 30-minute qualification and cross-platform CI are pending;
the failed run is not endurance qualification.

### POSIX post-join OS thread observations

A separate Linux sanitizer lifecycle failure reported two process threads
against a one-thread baseline after the native owned-worker count reached zero.
There was no sanitizer diagnostic. Linux OS accounting, like the previously
observed macOS case, need not be settled at the instant a join completes:
[glibc 2.39's join implementation](https://github.com/bminor/glibc/blob/glibc-2.39/nptl/pthread_join_common.c)
waits for the kernel to clear the thread ID. Linux v6.11 clears it and wakes the
joiner in [mm_release](https://github.com/torvalds/linux/blob/v6.11/kernel/fork.c),
called during `exit_mm`, before `exit_notify`/`release_task`/`__exit_signal`
decrement `nr_threads` in [the exit path](https://github.com/torvalds/linux/blob/v6.11/kernel/exit.c).
The [proc status implementation](https://github.com/torvalds/linux/blob/v6.11/fs/proc/array.c)
uses that thread count. These pinned source versions explain the possible
observation race; they are not a claim about the runner's exact kernel version.

The test-only observation helper now also polls on Linux, at most 100 times
with 1 ms requested sleeps, when the count initially exceeds its baseline.
It does not replace any joins or immediate native ownership assertions. A
1000-cycle plain no-op pthread probe checks successful joins, reports transient
OS counts and requires settling after every cycle, without opening WDSP/CM.
The existing deliberately held live-worker check must still detect that worker
after the complete grace period. A persistent extra thread still fails.

## Renderer-independent P2 receive spectrum frames

Validated source: `76b0d13a9e00b3f1bab13df8b3f48ede5d402302`, recorded
2026-09-07. The existing loopback receive session now publishes spectrum frames
from CM RX0's pre-demodulation input using its existing WDSP analyzer. See the
[frame contract](P2_RECEIVE_INTEGRATION.md#spectrum-frame-contract) for ownership,
frequency-axis mapping, tuning-generation semantics and uncalibrated units.
No renderer, audio device, physical radio or native TX path is exercised.

The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34161509656)
passes on all three OSes:

| Target | Native CTest | Managed tests with native library | Receive CLI audio before / after tuning |
| --- | --- | --- | --- |
| Windows x64 | 7/7 | 121/121, no skips | 998.31 / 1500.17 Hz |
| macOS arm64 | 8/8 | 121/121, no skips | 998.28 / 1500.17 Hz |
| Linux x64 | 8/8 | 121/121, no skips | 996.22 / 1500.18 Hz |

All three CLI runs report the same spectrum peak offsets (+984.375 / +1500 Hz),
RF positions and levels listed below. Sequence and tuning generation advance;
native socket/DSP errors, CM input overruns, audio drops and the simulator's
aggregate socket errors are zero in these runs. STOP and no-TX assertions pass.
The [Linux sanitizer job](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34161509656/job/101864137152)
passes all eight native tests with ASan, UBSan and leak detection enabled,
including active UDP/CM/WDSP spectrum processing. No suppressions were added.
The existing 11-check DSP and 100-cycle CM/transport CLIs also pass on each OS.

The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34161509556)
passes on all three OSes: 102 tests pass, 19 native-dependent tests skip, and
standalone simulator RX/TX self-tests pass. The legacy Windows-reference build
remains an opt-in, skipped job; this is not legacy application parity evidence.

Local macOS arm64 validation passes:

- Release build with no managed warnings; 121/121 managed tests (97 Core, 24
  Engine), with no skips; eight native CTests and eight ASan/UBSan CTests.
- The receive CLI's schema-2 spectrum peaks are +984.375 Hz and +1500 Hz for
  expected +1 kHz/+1.5 kHz offsets; bin spacing is 46.875 Hz. Peak RF frequencies
  are 14,199,984.375 and 14,200,000 Hz, within one bin of the 14.200 MHz tone.
  Levels are -12.67 and -12.04 uncalibrated dB for 0.25-amplitude I/Q. Sequence
  advances across retuning and generation changes from 1 to 2. Audio remains
  approximately 1/1.5 kHz and RMS 0.17678. Native socket/DSP/input-overrun/audio-drop
  counters are zero, STOP is observed and simulator PTT/TX remain off/zero.
- All four input rates (48/96/192/384 kHz), DDC selection, rapid retuning to a
  negative offset, copied-frame lifetime, slow readers, concurrent disposal,
  cancellation/rollback and SafeHandle cleanup remain covered.
- The native fixture checks coherent positive/negative/DC/edge tones, zero input,
  exact FFT bin mapping and level scaling, ABI capacity/canaries, coalescing and
  pending-frame invalidation on tune. Three active packet/audio/spectrum cycles
  close with no remaining owned workers or OS-thread growth.
- A separate `leaks --atExit` scan reports zero leaks after the active fixture;
  detailed inspection is security-limited. The macOS sanitizer run disables
  LeakSanitizer; the independent leaks scan is separate evidence.
- A `BUILD_TESTING=OFF` native build passes the complete audio/spectrum CLI.
  Actual Ctrl-C during streaming exits 130 after both owners are disposed.

The analyzer runs synchronously on the joined CM worker using existing WDSP
kernels, without additional async FFT workers or a managed DSP rewrite. The
4095-pixel axis deliberately accounts for WDSP omitting the negative Nyquist
bin; the new signed/DC/edge-bin fixture catches off-by-one mapping errors.
This is finite simulator qualification, not M4 completion: P1, hardware RX,
reference parity and long-run performance/latency budgets remain outstanding.
Earlier records below describe their original source and scope.

## P2 simulator → native ChannelMaster/WDSP receive

Validated source: `aa124b5ebca90ec5b2256404931f94f884cbc58d`, recorded 2026-09-07.
The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34159409716)
passes on Windows x64, macOS arm64 and Linux x64. This is the first test that
feeds simulator I/Q through the native P2 decoder, inherited CM router/input
worker and WDSP RX0/sub0, then measures recovered audio. It is loopback-only,
receive-only and pre-mixer; no physical radio, audio device or native TX engine
is exercised. See [implementation and commands](P2_RECEIVE_INTEGRATION.md).

| Target | Native CTest | Managed tests with native library | Receive CLI tone before / after retune |
| --- | --- | --- | --- |
| Windows x64 | 7/7 | 119/119, no skips | 998.20 / 1500.18 Hz |
| macOS arm64 | 8/8 | 119/119, no skips | 998.48 / 1500.16 Hz |
| Linux x64 | 8/8 | 119/119, no skips | 996.22 / 1500.18 Hz |

All receive CLI measurements satisfy the ±20 Hz tolerance around 1 kHz/1.5 kHz;
RMS is approximately 0.17678 for a 0.25-amplitude input with unity gain. Native
socket/DSP errors, CM input overruns and audio queue drops are zero in these
three CLI runs. STOP is observed and simulator PTT/TX packet counts stay off/zero.
The final Windows simulator snapshot separately reports four aggregate peer
socket errors. Individual error codes/timestamps are not retained, so this is
not a claim that every simulator UDP reply succeeded; in-flight replies can
overlap native socket teardown. The native receive measurements and shutdown
assertions pass. Finer peer-error attribution remains a diagnostic improvement.

Each job also passes the 11-check DSP CLI and existing 100-cycle CM and socket
probe CLIs. The 119 tests comprise 97 Core/simulator/CLI and 22 Engine tests.
The [Linux ASan/UBSan job](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34159409716/job/101857891816)
passes all eight native tests with leak detection enabled, including real UDP
packet decoding/routing/WDSP, malformed/sequence fixtures and three active RX
cycles. No sanitizer suppressions were added.

The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34158894066)
passes on all three OSes at implementation source
`7e5aff6a34076ed64e1364e21cd83cbf47f60fb9`: 101 tests pass, 18 native tests skip,
and standalone RX/TX simulator self-tests pass. The subsequent `aa124b5e` changes
only native test observations and documentation, not managed or runtime code.

Local macOS also passes 119 managed tests, all eight native tests and all eight
ASan/UBSan tests. A separate `leaks --atExit` scan of the active receive fixture
reports zero leaks (the tool warns that detailed inspection is security-limited).
A `BUILD_TESTING=OFF` runtime build passes `receive-selftest`; actual Ctrl-C
during streaming exits 130 after both owners are disposed. Native dependencies
and NuGet versions are unchanged; lockfile changes are project references only.

The first hosted macOS run exposed a transient OS thread count in the existing
offline lifecycle test. A standalone no-op pthread reproduced a count of two
immediately after successful `pthread_join`, falling to one after 1 ms, without
loading WDSP/CM. `aa124b5e` adds a bounded macOS observation window and a test
that a deliberately held live worker is still detected. Actual worker joins
and immediate zero owned-worker assertions are retained. Separately, runtime
work in `7e5aff6a` makes WDSP's previously detached flush worker joinable and
bounds the portable CM input ring so unread data cannot be overwritten.

This does not complete M4: spectrum, P1 receive, live G2 RX, broader protocol/
multi-receiver support and long-run performance qualification remain pending.
The G2's receive-only ANT1 restriction and simulator-only TX authorization are
unchanged. Earlier checkpoint results below retain their original test scopes.

## Virtual TX sink checkpoint — simulator-only transmission

Validated source: `fd05b91876c98fc7e46891f0b0f7ce324150021f`, recorded 2026-09-07.
The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34154757862)
passes on Windows x64, macOS arm64 and Linux x64. Each runs 110 managed tests
with no skips (95 Core/simulator plus 15 Engine), the 11-check DSP CLI, and
100-cycle CM and loopback transport CLIs. Native CTest passes 5/5 on Windows and
6/6 on macOS/Linux. The separate
[Linux sanitizer job](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34154757862/job/101844167672)
passes all six existing native tests with ASan/UBSan and leak detection.

The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34154757832)
also passes on all three OSes: 98 tests pass, 12 native-only checks skip, and both
standalone RX and virtual TX self-tests pass without native libraries. Each TX
test sends 100 packets / 24,000 synthetic keyed samples, checks the expected
normalized levels and zero sequence errors, deasserts PTT, checks unkeyed sample
discard and stops. Local macOS also passes 110 tests and ten consecutive TX CLI
tests; real Ctrl-C closes an opt-in sink with exit 130.

The extension changes no native implementation and does not exercise the
application's native transmit path, a live radio or an audio device. All TX packets target only the
simulator-created loopback peer. The physical G2 remains receive-only on ANT1;
simulator TX approval does not authorize hardware TX. See
[virtual TX scope and limits](G2_SIMULATOR.md#virtual-transmit-sink): no RF power,
CW keyer, EER, PureSignal, FIFO model or TX-to-RX feedback qualification.

## G2 simulator checkpoint — existing native regressions

Validated source: `acc1638fd06a5ed6b3ed0c5ec04950b60e8fe4cf`, recorded 2026-09-07.
The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34153221164)
passes after adding the standalone managed G2/P2 receive simulator. No native
implementation changed in this checkpoint.

| Target | Native CTest | Managed tests with native library | Existing DSP / lifecycle CLIs |
| --- | --- | --- | --- |
| Windows x64 | 5/5 | 95/95, no skips | 11 DSP checks; 100 CM and 100 transport cycles pass |
| macOS arm64 | 6/6 | 95/95, no skips | 11 DSP checks; 100 CM and 100 transport cycles pass |
| Linux x64 | 6/6 | 95/95, no skips | 11 DSP checks; 100 CM and 100 transport cycles pass |

The [Linux sanitizer job](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34153221164/job/101839603325)
also passes all six existing native tests with ASan/UBSan and leak detection.
The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34153221191)
passes on all three OSes: 83 tests pass, 12 native-only checks skip, and each
standalone simulator CLI receives 100 sequenced I/Q packets plus mic silence and
status without a native library.

The simulator's scope, local repeatability checks and Windows port-allocation
fix are recorded in [G2 simulator validation](G2_SIMULATOR.md). Simulator sockets
and all test clients are loopback-only; no G2, LAN peer, audio device or transmit
path was exercised. The native P2 packet parser is not connected to this peer
yet. These results preserve the M3a/M3b boundaries below rather than establishing
a working native radio receive path.

## M3b checkpoint — RNet and loopback socket lifecycle

Validated source: `21f7203de38c3c9576cf7f91a6d5d2a80a922d1c`, recorded
2026-09-04 Pacific / 2026-09-05 UTC.
The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33943107358)
passes on all three platforms, including Linux ASan/UBSan/LeakSanitizer without
suppressions. See the [implementation boundary](TRANSPORT_LOOPBACK.md).

| Target / compiler | Native CTest | .NET transport CLI | .NET CM session CLI | Managed tests |
| --- | --- | --- | --- | --- |
| Windows x64 / MSVC 19.51.36256.0 | 5/5; loopback 7.20 s | 100 cycles, 8.297 s | 100 cycles, 121.855 s | 68/68, no skips |
| macOS arm64 / Apple Clang 21.0.0.21000101 | 6/6; loopback 17.97 s | 100 cycles, 16.633 s | 100 cycles, 63.376 s | 68/68, no skips |
| Linux x64 / GCC 13.3.0 | 6/6; loopback 5.61 s | 100 cycles, 6.082 s | 100 cycles, 98.378 s | 68/68, no skips |

The existing DSP CLI also passes its 11 checks on each OS. Native CM lifecycle
times are Windows 118.09 s, macOS 70.63 s and Linux 103.55 s. The new tests cover
21 RNet allocation failure boundaries, actual seven-argument P1/P2 socket
initialization, P2 port relocation, ten signed startup checkpoints, partial
event/worker failure, exclusive bind/rebind and joined reader/timer teardown.
The fixture reader does not parse radio packets or produce DSP input. Each
transport CLI run counts 300 received and 100 oversized fixture datagrams.

Native loopback resource observations after ten warmups / 100 further cycles:

| Target | Process threads | Descriptors / Windows process handles | Resident bytes |
| --- | --- | --- | --- |
| Windows | 4 / 4 | 81 / 81 handles | 5,304,320 / 5,304,320 |
| macOS | 1 / 1 | 3 / 3 descriptors | 1,753,088 / 1,769,472 |
| Linux | 1 / 1 | 6 / 6 descriptors | 2,981,888 / 2,981,888 |
| Linux sanitizer | 1 / 1 | 6 / 6 descriptors | 51,789,824 / 63,651,840 |

The [sanitizer job](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33943107358/job/101244168536)
passes all six native tests; loopback takes 5.77 s and the CM core 176.03 s.
RSS is diagnostic, not a hard memory budget: allocator/sanitizer retention and
instrumentation differ. No continuing thread/descriptor growth was observed in
these finite tests; this is not general leak freedom or a real-time guarantee.

Local macOS also passes all six native tests, all 68 managed tests, and ASan/
UBSan. Separate `leaks --atExit` runs of both `rnet_tests` and
`cm_transport_tests` each report **0 leaks / 0 leaked bytes**. The transport scan
records threads 1/1 and descriptors 4/4; ordinary local CTest records 1/1 and
3/3, reflecting a different launch context. A separate `BUILD_TESTING=OFF` build
passes the 100-cycle transport CLI with the fault-injection export absent.
Real Ctrl-C during the transport CLI exits 130 after cleanup; a fresh process
with a missing native directory exits 3.

The [discovery-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/33943107525)
also passes on all three OSes: 56 tests pass and 12 native-only tests skip, as
expected without a native library. No legacy Windows reference build was run.

**Status:** RNet/socket allocation and loopback lifecycle checks pass
cross-platform. Full M3 remains partial: real P1/P2 packet-worker shutdown,
active-stream callback/control safety and longer-run resource/performance
qualification are still required. All fixture traffic stayed on IPv4 loopback;
the native probe has no sender. No G2 packets, radio RX/TX or audio devices were
used. The G2's receive-only ANT1 restriction remains in force.

## M3a — prior offline ChannelMaster core qualification

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
