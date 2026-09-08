# Receive playback preview

The output path is implemented separately from WDSP: native stereo 48 kHz PCM
→ managed background pump → bounded native queue/resampler → PortAudio callback.
`Thetis.Audio` owns an explicit native library and one output; `ReceivePlayback`
owns its pump/output, and `PreviewController` owns the complete simulator session.
There are no .NET, DSP, UI, logging, allocation or mutex calls in our device
callback. PortAudio initialization, device enumeration and stream open/close
run on one stable control thread (including WASAPI COM ownership).

## Backends and buffering

| Platform | Physical output built | Automated execution |
| --- | --- | --- |
| macOS | CoreAudio | No-device renderer; no speakers opened |
| Windows | Shared WASAPI | No-device renderer; no speakers opened |
| Linux | ALSA | No-device renderer; no sound card required |

Only explicitly selected stereo float outputs at 44.1, 48 or 96 kHz are accepted.
Selection prefers 48 kHz, then 44.1, then 96. A refreshed device index must still
match its UTF-8 name and host API at open; a stale/mismatched selection fails.
There is no automatic default-device fallback. Names are not persistent unique
hardware IDs; refresh and reselect after device changes.

The callback queue has 8,192 stereo source frames (about 171 ms maximum at
48 kHz), with a 4,096-frame prefill for independently clocked output (about
85 ms source buffering, plus driver latency). The sample-driven silent monitor
retains its 1,024-frame prefill. A 32-tap/1,024-phase windowed-sinc resampler
converts to the selected output rate. Callback buffers are supplied by PortAudio;
coefficients/storage are allocated before starting the stream. Atomic counters
expose queue depth, submitted/rejected/rendered frames, priming/starvation,
queue/driver underruns, nonfinite input, clipping and pre-mute peak. Playback
**ABI 2** adds clock tracking, correction in parts per billion, target queue
depth, re-primes, latched failure reason and last driver status to the 22-value
state record. Rebuild/stage the native audio library and managed app together;
the previous ABI is rejected before opening any stream.

Overflow rejects new frames; starvation emits silence and re-primes rather than
repeating stale samples. Nonfinite input becomes zero; input is bounded and
output clamps to ±1. Flush discards at the next consumer boundary. Mute cannot
recall audio already buffered by the driver. Native output always opens muted;
the application reconnects muted with AF gain no higher than −40 dB.

The no-device backend runs the same queue and resampler but **never initializes
PortAudio**. It advances in 10 ms sample blocks as PCM is recovered (at most five
blocks per pump iteration), retaining 1,024 source frames for prefill/FIR
look-ahead and pacing from actual queue occupancy, including after flush.
It measures tone frequency/RMS and does not advance a fake
wall clock while PCM awaits draining: host scheduling delays must not synthesize
test-only starvation. Native tests still explicitly exercise actual queue
starvation/overflow behavior. This sample-driven monitor deliberately disables
clock recovery: it has no independent clock to follow.

## Independent clocks and output loss

Physical output now adjusts the existing resampler's fractional step using a
consumer-owned queue-depth PI controller. It averages occupancy over 10 ms of
output samples, applies a one-second low-pass filter, and aims for 3,072 source
frames (64 ms). Proportional/integral gains are 2 ppm/frame and
0.05 ppm/(frame·second). Correction is limited to ±2,000 ppm and slewed at no
more than 200 ppm/second, with conditional integration to prevent wind-up.
Positive correction consumes source PCM faster using fractional interpolation,
not ad-hoc whole-sample drops/repeats. No wall-clock queries, allocation or locking
are added to the callback.

Initial prefill, starvation and flush do not train the estimator. Flush and
starvation clear its history/correction and require fresh prefill. Muting alone
continues consumption and clock tracking. These bounds are not a promise to
absorb arbitrary scheduling stalls, packet loss or offsets beyond ±2,000 ppm.
The desktop displays current correction alongside queue/underrun counters.

The native owner latches failure on a stream-finished notification, inactive or
failed driver status, or one second without callback progress. The watchdog also
stops an independently clocked owner after a one-second **polling** gap, even if
callbacks advanced (conservative recovery from a suspended control pump). It is
polled by the background receive pump, not the UI. A failed owner emits silence,
rejects further writes/unmute, and cannot revive when callbacks resume.
The controller observes pump completion and joins output, receiver and simulator
without requiring a UI poll; it publishes disconnected only after cleanup.

