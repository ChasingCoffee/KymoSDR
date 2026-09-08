# Native cross-platform CI results

## USB/LSB receive mode and filter controls

Validated source: `06416f06ee276687f1cb99ab37a38165a6c615e4`, recorded
2026-09-07. The loopback P2 owner now supports startup and live USB/LSB mode plus
receive-filter configuration, with positive audio-frequency edges, native
settings/generation readback, validation and clean reconnects. The bridge
updates all linked WDSP passbands and explicitly translates the loopback
I/Q/spectrum frequency convention to WDSP's FIR convention. Native DSP
algorithm sources and radio/TX permissions are unchanged.

The [native CI run](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34172762561)
passes Windows x64 (7 native CTests), macOS arm64 and Linux x64 (8 each), with
**145 managed tests per platform and no skips**. The new controls CLI passes
all 12 signal checks on each OS: wanted/opposite sidebands, both filter-edge
rejections, narrow/restored passbands and reopening with LSB settings. The
existing DSP, receive, short soak/fault, 100-cycle CM and 100-cycle transport
CLIs remain green. Linux ASan/UBSan/LeakSanitizer passes all eight native tests;
both 20-cycle reconnect memory guards pass without changing bounds.

The [managed-only run](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34172762512)
passes all three OSes (120 pass / 25 native-dependent skips). Local macOS also
passes 145 tests, eight native CTests, eight ASan/UBSan tests and the production
`BUILD_TESTING=OFF` controls CLI; actual Ctrl-C exits 130 after disposal.
See [the control contract, numerical measurements and limits](RECEIVE_CONTROLS.md)
for exact results. All traffic is owned IPv4 loopback with no TX. This remains
a partial M4 engine checkpoint, not hardware, audio-device or desktop qualification.

## Reconnect allocation-thread fix

Validated source: `63484307e5e492f798c12642bd71cad79985efc4`, recorded
2026-09-07. Full P2/offline native topology open/close now runs on one persistent
engine lifecycle thread, including SafeHandle fallback cleanup. This corrects
the observed Linux allocator amplification when asynchronous reconnects change
API caller threads. Audio/spectrum reads and tuning retain their existing paths;
no native destructor, DSP buffer size or production allocator setting changes.
See [diagnosis, ownership contract and exact memory results](RECONNECT_MEMORY.md).

The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34169904861)
passes at this source:

| Target | Native CTest | Managed tests with native library | Short receive/fault campaign | Final reconnect RSS |
| --- | --- | --- | --- | --- |
| Windows x64 | 7/7 | 139/139, no skips | 32.189 s, all six phases pass | 1,248,714,752 bytes |
| macOS arm64 | 8/8 | 139/139, no skips | 32.754 s, all six phases pass | 1,273,921,536 bytes |
| Linux x64 | 8/8 | 139/139, no skips | 34.395 s, all six phases pass | 1,322,430,464 bytes |

Linux's previous final reconnect RSS was 6,445,420,544 bytes at `bb463658`.
Fresh-process 20-cycle async and six-caller rotating regressions now pass the
new fixed-topology guards: closed allocator-used growth ≤32 MiB above baseline,
and post-cycle-3 RSS/reserved growth ≤128 MiB. Actual worst rotating-caller
growth is 15,204,352 RSS bytes / 180,224 reserved bytes; used growth above
baseline is 3,200,448 bytes. No trim, forced GC or allocator override is used.
The large native allocations are freed on close and reused on subsequent opens.

The 11-check DSP, receive audio/spectrum, 100-cycle CM and 100-cycle transport
CLIs pass on every OS. The Linux sanitizer job passes eight native tests with
ASan/UBSan/LeakSanitizer, without suppressions; it does not instrument the .NET
probe. The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34169904845)
also passes all three OSes: 117 pass / 22 native-dependent skips. The opt-in
legacy Windows-reference build is still skipped.

Local macOS passes all 139 tests, a 20-cycle six-caller allocator probe, and a
120-second steady receive / twenty-reconnect fault campaign (24 passing phases,
180.178 seconds total). The latter ends at 1,518,551,040 bytes RSS, so this is
**not** a claim of zero macOS allocator retention or a reduced full-topology
footprint. The earlier 10-/30-minute checkpoints below are historical sources,
not new long-duration runs of this fix. Desktop resource budgets, P1 and real
hardware RX remain unqualified. All streaming validation here is owned loopback
receive only; no physical radio or hardware TX was exercised.

## Simulator-backed receive endurance and fault campaign

The [campaign](RECEIVE_SOAK.md) now covers sustained receive, repeated retuning,
packet loss, bounded slow-reader behavior, peer disappearance and same-layout
reconnects. Every peer is owned IPv4 loopback; the campaign has no TX, hardware,
audio-device or UI path. No native runtime code changed in this checkpoint.
Validated implementation source: `bb463658f39c86004f5b55e1a91afabb5fd4eb68`,
recorded 2026-09-07. At this earlier checkpoint the harness and finite functional
checks pass, but reconnect memory remains open. The allocation-thread fix above
supersedes that finding; the measurements below are preserved as historical evidence.

