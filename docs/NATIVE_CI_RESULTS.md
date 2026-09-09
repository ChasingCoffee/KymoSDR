# Native cross-platform CI results

## Hosted validation branch — initial CI findings

Commit `1ac83e8b` was published to `validation/g2-rx-audio-endurance`.
[Discovery/portable CI](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34299591587)
passes on Windows/macOS/Linux. The initial
[native run](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34299591465)
fails: Linux Release and sanitizer builds expose the new G2 fixture's missing
explicit `libm` link (`llround`), and macOS/Windows fail its sideband RMS gate.
No default-branch merge or hardware test is performed.

The follow-up links the G2 test executable with `m` on non-Windows platforms,
matching the existing native signal tests. The two-second sideband fixture had
discarded only 4096 PCM frames and sent one I/Q packet per sleep, making sample
coverage dependent on host timer/scheduling behavior. It now discards 48000
produced PCM frames (matching established control/gain fixtures), supplies
bounded four-packet batches within a six-second native deadline, and requires
at least 12000 measured frames plus clean transport/DSP/overrun counters. The
wanted amplitude and unwanted-sideband RMS limits are unchanged. Logged RMS,
sample counts and counters make any future failure inspectable.

A separate `g2_receive_coarse_timer` case uses a deliberate 16 ms fixture timer.
Both standard and coarse cases pass locally in 63.89 s total: wanted RMS
0.00707106613, unwanted RMS 1.45534826e-10, with 24832–25344 measured frames
even in the coarse case and zero loss/input-overrun/audio-drop counters.
This is sample-chain/fixture evidence, not a physical playback timing result.
The native workflow includes both cases, increasing the full native suite from
15 to 16 tests. No production receiver/audio implementation or safety gate is
changed by this follow-up. Hosted revalidation is pending.

## Controlled G2 endurance and CI catch-up infrastructure — local working tree