The UI shows the fault, discards the old output selection and requires explicit
device refresh/reselection/reconnect. There is no auto-switch to a default or
similarly named output, no automatic reconnect, and no restored unmute/high gain.
A driver close failure retains callback-owned memory and prevents reopening in
the managed process; restart is required. Driver calls themselves are not given
an unsafe forced timeout: an indefinitely hung driver can still block cleanup.
OS-level rerouting inside an otherwise-active logical device is not detected by
this watchdog; actual device/default-route changes still need platform testing.

Tests can explicitly open a **clocked no-device fixture** with the same adaptive
renderer/watchdog and an independently paced consumer. It never initializes
PortAudio and is not an app/CLI output-selection option. A separate native test
links production playback code to a fake PortAudio implementation to exercise
the physical-driver lifecycle without loading any physical backend.

## Commands

After [building native code](NATIVE_DSP.md) and the managed solution:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build --no-restore -- \
  playback-selftest --native-dir /absolute/native/stage/Release
dotnet run --project src/Thetis.Headless -c Release --no-build --no-restore -- \
  audio-devices --native-dir /absolute/native/stage/Release
```

`playback-selftest` owns P1/P2 loopback peers, verifies startup silence, the
1 kHz tone at AF −20 dB with AGC off, mute, clean counters and muted reconnect.
It never enumerates or opens hardware audio. `audio-devices` initializes and
enumerates outputs but does not open a stream or microphone.

Optional, deliberate listening to a **simulated tone**, not a radio:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build --no-restore -- \
  playback-listen --native-dir /absolute/native/stage/Release \
  --device 0 --seconds 10 --unmute
```

Replace `0` with an explicitly enumerated device index. Lower system/speaker
volume first. Without `--unmute` it stays silent. Duration is 1–30 seconds;
startup remains muted for one second before optional unmute at −40 dB.
Ctrl-C cancels/joins the owned session. These commands reject hardware addresses
and TX options. The [desktop](DESKTOP_PREVIEW.md) uses the same owner.

## Coverage and remaining checks

The user confirmed hearing the simulator through the Mac desktop. This is an
initial manual listening check; device identity/rate, latency, unplug behavior
and duration were not recorded. It is not a full physical-device qualification.

Clock-recovery tests exercise eight virtual-hour controller scenarios (0,
±100/500/1,000 ppm and a direction reversal), bounded slew/anti-windup, and
four minutes of actual stereo PCM at each of three rates and both ±1,000 ppm
offsets. The fixed-rate negative controls exhaust their queues. A separate full
PCM virtual-hour run at 48 kHz/+1,000 ppm passed locally with queue depth
2,053–4,007 frames after settling, mean correction 999.871 ppm, 0.35355123 RMS
and 1001.0002 Hz, with zero underruns/rejections. This is 60 minutes of sample
time, **not 60 minutes of wall-clock listening**.

The fake-driver tests cover 100 loss/reopen cycles, finished callbacks, inactive
and error statuses, stalled callbacks, stale device indices, open/register/start
rollback, failed-close memory retention, and muted clean reopen. These new tests
are included in three-OS native CI; actual hosted results are recorded separately
in [NATIVE_CI_RESULTS.md](NATIVE_CI_RESULTS.md).

```sh
ctest --test-dir artifacts/native -C Release --verbose -R receive_playback
artifacts/native/stage/Release/playback_clock_tests --soak
```

The second command uses `.exe` on Windows. It renders a virtual hour without a
device. Managed playback regressions also run the real P2/CM/WDSP/pump pipeline
for 60 seconds of **wall time**, using two controlled virtual clock domains:
48 kHz I/Q and +1,000 ppm output. Each 5 ms source event is processed through
native DSP and the pump before advancing the test timeline. Readiness uses
cumulative expected PCM counts, not output queue occupancy; output frame counts
are derived independently from elapsed virtual time and the fixed skew. Host
scheduling delays pause the shared timeline, not either domain's relative rate.
The run covers mute/flush, retune and output loss and requires zero underruns
**and zero I/Q source-clock rebases**. It reports both wall and virtual duration
(37.600 virtual seconds in the initial local 60-second run). Automatic cleanup
and conservative reconnect are also exercised for both protocols.