### Final-source CI and local regression

The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34167665438)
passes on all three platforms, including the short six-phase receive campaign:

| Target | Native CTest | Managed tests with native library | Full short campaign | Steady spectrum cadence |
| --- | --- | --- | --- | --- |
| Windows x64 | 7/7 | 135/135, no skips | 31.648 s | 19.492 frames/s |
| macOS arm64 | 8/8 | 135/135, no skips | 33.293 s | 17.946 frames/s |
| Linux x64 | 8/8 | 135/135, no skips | 36.192 s | 19.393 frames/s |

Each short campaign includes ten seconds steady receive, a retune, all three
fault phases and two reconnects. All phases pass. Native input overruns and DSP
errors are zero throughout; socket errors occur only for deliberate peer loss
(one per run), missing packets only in the loss phase (345/333/346 respectively),
and audio drops only in the slow-reader phase (31,872/32,704/32,128). Simulator
socket errors are zero in all three reports. STOP/disposal/rebind and no-TX
checks pass. The existing 11-check DSP, receive audio/spectrum, 100-cycle CM and
100-cycle transport CLIs also pass on every OS.

The macOS short steady window records 237 simulator pacing resyncs, versus zero
on Windows/Linux; host scheduling affects synthetic throughput and measured
cadence. The bounded replay prevents large bursts without claiming a hardware
sample clock or identical hosted-runner performance.

The [Linux sanitizer job](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34167665438/job/101881835006)
passes eight native CTests with ASan, UBSan and leak detection, without
suppressions. These instrument native fixtures, not the .NET soak process.
The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34167665425)
passes on all three OSes: 114 tests pass and 21 native-dependent tests skip;
the existing standalone simulator RX/virtual-TX self-tests pass. The legacy
Windows-reference build remains opt-in and skipped.

Local macOS passes 135 managed tests (109 Core, 26 Engine), with no skips or
managed build warnings. The changed native test observers pass all eight CTests
and eight local ASan/UBSan CTests; macOS LeakSanitizer is disabled. Deterministic
clock-jump/high-rate regressions, independent fault allowances, strict CLI
options, partial-report cancellation and observer-error cleanup are included.

A fresh isolated **final-source ten-minute** campaign also passes: 600.003
seconds steady / 637.535 seconds total, 39 retunes, all fault phases and ten
reconnects. It consumes 28,800,000 stereo audio frames and 11,761 spectrum frames
(19.610 produced frames/s, maximum read gap 420.602 ms), with 3200 audio and
11,039 spectrum signal checks. Steady native error/loss/drop counters and
simulator pacing resyncs are zero. Every phase passes shutdown/port-release
checks, with the explicitly expected peer-loss exception; no unsafe request,
watchdog stop or TX packet occurs.

Its process CPU is 83.231 seconds / 13.872% of one core. Sampled steady RSS is
1,260,912,640 → 1,274,429,440 bytes (peak 1,274,445,824); approximate managed live
bytes are 1,039,968 → 1,014,560, with 541,166,560 allocated. Final reconnect RSS
is 1,593,491,456 bytes, so macOS reconnect retention also merits profiling even
though its increase is smaller than Linux's. Raw final-source report:
`artifacts/receive-soak-20260907/bounded-report.json`. The separate earlier
30-minute checkpoint below must not be conflated with this ten-minute run.

### Completed local 30-minute checkpoint

An isolated Release publish of managed runtime source `c755f191` passes on
macOS 26.6.2 arm64, using the existing `BUILD_TESTING=OFF` native runtime build.
This is **before** the Windows timer scope and final high-rate pacing follow-up
described below; it is not a claim of a 30-minute run at the final revision.
The complete command takes 1837.362 seconds, including 1800.003 seconds of
steady observation, three fault phases and ten reconnects (14 phases total).

| Steady-window measurement | Observed value |
| --- | --- |
| Retunes / settled signal checks | 119 retunes; 9598 audio and 33,111 spectrum checks |
| Audio / spectrum consumed | 86,394,368 stereo audio frames; 35,285 spectrum frames |
| Spectrum cadence / largest read gap | 19.607 produced frames/s; 411.249 ms, including retune settling |
| CPU | 225.529 process CPU seconds; 12.529% of one logical core |
| Sampled RSS baseline / final / peak | 1,260,584,960 / 1,275,576,320 / 1,275,576,320 bytes |
| Approximate managed live bytes / allocation | 1,028,088 → 7,585,592 live; 1,605,566,760 allocated over the window |
| Process threads / simulator pacing resyncs | 57 → 63 threads; six resyncs |

Native socket/DSP errors, input overruns, missing/malformed/foreign/late packets
and audio drops are zero during steady receive. The intentional loss phase
records 346 missing packets; slow reading records 31,936 dropped audio frames
and 19 coalesced spectrum frames; peer disappearance records its expected one
socket error. All ten fresh reconnects pass their signal and clean-counter
checks. Every native owner disposes and its port rebinds; STOP is observed for
each live peer, and the disappeared peer records expected failure instead.
No watchdog stop, unsafe request or TX packet occurs.

