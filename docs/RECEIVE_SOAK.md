# Simulator-backed receive endurance and fault campaign

`receive-soak` exercises the existing native P2 → ChannelMaster → WDSP audio and
spectrum session for longer than the signal smoke test. **Every endpoint is
owned IPv4 loopback. No physical radio, TX path, audio device or UI is used.**
The G2's receive-only ANT1 restriction is unchanged.

## Run

Build the [managed harness](GETTING_STARTED.md) and [native module](NATIVE_DSP.md).
Use an absolute native staging directory as in [receive-selftest](P2_RECEIVE_INTEGRATION.md).

```sh
# Short CI-sized campaign: 10 seconds steady RX plus fault/reconnect phases.
dotnet run --project src/Thetis.Headless -c Release --no-build -- receive-soak --native-dir ABSOLUTE_NATIVE_STAGE_DIRECTORY --duration-seconds 10 --reconnects 2

# Sustained 30-minute measurement plus fault phases and ten reconnects.
dotnet run --project src/Thetis.Headless -c Release --no-build -- receive-soak --native-dir ABSOLUTE_NATIVE_STAGE_DIRECTORY --duration-seconds 1800 --reconnects 10
```

Defaults are 60 steady seconds and two reconnects. Duration accepts integer
10–7200 seconds; reconnects accepts 1–20. Unknown, duplicate, incomplete and
out-of-range options fail before any simulator/native startup. There are no
radio-address, NIC, arbitrary-packet or TX switches.

Duration measures the **sustained observation window**, not the whole command.
Startup, initial signal warm-up, fault scenarios, reconnects and teardown add
time. Do not suspend the host during a qualification run. The simulator and
engine share this process; avoid unrelated local test loads when interpreting
resource/performance results. Keep the build/native library unchanged while
running, or publish into a separate artifact directory first.

The final schema-1 JSON goes to stdout. Progress goes to stderr at phase start,
every approximately 30 seconds and phase end. Redirect stdout to retain the
report. Expected measurement failures and active cancellation retain completed
and partial phase results, including counters and cleanup flags. Native load
failures and invalid arguments are reported on stderr. Exit codes: 0 pass/help,
2 syntax, 3 missing/incompatible native library, 4 failed checks, 130 cancellation.
Ctrl-C requests disposal; cleanup still gets a bounded wait for the peer to stop.

The Native DSP workflow runs the short campaign on every relevant push/PR.
Manual **Run workflow → receive_soak → 30-minutes** opts into a long campaign
on all three OSes; it is not the default. Native jobs allow 60 minutes to cover
builds plus the optional long run. The existing sanitizer job is unchanged.

## Campaign and pass conditions

All phases use DDC2, 192 kHz complex input, 48 kHz stereo USB output and the
simulator's 14.200 MHz / 0.25-amplitude tone. A separate simulator/session pair
per scenario keeps injected faults out of healthy-session counters.

| Phase | Exercise | Required observations |
| --- | --- | --- |
| Steady | Warm both signal paths, then stream for the requested duration. Retune between 14.199 and 14.1985 MHz every 15 seconds (every five seconds in the 10-second mode). | Finite advancing I/Q/audio/spectrum, settled audio RMS/tone and spectrum RF position/level; no unplanned loss, audio drops, CM input overruns or native errors. |
| Packet loss | Three seconds with every seventh generated RX packet dropped. | Simulator injection and native missing-packet counters increase; finite audio/spectrum continue. Exact tone/RMS checks are disabled across intentional discontinuities. |
| Slow reader | Four seconds, withholding both pulls between seconds one and two. | Audio stays bounded at 16384 queued frames; intentional audio drops and spectrum coalescing increase; reads resume. |
| Peer disappearance | Verify two seconds of receive, then dispose the peer while native RX is active. | Native reports failure within six seconds; heartbeats cease and Tune is rejected. This phase's socket failure is expected, not hidden in an aggregate error total. |
| Reconnects | Recreate the simulator on the disappeared peer's **same port layout**; open, verify two seconds of audio/spectrum, stop and repeat. | New sessions have clean counters and valid signal output; native socket can be rebound after each close. |