The next 2026-09-08 increment adds `g2-soak`: explicit ANT1 and extended-RX
confirmations, a bounded 60–3600-second continuous phase, and optionally 0–10
ten-second reconnect phases. It uses the existing desktop receive/playback
controller, fresh identity/idle preflight per connection, no-device/muted defaults
and a separately selected native endurance export. The original native opener
and normal desktop/listen paths remain limited to 60 seconds. No TX controls,
automatic fallback, deadline renewal or persisted extended-run consent is added.
See the [contract and staged procedure](G2_HARDWARE_RECEIVE.md#controlled-longer-receive-campaign).

A new report path is reserved before touching any devices/network. Success,
failure and cancellation retain per-phase counters plus bounded CPU/memory and
receive/output diagnostics after disposal is attempted. No addresses, device
names or waveform arrays are exported. Polled spectrum progress is not rendered
cadence; a no-device run cannot qualify physical playback.

Locked dependency restore and Release build/publish pass with zero build
warnings/errors. **270/270 managed cases pass**, with no skips: Core/CLI 148
(6 s), Desktop 48 (45 s), Engine 74 (4 m 36 s, including the independent confined
P1 reference). The 19 new Core cases use an injected session and virtual clock,
including a virtual hour plus ten reconnects, early/missing deadlines, stalled
progress, packet loss versus application overruns, key/driver/nonfinite faults,
failed idle confirmation, cancellation/startup/cleanup failures and protected
existing report files. These do not run an hour of DSP or contact a radio.
Results: `artifacts/test-results/g2-endurance-full` and `g2-soak-focused`.

All **15/15 native CTests pass** in **101.25 s**, including extended-duration
boundary checks, a production extended-open cancellation before any socket can
open, all-stage rollback, and loopback key/status/IQ fail-close cases with an
hour limit. The actual short native deadline and exact one-hour deadline
arithmetic are separately checked. Native packet allowlist/orientation and
existing playback/clock/fake-driver suites remain in the full run. JUnit result:
`artifacts/native/receive-validation.xml`.

All **5/5 targeted native ASan/UBSan cases pass** in **35.64 s** on the final
source, with `halt_on_error=1` and macOS `detect_leaks=0` (not LeakSanitizer or
full sanitizer-suite qualification). The first final rerun overlapped the
regular suite's exclusive loopback ports and failed to bind its fixture; the
serial rerun after regular-suite completion passes without a code workaround.
Do not run separate G2 CTest builds concurrently on the same host. Sanitizer
JUnit: `artifacts/native-asan/receive-validation.xml`.

The separate developer bundle `artifacts/g2-endurance` includes the new headless
command and production (`BUILD_TESTING=OFF`) native libraries. The production
WDSP exports the new endurance opener and no G2 test openers. SHA-256:

- WDSP: `5ade1786e0b1239c31179ea32fc4ba3b8b87a8bfe1cffc8dc2fe557c61b16765`
- Audio: `1d630cfcd78e2acd44f803c57bb7bfddcffa5af7df3ac958e9e255845b383f2d`

Published `g2-soak --help` succeeds without hardware. The production bundle's
P1/P2 `playback-selftest` passes in **12.021 s**, with loopback-only transport,
no physical output and clean receive/output counters. This is an ordinary
simulated playback smoke, not a physical or hour-long endurance result.

The existing `artifacts/desktop` publish is not overwritten by this increment.
No G2 discovery/receive, physical output stream, sleep/wake exercise or hardware
TX is performed. Long hardware qualification remains open.

The native workflow now runs a named G2/discovery/preferences/live-output
checkpoint early on Windows/macOS/Linux, checks nonzero TRX totals with all
cases passing (no skips), and requires the expected test classes to be present.
Focused CTest JUnit/TRX and full managed reports are retained, including failures.
Only `g2-soak --help` runs as a CLI smoke in hosted CI; hardware campaigns are
never launched by the workflow. The discovery-only workflow retains its TRX
artifacts separately, with native-dependent skips explicitly expected there.
Both workflow YAML files parse locally; the new PowerShell checkpoint and
Windows/Linux builds still need hosted execution after the working tree is
committed/pushed. No new hosted qualification is claimed. Read-only GitHub
inspection still shows the preceding successful native/discovery runs at
`f90de9a9`, not this increment. A stopped Parallels Windows 11 VM was listed,
but no guest was started or modified.

## System-default output and live handoffs — local working tree

The next 2026-09-08 increment selects the system's default **output** on first
interactive launch with new valid preferences, without opening a stream. Saved
devices and explicit No-device choices take precedence later. Old profiles
without the additive `audioSelectionInitialized` field preserve their previous
selection; invalid/protected settings and no-settings/automation do not trigger
automatic output enumeration. Native metadata adds `ThetisAudioDefaultOutputDevice`
without changing the existing playback/routing ABI or state-array layouts.

While receiving, device/pair choices are drafts until **Switch output**. The
same radio and receive pump stay active; old audio is muted/closed before new
audio opens, while PCM continues to drain without queuing a delayed backlog.
Applied gain/mute/tuning persist, and meter/rate/resampler state resets. Connected
Refresh uses the same handoff to re-enumerate/revalidate the active route, never
following a different system default. Failure stops receive safely with no
alternate-device fallback. Output generation and discarded-handoff-frame
counters disambiguate output resets from continuous receive-session counters.

Local Release build/publish has zero warnings/errors. **251/251 managed cases
pass**, no skips: 129 Core/CLI (5 s), 74 Engine (4 m 37 s including confined
independent P1 reference), 48 Desktop (45 s). The seven new engine cases cover
both P1/P2, 44.1/48/96 kHz no-device outputs, pair selection, silent monitor,
continuous receiver identity/session counters, gain/mute preservation, a blocked
open with continued PCM draining/spectrum progress, failure without fallback,
disconnect cancellation and hardware-deadline fixtures. Four new desktop cases
cover first-run default metadata, remembered No-device/missing-output behavior,
automation, explicit live switch and unapplied receive-control preservation.
Reports/captures: `artifacts/test-results/live-output-full`.

All **3/3 targeted native audio CTests pass** (4.17 s), including a fake
default-device lookup that verifies no stream open, and **3/3 targeted ASan/UBSan
audio CTests pass** (10.23 s, halt-on-error, `detect_leaks=0` on macOS). These are
not new full native-suite or LeakSanitizer results. Production native audio is
rebuilt and copied into `artifacts/desktop/native`, with matching SHA-256 hashes.
The refreshed publish passes a native-window smoke run in 2.042 s, with 20
displayed/rendered frames, muted no-device output and clean receive/output
counters. Screenshots include the 900×700 headless layout and native window.

Read-only real CoreAudio enumeration reports the **828** as the system default
(index 6, 32 channels, first pair 1–2). No physical stream is opened for that
check. All execution tests use fixtures/simulators and no-device audio: actual
physical-device handoff audibility, latency and unplug behavior remain manual
checks. No G2 receive connection, network discovery or hardware TX was performed
for this increment. These are local working-tree results, not new hosted
Windows/Linux qualification.

## G2 discovery and remembered selections — local working tree

The next 2026-09-08 increment adds the requested **Discover radios · Ethernet**
picker, remembered G2 connection form/source, and remembered audio interface/
stereo pair. Discovery is explicit, P2-only, Ethernet-only, and bounded to four
seconds; results never start receive or auto-select even a single radio. Every
Connect still performs the strict targeted identity/idle preflight and requires
fresh ANT1 consent. Startup remains disconnected, muted and AF −40 dB.

Preferences schema 2 migrates existing schema 1 in memory. The saved audio
bookmark has no device index: fresh name/backend/layout/pair metadata must match
uniquely. Missing/ambiguous/changed outputs remain unselected with a visible
message; the saved choice survives until explicitly replaced or forgotten.
Settings may contain addresses/MACs and device/channel names; diagnostic exports
do not include these bookmarks. Automated campaigns ignore settings and cannot
invoke LAN discovery or metadata enumeration.

The Release solution builds with zero warnings/errors. **240/240 managed cases
pass**, no skips: 129 Core/CLI (6 s), 67 Engine (4 m 32 s, including the confined
independent P1 reference), 44 Desktop (44 s). Nine new cases cover strict schema
migration/validation, changed device indices, duplicate/missing/changed outputs,
remembered safe startup, forgetting an unplugged output, deliberate discovery
selection, busy/profile/subnet policy, automation/no-settings guards and joined
discovery cancellation on window close. The initial targeted run exposed a
test-fixture cleanup race with asynchronous window-close saving; waiting for
the shown window's completed close fixes it. Full suite results are under
`artifacts/test-results/connection-preferences-full`; final desktop assertions
and captures are under `artifacts/test-results/connection-preferences-verified`.
Native/audio implementation is unchanged from the preceding checkpoint; no new
native or sanitizer qualification is claimed for this managed-only increment.

A separate live **discovery-only** subnet scan on verified Ethernet `en7`
(`169.254.47.65/16`) completes in **830 ms**, sends two P2 requests and receives
two replies (one unique radio). It finds the idle G2 at `169.254.187.120`, MAC
`2C-CF-67-FC-A3-DF`, Saturn/code27/beta50/protocol43/10 DDCs, with no socket error,
malformed/subnet rejection or deadline. No receive-start, audio stream or TX is
used. This exercises the shared discovery backend on Ethernet; picker tests use
injected metadata and do not constitute a live GUI-to-radio connection test.

The production developer publish under `artifacts/desktop` is refreshed, with
native hashes matching the preceding production audio/WDSP build. Local
**native-window simulator smoke passes**: 21 distinct displayed/rendered frames
in **2.061 s**, muted no-device output and no receive/output errors. The earlier
macOS render-timer failure is not reproduced in this session. No hardware RX,
physical listening or TX was performed, and no old running user app was stopped.
After the final picker-population guard/privacy assertions, all 44 desktop
tests pass again and the refreshed publish passes native-window smoke again
(20 displayed/rendered frames in 2.049 s, clean muted no-device counters).
These are working-tree results, not new hosted Windows/Linux CI results.

## Stereo output pairs, meters and listening controls — local working tree

The next 2026-09-08 increment adds explicit adjacent stereo output pairs,
CoreAudio channel names/maps, portable sparse-channel output and a bounded
100 ms post-mute L/R RMS/peak meter. Playback ABI 2 remains unchanged, with an
additive routing/meter ABI 1. Desktop controls show pending AF edits and offer
an explicit G2 listening preset (AF −10 dB, medium AGC / max 80), preserving
applied tuning and conservative muted reconnect. These are software controls;
no hardware mixer/system volume or radio packet policy changes were made.

Local macOS arm64 validation:

- Release solution build: zero warnings/errors. Production and test native
  audio libraries rebuild successfully.
- **231/231 managed cases pass**, no skips: 129 Core/CLI (6 s), 67 Engine
  (4 m 32 s, including confined independent P1 reference), 35 Desktop (44 s).
  Initial broad testing caught a 900×700 plot-height regression. Compact control
  spacing fixes it; the complete 35-case desktop rerun passes, with additional
  bounds assertions for both meter bars and listening/apply buttons. Results:
  `artifacts/test-results/audio-controls-full` (Core/Engine passes, original
  desktop failure), `audio-controls-layout` and `audio-controls-desktop-final`.
- **15/15 Release native CTests pass** in 104.17 s. Fake-driver routing covers
  channels 1–2, 11–12, 13–14 and 127–128, zeroes unselected callback channels,
  checks buffer canaries and muted callbacks, and rejects stale channel counts,
  odd/out-of-range/unsupported pairs without fallback. Mac fixtures separately
  verify the explicit CoreAudio map and stereo callback width.
- **3/3 targeted audio ASan/UBSan CTests pass** in 9.23 s, halt-on-error enabled.
  The first attempt with `detect_leaks=1` was rejected by the macOS sanitizer
  runtime before tests; rerun uses `detect_leaks=0`. This is not LeakSanitizer
  qualification or a new full sanitizer-suite result.
- Rolling meters pass 44.1/48/96 kHz fixtures, stereo imbalance, a level step,
  mute/silence/reset/reopen and ABI capacity checks. The lifetime peak test
  explicitly allows the FIR resampler's step transient while requiring the
  current-window peak/RMS to settle to the new lower input.
- Read-only production enumeration reports the 32-channel **828**, 16 supported
  pairs, including Main Out 1–2 and Phones 1/2 on 11–12 / 13–14. No stream was
  opened. Actual headphone routing/driver behavior still needs manual listening.

The published developer app in `artifacts/desktop` is refreshed; staged audio
and WDSP library hashes match the production build. The new native-window smoke
cannot launch in this session: both dotnet and apphost return exit 4 / Avalonia
render-timer error **−6661**. `system_profiler SPDisplaysDataType` reports the GPU
but no attached display. Headless Skia screenshots and resized layouts pass
under `artifacts/audio-controls.V5iI89/headless`; this is not a successful native
window launch. The pre-existing user app process was left running and must be
closed/relaunched to load the changes.

All radio/audio execution for this increment used fixtures, owned loopback
simulators and no-device output. No G2 connection, physical playback or hardware
TX was performed. The user's prior audible 828 receive confirmation is recorded
in [G2 results](G2_HARDWARE_RECEIVE.md#human-listening-and-desktop-audio-controls),
not a headphone-routing test. These are local working-tree results, not hosted
Windows/Linux validation or a new release/CI claim.

## G2 desktop/playback checkpoint — local working tree

The subsequent 2026-09-08 increment adds explicit G2 form validation/shared
preflight, bounded receive ownership through the desktop/audio controller,
native-deadline playback cleanup, hardware-labelled diagnostics, FT8 controls
and opt-in offline capture. Live FT8 testing exposed a hardware I/Q orientation
mismatch: Saturn samples now have Q conjugated once at the G2-only ingress,
before shared spectrum/DSP. Simulator codecs and all outbound packet/no-TX
controls remain unchanged. Native fixtures cover signed conversion and all
four USB/LSB × positive/negative RF-offset cases.

Local managed validation passes **223/223**, no skips: 129 Core/CLI, 63 Engine
(4 m 35 s, including the independent confined P1 reference), 31 Desktop (43 s).
The Release solution builds with zero warnings/errors. New cases cover capture
bounds/exclusive export, deadline/key/status-stop playback, hardware form rendering,
unconfirmed connection rejection, automation guards and diagnostic labelling.
No automated test contacts hardware or opens physical sound. Reports are under
ignored `artifacts/test-results/g2-desktop`.
After the final CLI sideband-selection change, all 129 Core/CLI cases pass again
under `artifacts/test-results/g2-desktop-cli-final`; it changes no native packets.

Separate real Ethernet/ANT1 runs exercised 14.074 MHz through the shared owner:
60 seconds with silent monitor/capture, then 30 seconds of freshly selected
MacBook speaker output at 48 kHz. Both ended at the native deadline, with zero
receive errors/output underruns and idle verified afterward. Initially there
were no FT8 decodes and the user heard no clear signal. The opposite-sideband
comparison decoded three FT8 messages, isolating the orientation mismatch.
**After the fix, correct USB decoded four FT8 messages** in a captured slot from
a clean 60-second speaker run. Explicit AF −20 / AGC max 80 brought peak output
to −39.2 dBFS, without system-volume or RF-routing changes. The radio returned
to idle. The offline decoder also passed a synthetic fixture/resampling check.
See [full measurements and remaining gates](G2_HARDWARE_RECEIVE.md).

These are local working-tree results, not hosted Windows/Linux qualification
or an updated CI/release claim. No hardware TX was performed.

After the native orientation fix, **15/15 native CTests pass** (100.40 s), and
the four targeted G2/packet/transport checks pass ASan/UBSan (31.65 s,
halt-on-error; not full-suite LeakSanitizer qualification). The production build
exports the G2 ABI but not its test opener/private packet helpers. The developer
publish under `artifacts/desktop` has been refreshed with the corrected production
native library. A local **native-window smoke run passes** in 2.333 s, displaying
and rendering 22 distinct frames, with no receive/output errors. That graphical
check uses only a muted simulator; it is not an actual GUI-to-hardware run.

Final full managed rerun against the corrected native library passes **223/223**
again, no skips: 129 Core, 31 Desktop (43 s), 63 Engine (4 m 32 s, independent
P1 reference included), under `artifacts/test-results/g2-usb-final`. The G2
sideband fixture was then made sample-warmup-based rather than dependent on
Sleep iteration counts, to tolerate Windows timer granularity; its final
Release/sanitizer reruns pass in 20.85/23.80 s. This test-only adjustment changes
no runtime library or RF behavior. `git diff --check` is clean. New Windows/Linux
hosted execution is still pending, not inferred from these local results.

## G2 Ethernet receive checkpoint — local, not a new CI result

The 2026-09-08 [hardware receive increment](G2_HARDWARE_RECEIVE.md) adds a
separate guarded G2 ABI and explicit Ethernet-only CLI. At that headless
checkpoint the desktop remained simulator-only; the later increment above adds
its separate opt-in hardware source. Three real ANT1 receive-only sessions pass on
local macOS arm64 (10/60/10 seconds, two 20m frequencies), with decoded audio,
spectrum, zero loss/error/key counters and idle confirmed after each stop.
No physical sound output or transmit test was performed. The radio was reached
at its Ethernet link-local address, not the previously used Wi-Fi subnet.

Current local Release build passes with zero warnings/errors, **128 Core/CLI
tests pass**, and the **full 15/15 native CTests pass** in 98.95 seconds,
including the new hardware-profile worker exercised only on loopback.
The four targeted G2 packet/worker and transport checks also pass under local
ASan/UBSan in 23.03 seconds, with halt-on-error enabled; this was not a full
sanitizer-suite or leak qualification for the new increment.
All 27 existing simulator desktop regressions pass locally (43 s), without a
physical window, audio device or radio connection.
The full native-backed managed engine suite passes **59/59** in 4 m 30 s,
including the confined independent P1 reference: **214/214 managed tests total**
(128 Core / 59 Engine / 27 Desktop), no skips. Test results are under ignored
`artifacts/test-results/g2-rx-{core,engine,desktop}`. Live hardware measurements
ran before the broad regression suites, without concurrent DSP test load.
This is working-tree validation, not a published release or a new Windows/Linux
CI claim. The earlier cross-platform results below belong to their stated
commits and must not be attributed to this hardware increment.

## Desktop diagnostics, endurance and preferences checkpoint

Implementation/benchmark source: `2c52092230e09df17a1cd848bc1d5ff7788eef57`, recorded
2026-09-08 Pacific. The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34200148029)
passes Windows/macOS/Linux; the optional legacy Windows reference was not
dispatched. The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34200148034)
passes all four jobs, including all three native-window endurance/failure-report
checks and Linux ASan/UBSan/LeakSanitizer (14/14, 303.10 s, no suppressions).
Full managed suites pass 208 cases / one reference skip per OS; the separate
POSIX reference gives 209 distinct passing cases on macOS/Linux. Native CTest
passes 13/13 on Windows (232.52 s), 14/14 on macOS (292.84 s) and 14/14 on Linux
(184.54 s). Existing lifecycle and Linux reconnect-memory gates pass unchanged.