Steady RSS rises about 14.3 MiB; the final reconnect ends at 1,375,502,336 bytes.
These figures include .NET, simulator, harness and inherited native topology.
They are not DSP-only CPU, calibrated latency, a forced-GC leak test, or a
performance-budget pass. Native test builds ran briefly on the same host during
the steady window; this is endurance evidence, not an isolated benchmark.
The raw report is retained locally at
`artifacts/receive-soak-20260907/paced-report.json` (ignored build artifact).

### Failures found and corrected

The first bounded-replay Windows run at `c755f191` passed native CTest and the
offline lifecycle CLI but timed out in the audio smoke test before reaching the
soak campaign. Coarse Windows timer waits are the suspected cause of insufficient
sample throughput with the new small replay budget. A balanced, simulator-owned
1 ms Windows timer request restores passing receive/short-soak/integration tests
in the [Windows job at 6abf24b5](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34166937632/job/101879742987).
Deadlines and replay bounds are not relaxed. The audio timeout now includes
counters for future diagnosis.

The new [receive-soak campaign](RECEIVE_SOAK.md) exposed a simulator scheduling
defect: after a host pause it replayed 32 packets before rebasing its clock.
At 238 samples per packet, that 7616-sample burst exceeds the 6144-sample CM
input ring. A local sustained run stopped at 676.05 seconds with eight input
overruns and one simulator pacing resync; native socket/DSP errors, missing
packets and audio drops remained zero. Cleanup still disposed both owners,
observed STOP and rebound the native port. A deterministic 100 ms clock-jump
test reproduces the 32-packet burst without sockets.

The first fix at `c755f191` rebased before replaying a large pause, emitting one
packet in that case and allowing eight for smaller jitter. That revision passes
the local 30-minute campaign, but a later hosted macOS 384 kHz regression exposes
starvation under repeated coarse wakeups: 46,336 audio frames produced in eight
seconds, below the smoke test's 56,192-frame requirement, with no socket/DSP
errors or overruns. A deterministic sequence of ten 10 ms wakeups reproduces
only ten packets rather than the safe 80-packet budget.

The final pacing policy emits **at most eight packets per DDC per tick**, then
rebases if still behind. That is 1904 I/Q samples, well within CM's 6144-sample
ring, without the one-packet starvation. Clock-jump tests check the bound,
contiguous sequences, phase continuity and continued progress under repeated
coarse high-rate wakeups; ordinary small jitter still catches up normally.
Resync counters remain visible; native overrun assertions, ring capacity and
signal-measurement deadlines are unchanged. Final-source validation above passes.

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
At `c755f191`, the isolated probe records 10/1000 transient counts on the hosted
Linux sanitizer runner, and 992/1000 locally on macOS (932/1000 under ASan).
Every cycle settles to one thread. The Linux sanitizer job passes all eight
tests with ASan, UBSan and leak detection enabled, without suppressions.

### Open follow-up: Linux reconnect memory

**Historical status at `bb463658`:** the follow-up described here is addressed
by the [allocation-thread fix and measured regressions](RECONNECT_MEMORY.md).
General desktop resource qualification and macOS allocator retention remain
separate limits; the paragraphs below document why the investigation was needed.

The short campaign at `c755f191` passes its signal, fault and cleanup checks on
Linux, but its process RSS increases from 1,255,747,584 bytes at the steady
baseline to 6,445,428,736 bytes at the end of the second reconnect. Most of that
growth occurs between separately opened phases, not within the 10-second
steady window (+13,201,408 bytes). The approximate managed heap remains small.
The same runner's synchronous 100-cycle native CM fixture is nearly flat after
warm-up (1,251,774,464 → 1,251,782,656 bytes); the managed offline lifecycle CLI
also remains near its warm baseline (1,285,849,088 → 1,289,093,120 bytes).
The final-source Linux short campaign at `bb463658` repeats the growth:
1,253,744,640 bytes at its steady baseline → 6,445,420,544 bytes at the end of
reconnect 2. The pacing fixes do not resolve this resource issue.

At that checkpoint this was **unresolved reconnect memory growth**, not a proven leak or harmless
cache. Native fixture LeakSanitizer success does not qualify the asynchronous
managed campaign's memory behavior. Allocator arena reuse and allocation/free
thread placement are hypotheses to compare with retained live native blocks;
[glibc's allocator controls](https://sourceware.org/glibc/manual/latest/html_node/Memory-Allocation-Tunables.html)
provide diagnostic levers, not an established fix. No allocator override,
forced trim/GC, topology reduction or memory-limit relaxation was added.
That evidence prompted the isolated allocator/thread-placement experiment and
subsequent fix above. It did not justify treating native leak checks alone as
memory qualification for the asynchronous managed campaign.

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