All normal phases require the peer to stop within two seconds of native
disposal, without a watchdog stop; a watchdog expiry must not masquerade as
receipt of native STOP. The disappeared peer cannot acknowledge STOP, so that
phase records `expectedPeerFailureObserved` instead. Native sockets are rebound
after disposal to check release. Existing native lifecycle tests separately
check joined workers; process thread counts here are diagnostic, not a .NET
thread-pool leak assertion.

The monitoring loop requests a five-millisecond delay between pulls, but OS
scheduling determines actual cadence. An unexpected five-second lapse in I/Q,
audio production or spectrum observations fails the campaign. Phase-end checks
also require actual audio and multiple spectrum frames, so short phases cannot
pass simply by reaching a timer. The one-second intentional reader pause and
native retune settling interval are included in observed spectrum gaps.

Healthy signal checks wait 1.2 seconds after phase start or retuning. Audio is
measured in 8192-frame windows (RMS within 0.01 of 0.25/√2; tone within 20 Hz).
Spectrum requires the expected generation/configuration, strictly increasing
sequence, nondecreasing host publication time, a peak within one FFT bin of
14.200 MHz and level within 1.6 dB of 20 log10(0.25). Spectrum units remain
uncalibrated; see the [frame contract](P2_RECEIVE_INTEGRATION.md#spectrum-frame-contract).

## Reading the report

Each phase records pass/failure, actual observation duration, retunes, consumed
audio/spectrum, inferred produced spectrum count, observed cadence, maximum
frame-read gap, signal-check counts, native/simulator snapshots and cleanup flags.

- Native counters are **session totals**, including startup/warm-up and, in
  the disappeared-peer case, its shutdown/failure wait. Do not subtract snapshots
  across reconnects: they belong to distinct owners.
- `spectrumFramesProduced` is the inclusive sequence span from the first to last
  observed frame. `spectrumFramesPerSecond` divides it by observation duration;
  it includes retune/startup settling and reader pauses. It is not a render FPS
  or sample-to-screen latency measurement.
- `spectrumFramesCoalesced` is the difference between the first and last frame's
  cumulative coalescing counters. Sequence gaps can also include pending frames
  invalidated by Tune; do not equate every unread frame with transport loss.
- Resources are sampled initially, every approximately five seconds and at the
  end of the observation window. Peak RSS means **sampled** peak, not every
  transient OS allocation. Private bytes are null if the platform reports no
  usable value; null is not zero memory consumption.
- CPU is cumulative process CPU seconds divided by elapsed observation time,
  expressed as percent of **one logical core**; multi-core workloads can exceed
  100%. RSS/private memory, approximate managed live bytes, cumulative managed
  allocation and thread counts include the engine, simulator, harness, runtime
  and reporting. They do not isolate DSP CPU or native allocations.
- No forced GC is performed. RSS growth, heap size, allocation volume and .NET
  thread-pool changes are diagnostics, not a leak detector or an enforced
  performance budget. The inherited native topology retains substantial memory.
  Read sanitizer/leak results separately and compare like-for-like hosts/runs.
- Simulator aggregate socket errors are retained, including replies racing
  native socket teardown. Native socket errors are forbidden during observation;
  only the explicit peer-disappearance check permits them after observation.
- `pacingResyncs` records simulator clock rebases after host scheduling pauses.
  The simulator limits catch-up to eight packets, then rebases when farther
  behind; it does not dump a long replay burst into CM. Rebases can lower
  wall-time throughput and remain visible; no native input-overrun allowance is
  introduced to make such failures pass.

State/loop bounds are checked, but there is no new native allocation-failure or
hard real-time guarantee. A native crash or hung native constructor/destructor
still needs the process/CI watchdog; cooperative cancellation cannot forcibly
interrupt arbitrary native code safely.

## Qualification scope

The short campaign, input/error policy tests and real cancellation/observer-error
cleanup tests supplement the existing receive regressions. Source-specific
platform and long-run results are recorded in [CI results](NATIVE_CI_RESULTS.md).
The first Linux short run exposed substantial RSS growth between reconnect
phases despite passing native leak checks; see the [open memory investigation](NATIVE_CI_RESULTS.md#open-follow-up-linux-reconnect-memory).
Long Linux reconnect campaigns can need much more memory than a steady session;
the optional long mode is not a claim of memory qualification.
Simulator endurance is not live-radio qualification or completion of M4: P1
streaming, real G2 RX, Windows-reference comparisons and hardware performance
checks remain pending. Desktop/audio output and native TX integration remain
separate work.