Final runtime source `f90de9a9626eeb0238efcee2b9746aff72210fea` adds a temporary-file
ownership guard and one regression: a failed exclusive create must not delete a
pre-existing temporary name. Its [managed-only CI](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34202579079)
passes all three OSes; [final native CI](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34202579030)
also passes all four jobs. Managed-only runs have 157 passes / 53 explicit
native-dependent skips per OS; full native-backed suites have 209 passes / one
reference skip, with the separate POSIX reference giving 210 distinct passing
cases on macOS/Linux. Final native CTest passes 13/13 on Windows (295.39 s),
14/14 on macOS (286.54 s), and 14/14 on Linux (183.71 s); Linux sanitizer/leak
tests pass 14/14 in 360.07 s without suppressions. Native-window endurance and
failed-startup reports, signal/control/playback campaigns and all inherited
lifecycle/reconnect-memory gates pass again. Its native-window campaigns finish
in 65.301 / 65.833 / 64.702 s on Windows/macOS/Linux respectively, with all three
negative startup checks retaining failed reports and returning exit 4.
Locally the final source builds with zero warnings/errors and
passes **210/210** managed cases (124 core / 59 engine / 27 desktop), including
the independent P1 reference; engine 4 m 30 s and desktop 43 s. The published
developer app has been refreshed to this source. The long benchmark below remains
`2c520922`: its binaries were not rebuilt/replaced for this isolated file-I/O change.

