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
48 kHz), with a 1,024-frame prefill. A 32-tap/1,024-phase windowed-sinc resampler
converts to the selected output rate. Callback buffers are supplied by PortAudio;
coefficients/storage are allocated before starting the stream. Atomic counters
expose queue depth, submitted/rejected/rendered frames, priming/starvation,
queue/driver underruns, nonfinite input, clipping and pre-mute peak.

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
starvation/overflow behavior. This is deterministic signal/lifecycle coverage,
not a physical device clock or latency qualification. There is no adaptive hardware-clock drift
correction yet; long sessions with independent radio/device clocks may exhaust
the queue. Driver hangs, actual unplug, sleep/wake and 60-minute underrun-free
listening remain manual qualification gates, not simulated-device claims.

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
physical-device playback from a no-device or native-window test.

Native tests cover ABI/capacity canaries, three-rate 1 kHz amplitude/frequency,
mute, queue overflow, explicit starvation/re-prime, nonfinite/clipping, flush,
simulated output loss, 100 no-device output lifecycles and concurrent
producer/consumer wrap. Managed tests cover P1/P2 tone recovery,
rollback, invalid controls, cancelled startup, interrupted output, concurrent
dispose and reopening. Linux CI includes ASan/UBSan with leak detection; local
macOS sanitizers do not enable leak detection.

No physical playback or RF transmission was used for this implementation's
automated validation. Actual sound quality, stereo routing, device-specific
latency/rates/loss, resampler wideband response, sustained clock drift, speech,
noise and calibrated RF levels remain unqualified. Do not connect this preview
to a G2: hardware streaming is deliberately absent. The existing G2 receive-only
ANT1 constraint is unchanged.

The callback design follows [PortAudio's callback restrictions](https://files.portaudio.com/docs/v19-doxydocs/writing_a_callback.html)
and [stream lifecycle API](https://files.portaudio.com/docs/v19-doxydocs/portaudio_8h.html).
The vendored source identity and licenses are recorded in [DEPENDENCIES.md](DEPENDENCIES.md).
