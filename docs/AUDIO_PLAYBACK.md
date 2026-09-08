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
PortAudio**. Its managed 10 ms scheduler has bounded catch-up and measures tone
frequency/RMS. This is deterministic signal/lifecycle coverage, not a physical
device clock or latency qualification. There is no adaptive hardware-clock drift
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

Native tests cover ABI/capacity canaries, three-rate 1 kHz amplitude/frequency,
mute, queue overflow, nonfinite/clipping, flush, simulated output loss and
concurrent producer/consumer wrap. Managed tests cover P1/P2 tone recovery,
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