Local macOS arm64 Release builds with zero warnings/errors and passes **209/209
managed cases**, no skips: 124 core, 59 engine (including the confined independent
P1 reference) and 26 desktop. Engine duration is 4 m 30 s; desktop duration is
43 s. Native algorithms, ABI 2, queue limits and existing memory guards are
unchanged by this increment. Published managed binaries match the built DLLs;
the refreshed developer artifact keeps the existing source-built native libraries.
No physical window/audio stream was opened by the local agent.

The short hosted desktop campaign uses an actual native window, P2 → P1 → P2,
20 connected seconds per phase, retune/mode/filter/AGC changes and resize every
five seconds. All phases remain muted on the sample-driven no-device monitor.
Both intermediate and final receive/output error counters are clean on all OSes;
all three sessions join successfully. Native-window measurements are:

| Target | Total wall time | P2 / P1 / P2 rendered frames | Approx. distinct-frame cadence, Hz | Sampled peak RSS, MiB | Process CPU, % of one core |
| --- | --- | --- | --- | --- | --- |
| Windows x64, build 26100 | 63.991 s | 354 / 218 / 357 | 17.70 / 10.89 / 17.84 | 1,342.52 | 48.23 |
| macOS 26.6.2 arm64 | 66.959 s | 183 / 172 / 175 | 9.08 / 8.59 / 8.65 | 1,357.19 | 23.88 |
| Ubuntu 24.04.4 x64, X11/Xvfb | 64.722 s | 365 / 219 / 362 | 18.19 / 10.92 / 18.06 | 1,406.54 | 44.20 |