The initial 192 kHz version of that wall-clock test failed on hosted macOS: its
source had 21 scheduler rebases by 3.2 seconds, losing much more time than the
injected 1,000 ppm offset. The simulator deliberately drops overdue clock time
beyond eight replay packets, which at 192 kHz covers only 9.9 ms. A 48 kHz
attempt (39.7 ms budget) also encountered a host stall, lost 7.659 ms of I/Q
clock time and underrun at 17.5 seconds. That disproved the assumption that a
general-purpose hosted runner could supply a reliable independent real-time
source for this test. The controlled-timeline fixture retains strict sample/
counter assertions without widening the actuator or enlarging queues. It proves
clock/sample/lifecycle behavior, **not host real-time scheduling or physical
playback endurance**. Explicit `iqPacingResyncs`/`iqPacingLostNanoseconds`
diagnostics quantify the limitation in the normal simulator. The preview's
default 192 kHz profile and all existing higher-rate transport tests are
unchanged; host scheduling starvation remains a real limitation, not a claim
that the radio's hardware clock stops under load.

### Previous simulator preview checkpoint

Local macOS arm64 source `e9c352ce2f90a686460a3aeb590915f068b70616`
(2026-09-07 Pacific) passes locked restore, a zero-warning managed build,
182 managed tests (124 core / 53 engine including the confined independent P1
reference / 5 desktop), 12 Release native tests and 12 ASan/UBSan native tests.
The production-library (`BUILD_TESTING=OFF`) playback campaign passes in 11.425 s:
P1 RMS 0.01767763735 and P2 RMS 0.01767783921, both 1000 Hz, with zero input
overruns, audio drops, output rejections, underruns, nonfinite or clipped samples.
Priming/flush silence is counted separately from underrun events. Actual Ctrl-C
exits 130 after owner cleanup; missing native libraries return 3. CoreAudio
enumeration succeeds without opening a stream. The vendored PortAudio build has
macOS deprecation warnings; the zero-warning claim applies only to managed code.

The old silent monitor could render up to 3,840 catch-up frames after draining
only 2,048 input frames following a host scheduling delay. Hosted macOS exposed
an output underrun despite zero CM overruns/drops, and a separate 790 Hz/low-RMS
window containing silence. Input-credit pacing alone also allowed a large first
batch to drain the FIR look-ahead, which hosted macOS exposed. Queue-based pacing
with a sample reserve addresses both paths without relaxing signal tolerances or
reclassifying real queue underruns. A 250-batch/flush regression covers each output
rate; removing the reserve makes all three cases fail, restoring it makes them
pass. Physical callback timing remains a distinct, unqualified gate.

See [the CI record](NATIVE_CI_RESULTS.md) for hosted source/results; do not infer
physical-device playback from a no-device or native-window test. The counts in
this historical subsection refer to that earlier source, not the new increment.

Native tests cover ABI/capacity canaries, three-rate 1 kHz amplitude/frequency,
mute, queue overflow, explicit starvation/re-prime, nonfinite/clipping, flush,
simulated output loss, 100 no-device output lifecycles and concurrent
producer/consumer wrap. Managed tests cover P1/P2 tone recovery,
rollback, invalid controls, cancelled startup, interrupted output, concurrent
dispose and reopening. Linux CI includes ASan/UBSan with leak detection; local
macOS sanitizers do not enable leak detection.

No physical playback or RF transmission was used for this implementation's
automated validation. Actual sound quality, stereo routing, device-specific
latency/rates/loss, resampler wideband response, sustained physical-clock drift, speech,
noise and calibrated RF levels remain unqualified. Do not connect this preview
to a G2: hardware streaming is deliberately absent. The existing G2 receive-only
ANT1 constraint is unchanged.

The callback design follows [PortAudio's callback restrictions](https://files.portaudio.com/docs/v19-doxydocs/writing_a_callback.html)
and [stream lifecycle API](https://files.portaudio.com/docs/v19-doxydocs/portaudio_8h.html).
The vendored source identity and licenses are recorded in [DEPENDENCIES.md](DEPENDENCIES.md).