Each cadence divides distinct rendered frames by the corresponding connected
observation time; it includes retunes and resizing. The 30 Hz M5 target is **not
met by these hosted campaigns**. CPU divides first-to-last sampled process CPU
time by the same samples' monotonic wall interval; it includes reconnect work,
the simulator/runtime and rendering, not only WDSP. RSS is a sampled process
working set, not private/native allocation or a leak result. In particular,
Windows releases much of its working set after cleanup; comparing that endpoint
to an active-session start is not a steady-state memory-growth measurement.

P2 source-clock loss remains visible even with clean playback counters. Windows
records two rebases / 9.394 ms lost source time in its first phase, zero in its
last. Hosted macOS records 119 / 865.893 ms and 74 / 602.515 ms in its two P2
phases. Linux records zero in both. These are source scheduling deficits, not
wire sequence gaps, and the sample-driven monitor is not a physical-clock test.
No source-clock allowance was changed; these are measured limitations, not a
claim that real-device endurance or real-time host scheduling is solved.

All three deliberately missing-native launches retain a schema-1 **failed**
report and return exit 4, with zero successful sessions. The original
`DllNotFoundException` is retained in the connection event; the campaign's
bounded spectrum wait subsequently times out. Reports are available as
`desktop-diagnostics-<runner>` artifacts on the native run for 14 days.

New regression coverage includes safe restored controls on both protocols,
disconnected/muted/no-device startup, orderly-close persistence, invalid window
dimensions, corrupted/null/oversized/future/unknown-field files, external edits,
serialized concurrent saves, unchanged legacy data, and injected pre-rename
failure/cancellation. Diagnostic tests cover bounded sample/event/session
eviction, unit calculations, immutable export during receive and after stop,
and retention of a latched output fault after automatic cleanup. All automated
settings files use owned temporary directories, not the user's real settings.

The **30-connected-minute headless Skia** campaign passes locally at `2c520922`:
1,801.936 total wall seconds, with three ten-minute P2/P1/P2 phases. It draws
11,059 / 6,538 / 11,074 distinct frames (18.43 / 10.90 / 18.46 Hz). Every normal
and terminal receive/output error gate is clean: zero packet gaps/order/malformed/
foreign packets, native socket/DSP errors, CM overruns/audio drops, output
rejections, underruns/driver underruns, nonfinite samples or clipping. All three
sessions end without a fault and remain muted/nonphysical. The first P2 phase
has one source-clock rebase / 23.240 ms lost time; the final P2 phase has zero.
Priming/flush silence is recorded separately, not reclassified as an underrun.

The sampled peak working set is 1,354.72 MiB. First-to-last sampled RSS increases
49.48 MiB across warm-up/reconnects; the final ten-minute phase changes by
−0.016 MiB. Average process CPU is 32.42% of one core, sampled peak 61.74%, with
a maximum sample gap of 1.00346 s. These observations are not a general memory/
CPU qualification or leak proof. No other native campaign ran concurrently; an
early non-native, no-build publish attempt stalled in the sandbox and was stopped
and retried with build-server reuse disabled. It did not rebuild the running DLLs.

The report retains 1,810 samples and three session summaries. Its event history
caps at 256 and explicitly reports 468 evicted events; no sample/session history
is evicted. This exercises real bounded retention beyond the deterministic unit
tests. The report is saved locally as
`artifacts/desktop-endurance-30m-2c520922.json`, with its passing TRX in the matching
`artifacts/test-results/desktop-endurance-30m-2c520922` directory.

This is separate from the hosted native-window measurements above, and is not
a physical-audio listening run. See [the reliability contract](DESKTOP_RELIABILITY.md)
for commands, fixed safety/progress gates, retention limits and remaining M4/M5
hardware/audio/performance qualification.

## Audio clock recovery and output-loss checkpoint

Runtime source: `9311607cd9ae4c0f24c746954bafa9384d2078f0`, recorded
2026-09-07 Pacific. All four jobs pass in the
[native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34194859815),
including Linux ASan/UBSan/LeakSanitizer: 14/14 tests in 358.83 s, leak detection
enabled and no suppressions. Existing lifecycle and Linux memory-growth gates
also pass unchanged.
The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34194859829)
passes all three platforms; the optional Windows-reference job was not dispatched.

| Target | Native CTest | Full managed suite | P1/P2 playback campaign | Published native window |
| --- | --- | --- | --- | --- |
| Windows x64 | 13/13, 268.91 s | 188 pass / 1 reference skip | 15.166 s, pass | 20 displayed/rendered frames, 2.672 s, exit 0 |
| macOS arm64 | 14/14, 284.46 s | 188 pass / 1 reference skip | 14.428 s, pass | 20 displayed/rendered frames, 4.300 s, exit 0 |
| Linux x64 | 14/14, 184.60 s | 188 pass / 1 reference skip | 15.014 s, pass | 20 displayed/rendered frames, 3.039 s, exit 0 under Xvfb/X11 |

The full suite contains 124 core, 59 engine and six desktop cases. The independent
P1 reference also passes separately on macOS/Linux, giving 189 distinct passing
cases there; Windows has 188 without that comparison. Thirteen focused playback
and six desktop cases repeat early in CI, not additional distinct cases. The
deliberately missing-native desktop launch returns exit 4 on every OS.

All three platforms pass the virtual-hour PCM test with the same printed
queue/correction/signal results listed below, and the controlled-clock 60-second
wall-time pipeline test. These are no-device tests, not physical listening.
The separate sample-driven playback campaign recovers 1,000 Hz on both protocols:
P1 RMS is 0.01767763735 on all platforms; P2 is 0.01767758865 on Windows,
0.01767784074 on macOS and 0.01767758876 on Linux. Required receive/output error
counters are clean.

Local macOS arm64 validation passes:

- Zero-warning managed build and framework-dependent desktop publish, with
  production playback ABI 2 staged beside the published app.
- 189 managed cases: 124 core, 59 engine (including the confined independent P1
  reference) and six headless desktop/Skia cases; no skips or failures.
- 14/14 Release native tests (79.68 s) and 14/14 ASan/UBSan tests (114.55 s).
  Local macOS leak detection remains disabled; no sanitizer suppressions added.
- A real stereo PCM **virtual-hour** run at 48 kHz/+1,000 ppm: steady queue
  2,053–4,007 frames, average correction 999.871 ppm, RMS 0.35355123,
  1001.0002 Hz and zero underruns/rejected frames. This is accelerated sample
  time, not a wall-clock hour or a physical listening session.
- Six four-minute PCM cases cover both ±1,000 ppm at 44.1/48/96 kHz, with
  variable independent packet/callback sizes, stereo ratio/amplitude/frequency
  checks and zero underruns/rejections. Both fixed-rate negative controls exhaust
  their queues. Eight virtual-hour controller cases cover 0, ±100/500/1,000 ppm,
  offset reversal, scheduling disturbance, slew bounds and anti-windup.
- One **60-second wall-clock** P2/CM/WDSP session uses controlled virtual source
  and +1,000 ppm output clock domains, mute/flush/retune and output loss. Host
  delays pause the test timeline, not the relative clock skew. It advanced
  37.605 virtual seconds in the full local suite: 1,806,845 output frames,
  queue 2,546, correction −726.282 ppm, zero I/Q clock rebases/lost time,
  two re-primes, zero input/output overruns, drops, underruns, nonfinite/clipped
  samples or transport/DSP errors. Correction was still settling after flush;
  this is resilience coverage, not the steady-state estimator measurement.
- The fake PortAudio backend drives production native lifecycle code through
  100 loss/reopen cycles, stopped/error/finished/stalled callbacks, stale device
  selection, startup rollback and failed-close storage retention. It is not
  linked to any physical audio backend. Engine/UI tests verify automatic cleanup,
  cleared UI device selection, and explicit conservative muted reconnect.
- Production (`BUILD_TESTING=OFF`) P1/P2 playback campaign: 11.708 s, 1,000 Hz,
  RMS 0.01767763735 / 0.01767758876, clean receive/output counters.

The new controller-loss regression exposed an intermediate state that published
disconnected before native RX close completed. Publication now occurs only after
all owned cleanup; both protocols pass repeated failure/reconnect tests.

Initial hosted failures were not treated as passing results. `dc22c8fa` needed
an explicit `<initializer_list>` include for GCC's fake-driver test build;
`e51fd7c8` fixes that. The first unconstrained wall-clock integration test also
failed on hosted Mac: at 192 kHz I/Q, 21 simulator scheduler rebases by 3.2 s
lost much more source time than the injected ppm offset. A 48 kHz attempt at
`de45a1ec` also lost 7.659 ms of I/Q clock time and underrun at 17.5 s. The
simulator intentionally bounds replay to eight packets and is not a hardware
clock. Those are genuine host-paced simulator limitations, not evidence that
physical playback endurance is solved. `9311607c` drives the complete native
receive path on a controlled timeline with independently derived clock-domain
frame counts and strict zero-underrun/rebase checks. Queue size, correction
limits, public simulator replay policy and higher-rate transport gates were
not relaxed. See [the test boundary and diagnostics](AUDIO_PLAYBACK.md).

The user reported a working Mac window and audible simulated tone on the prior
preview. No physical audio stream, microphone, LAN radio or hardware TX was used
by this increment's automated validation. Real driver unplug/sleep/wake, hanging
driver calls, physical-clock endurance/latency, wideband/speech quality, hardware
G2 receive and the M4/M5 performance gates remain unqualified. See the
[audio contract](AUDIO_PLAYBACK.md) and [desktop preview](DESKTOP_PREVIEW.md).

## Simulator receiver desktop and playback checkpoint

Validated runtime source: `e9c352ce2f90a686460a3aeb590915f068b70616`, recorded
2026-09-07 Pacific. The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34184773806)
passes all four jobs, including Linux ASan/UBSan/LeakSanitizer with all twelve
native tests, leak detection enabled and no suppressions. The
[managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34184773795)
passes on all three OSes with 138 passes / 44 explicit native-dependent skips;
the legacy Windows-reference job was not dispatched.

| Target | Native CTest | Full managed suite | Playback campaign | Published native window |
| --- | --- | --- | --- | --- |
| Windows x64, Windows 2025 / VS 2026 image | 11/11 | 181 pass / 1 reference skip | 15.091 s, pass | 20 rendered frames, 2.729 s, exit 0 |
| macOS 26 arm64 | 12/12 | 181 pass / 1 reference skip | 16.763 s, pass | 20 rendered frames, 4.989 s, exit 0 |
| Ubuntu 24.04 x64 | 12/12 | 181 pass / 1 reference skip | 15.039 s, pass | 20 rendered frames, 3.021 s, exit 0 under Xvfb/X11 |

The full suite contains 124 core, 53 engine and 5 desktop cases. The independent
pinned P1 reference also passes in a separate POSIX step, giving 182 distinct
passing cases on macOS/Linux. Windows still has 181 without that independent
comparison. Seven focused playback and five desktop cases run early and again
in the full suite; these are repeat executions, not additional distinct tests.

Both-protocol playback recovers a 1000 Hz tone at AF −20 dB with AGC off. P1 RMS
is 0.01767763735 on all three platforms; P2 RMS is 0.01767783924 on Windows,
0.01767784079 on macOS and 0.01767758876 on Linux. Campaign counters show zero
receive gaps, socket/DSP errors, CM overruns/audio drops, playback rejections,
underruns, nonfinite input and output clipping. Priming/flush silence is tracked
separately. Startup, mute and conservative muted reconnect pass.

Published framework-dependent desktops load their copied, source-built native
libraries, open an actual window, receive from an owned P2 simulator, retune and
draw advancing frames before shutdown. All use muted no-device output. A second
launch with a deliberately missing native directory must return exit 4; that
negative check passes on every OS. These short launch measurements are not a
steady-state rendering, GPU, latency or performance qualification.

Native playback coverage includes three-rate signal checks, bounded concurrent
SPSC operation, explicit starvation/re-prime, flush, finite/clipped samples,
simulated output loss and 100 no-device output lifecycles. A managed regression
feeds 250 varying-size batches with flushes at each output rate, preserving a
1,024-frame reserve. Removing the reserve fails all three cases; restoring it
passes. The correction prevents a silent monitor from draining FIR look-ahead
or rendering wall-clock catch-up silence while PCM awaits draining; it does not
relax underrun counters or qualify physical hardware clocks.

The first Windows build exposed an internal `terminate()` helper name collision
with MSVC's runtime; explicit audio lifecycle names fix it. Hosted testing also
exposed the silent-monitor scheduling/reserve bugs and a race in Avalonia's
manual headless-session disposal. Tests now own a single explicitly joined UI
thread, with per-window/controller cleanup; the pinned framework is not patched.
The [desktop](DESKTOP_PREVIEW.md) and [audio](AUDIO_PLAYBACK.md) documents describe
the contracts, negative controls and local measurements.

Existing native DSP, independent P1 reference, P1/P2 receive/controls/gain,
short endurance/fault campaigns, 100-cycle CM/transport CLIs and Linux reconnect
memory guards all pass with their previous thresholds unchanged. Local macOS
passes 182 managed cases and twelve normal/instrumented native tests. Production
playback (`BUILD_TESTING=OFF`), device enumeration without opening a stream,
missing-library exit 3 and real Ctrl-C exit 130 also pass locally.

No physical audio stream, microphone, G2/LAN radio or hardware TX was used.
The local agent session reported no attached display and could not start the
native macOS render timer; headless drawing passed locally, while the actual
macOS window passed on the hosted graphical runner. Real-device listening,
clock drift, device unplug, sleep/wake, G2 RX, full M5 endurance/performance and
packaged release qualification remain open. The G2's RX-only ANT1 constraint is
unchanged.

## Shared receive gain, mute and AGC checkpoint

Validated runtime source: `e49104decdff79a4773a0818cf15165919279add`, recorded
2026-09-07 Pacific. The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34178295913)
passes on Windows x64, macOS arm64 and Linux x64. Linux ASan/UBSan/LeakSanitizer
passes all eleven native tests, including the new gain/history/rollback test,
with leak detection enabled and no suppressions.

| Target | Native CTest | Full managed suite | Separate pinned P1 reference | Gain/AGC CLI |
| --- | --- | --- | --- | --- |
| Windows x64 | 10/10 | 168 pass / 1 reference skip | POSIX tool not run | 98.754 s, pass |
| macOS arm64 | 11/11 | 168 pass / 1 reference skip | 1/1 pass | 104.757 s, pass |
| Linux x64 | 11/11 | 168 pass / 1 reference skip | 1/1 pass | 99.409 s, pass |

The full suite contains 123 core and 46 engine cases. Its reference test is
enabled only in the separate POSIX step, giving 169 distinct passing cases on
macOS/Linux. Windows has 168 and still lacks independent POSIX-reference or
legacy-Windows-application comparison. The
[managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34178295867)
passes all three OSes: 134 pass / 35 native-dependent skips, with the optional
legacy Windows-reference job not dispatched.

Each gain CLI runs ten gain/mute/ceiling checks and three AGC amplitude-step
traces on each protocol (P1 at 48 kHz, P2 at 192 kHz). All three OSes observe the
strong-to-weak drop in the 3.2 s audio window, with 300 ms fast recovery and
1600 ms medium/slow recovery. The early responses differ: approximately
0.319/0.0275/0.0131 RMS for P1 fast/medium/slow and
0.314/0.0273/0.0131 for P2. Maximum step peaks are below 0.99 and settled silence
is exactly zero. These are 100 ms-window observations of this fixture, not
hardware latency or a promise that slow/medium always finish recovery together.
The full traces are in the job logs; see the [gain contract](RECEIVE_GAIN.md)
for analysis boundaries and expected levels.

All gain campaigns enforce zero missing packets, socket/DSP errors, CM input
overruns and audio drops, and verify safe STOP/rebind. Startup mute, unmute,
idempotent updates, native settings, maximum gain, default reconnect and
unmodified RF-spectrum level checks pass. The native test also verifies actual
AGC history is unchanged by AF/mute-only updates, restores slow hang settings
after other presets, and exercises all five configured-startup rollback stages.

Existing P1/P2 audio/spectrum/controls, short soak, 100-cycle CM/transport CLIs,
and Linux six-cycle same-caller / twenty-cycle async / twenty-cycle rotating-
caller memory regressions pass with unchanged guards. No WDSP algorithm,
production allocator setting or RF/TX control was changed.

Local macOS arm64 passes all 169 managed tests including the confined reference,
eleven Release native CTests, and eleven ASan/UBSan CTests (local leak detection
disabled). A separate `BUILD_TESTING=OFF` native gain campaign passes in
89.068 s; real Ctrl-C exits 130 after disposing owners. All radio traffic is
owned IPv4 loopback. No G2/LAN radio, audio device or hardware TX was exercised.
Hardware RF gain, calibration, speech/noise AGC behavior, click-free playback
and the broader M4 hardware gate remain unqualified.

## P1 simulated receive checkpoint

Validated runtime source: `986467f5d29e3261c53a78aefcbca49b13ea1db1`, recorded
2026-09-07. The [native workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34175140277)
passes on Windows x64, macOS arm64 and Linux x64, including the Linux
ASan/UBSan/LeakSanitizer job (ten tests, no suppressions).

| Target | Native CTest | Full managed suite | Separate pinned reference | P1 14-check CLI |
| --- | --- | --- | --- | --- |
| Windows x64 | 9/9 | 155 pass / 1 reference skip | POSIX tool not run | 19.574 s, pass |
| macOS arm64 | 10/10 | 155 pass / 1 reference skip | 1/1 pass | 19.139 s, pass |
| Linux x64 | 10/10 | 155 pass / 1 reference skip | 1/1 pass | 19.231 s, pass |

The reference environment is enabled only in its dedicated macOS/Linux step;
the full suite explicitly skips that test without it. This gives 156 distinct
passing managed tests on those two OSes, not 156 passes in the full-suite step.
Windows still lacks independent POSIX-reference/legacy-Windows-application comparison.
Wanted RMS ranges are 0.176734–0.176777 (Windows), 0.176708–0.176808 (macOS), and
0.176734–0.176847 (Linux); maximum rejected RMS is below 1.135e-8 on each.
The P1 signal campaigns have zero missing/late/malformed/foreign packets, socket
or DSP errors, CM overruns, audio drops, unsafe requests and watchdog stops.
Both STOPs and reconnects pass. Injected-fault regressions are separate tests.

Existing P2 audio/spectrum/controls, short soak, 100-cycle CM/transport CLIs and
Linux six-cycle same-caller / twenty-cycle async / twenty-cycle rotating-caller
memory regressions also pass with unchanged budgets. The
[managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34175140270)
passes all three OSes with 125 passes / 31 native-dependent skips; the opt-in
legacy Windows-reference build remains skipped.

The [P1 receive path](P1_RECEIVE_INTEGRATION.md) shares the P2 native/managed
owner. Local macOS arm64 validation on 2026-09-07 passes all 156 managed tests
(115 core / 41 engine, including independent pinned `hpsdrsim` interoperability)
and ten native CTests, both normally and with ASan/UBSan. Local sanitizer leak
detection is disabled on macOS; Linux CI retains its existing leak checks.
Fourteen P1 USB/LSB/filter/retuning audio checks and four signed RF spectrum
checks pass. The independent reference yields 796.972 Hz LSB audio at
0.000177726874 RMS, both expected spectral lines, RX tune receipt, STOP and
native-port rebind. Hardware, higher-rate/multi-RX P1 and TX remain unqualified.
The local production build (`BUILD_TESTING=OFF`) passes the P1 campaign in
17.550 seconds, wanted RMS 0.176734–0.176812 and rejected RMS below 1.135e-8.
A separate real Ctrl-C check exits 130 after disposing owners. All traffic in
this checkpoint is owned IPv4 loopback; no physical G2 or hardware TX was used.

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
