# Shared receive gain, mute and AGC

The loopback-only P1/P2 engine now exposes post-AGC audio attenuation, mute and
WDSP AGC presets. This is a simulator-backed receive increment, not hardware,
audio-device or desktop qualification. Existing default signal levels remain
unchanged: unity audio, unmuted, AGC off with fixed unity gain.

## Contract

Both `P1ReceiveOptions` and `P2ReceiveOptions` accept optional `ReceiveGain`.
`ReceiveSession.ConfigureGain(settings)` applies live changes; `session.Gain`
returns settings, native attack/decay/hang values and a generation. Startup
settings take effect before RX processing starts. A successful change advances
the generation; an identical or rejected change does not. Reopening without
settings restores the defaults and generation 1.

- `AudioGainDb`: integer −60 through 0 dB, applied after AGC to both audio channels.
  This is AF attenuation, not a radio preamp, ADC attenuation or calibrated RF gain.
- `Muted`: gates subsequent audio pulls to exact zero after configuration returns.
  Queued tap audio is cleared and in-flight DSP/resampler output is gated too.
  Samples already copied to a caller cannot be recalled.
- `AgcMode`: `Off`, `Slow`, `Medium` or `Fast`. Long/custom modes are not exposed.
- `AgcMaxGainDb`: integer 0 through 80 dB, default 60. This limits AGC gain below
  its knee; it is not a target output level and is inactive with AGC off.

All presets use a 1 ms attack and flat gain slope. The inherited WDSP algorithm
has additional hang, fast-decay and pop-response paths: a decay time constant is
not the time to complete recovery from an arbitrary signal step.

| Preset | Native mode | Decay | Hang | Hang threshold |
| --- | --- | --- | --- | --- |
| Off | 0 | 250 ms (inactive) | 0 ms | 100% |
| Slow | 2 | 500 ms | 1000 ms | 25% |
| Medium | 3 | 250 ms | 0 ms | 100% |
| Fast | 4 | 50 ms | 0 ms | 100% |

Slow explicitly restores its hang threshold: inherited fast/medium setters set
it to 100%, but the slow setter alone does not undo that. AF-only and mute-only
changes do not call AGC setters, preserving envelope and lookahead history.
AGC continues tracking incoming signals while muted. The RF spectrum tap stays
upstream of demodulation, gain and mute.

Example using an owned fixture (no LAN address or audio device):

```csharp
await using var simulator = G2Simulator.Open(new(BasePort: 0));
using var receiver = P2ReceiveSession.Open(nativeDirectory,
    new(simulator.BasePort, Gain: new(-20, AgcMode: ReceiveAgcMode.Medium)));
receiver.ConfigureGain(receiver.Gain.Settings with { Muted = true });
```

Gain updates follow the existing lifecycle thread and managed/native command
serialization. Native lock order is CM update → DSP → observer state; no control
waits for DSP while holding observer state. The bounded pull API, exclusive
P1/P2/offline ownership, five-stage cancellation rollback and joined shutdown
remain in place. Gain ABI 1 adds `ThetisReceiveOpenWithGain`,
`ThetisReceiveSetGain` and an eleven-int64 `ThetisReceiveGetGain` snapshot.
Earlier open exports retain their signatures and delegate with default gain.
New managed binaries require the matching gain ABI.

Except for mute's zero-output guarantee, this does not promise immediate,
click-free or sample-accurate audio changes. WDSP/ChannelMaster retain internal
filter, lookahead and output-buffer history. Low-latency playback and transition
ramps remain separate work.

## Repeatable signal profiles

Both simulator options accept an immutable `SignalLevelProfile` of 1–32 strictly
increasing steps, at 0–600,000 milliseconds and finite complex amplitudes 0–0.9.
The initial amplitude applies before the first step. Steps use generated I/Q
sample counts, not host wall-clock timers, and preserve oscillator phase.
START resets phase and profile time; a P2 rate change restarts profile time while
preserving phase. No profile means the previous constant-amplitude behavior.

The new bounded campaign owns its P1 and P2 loopback peers:

```text
dotnet run --project src/Thetis.Headless -c Release --no-build -- receive-gain-selftest --native-dir ABSOLUTE_NATIVE_STAGE_DIRECTORY
```

It accepts no hardware address, transmit, duration or audio-device options. Exit
codes are 0 success, 2 syntax, 3 native-load/ABI error, 4 failed measurement and
130 cancellation. Success JSON records ten gain/ceiling checks and three AGC
step traces per protocol, plus native readback and clean streaming counters.

Checks include startup/live −6/−20 dB attenuation, off/unity, mute/unmute with
AGC off/on, unchanged RF spectrum, 20/60 dB gain ceilings, muted startup and
default reconnect. Each AGC preset receives `.005 → .25 → .005 → 0 → .25` input
and must produce finite bounded audio, correct steady levels, exact settled
silence and recovery. Fast/medium/slow early responses must separate.

Recovery measurements use 100 ms RMS windows. The analyzer first locates an
observed strong-to-weak drop in the bounded 3–4 s audio window, then requires
three consecutive windows at ≥90% of the steady target within three seconds.
It cannot mistake buffered strong audio before the drop for recovery. Raw RMS
windows and the observed drop position are included for reproducibility. These
are fixture audio observations, not hardware latency or exact AGC time constants.

The flat-slope, unclipped steady target for these fixtures is
`(1 − exp(−4)) × .9999 / sqrt(2) ≈ 0.694086` RMS, derived from the inherited WDSP
`wcpAGC.c` output target and a real sinusoid. Weak inputs below the knee instead
follow the configured maximum gain. The WDSP algorithm source is unchanged.

Native tests additionally check buffer canaries, invalid ABI/ranges, actual
preset fields, history-independent preset restoration, byte-for-byte AGC state
preservation during AF/mute changes, exact mute including transition tails and
all five startup rollback stages. Managed tests cover concurrent controls/pulls/
dispose and settings validation. Pure tests verify exact sample-boundary wire
amplitudes/phase and recovery analysis with shifted or missing drops.

## Qualification

Local macOS arm64 signal campaign passes for both protocols. Each recorded
strong-to-weak drop in the 3.2 s audio window; recovery was 300 ms for fast and
1600 ms for medium/slow, with different early envelopes. Peaks were below 0.99
and settled silence was exactly zero. These observations describe this fixture
and inherited buffering, not a universal preset performance specification.

Local Release build has no warnings/errors. All 169 managed tests pass (123 core,
46 engine, including the confined independent P1 reference). Without native
libraries, 134 pass and 35 explicitly skip. All eleven native CTests pass in
Release and macOS ASan/UBSan builds; local leak detection is disabled, with
Linux leak checking retained in hosted CI. The separate `BUILD_TESTING=OFF`
native build passes the gain campaign too, and real Ctrl-C exits 130 after
disposing the owned peers/receiver. JSON is retained locally in ignored
`artifacts/receive-gain-local.json` and `artifacts/receive-gain-production.json`.

Three-OS hosted qualification is pending. No G2/LAN radio or physical audio
output was used.

Still unqualified: hardware RF gain/routing, calibrated metering, noise-rich or
speech AGC behavior, click-free playback, other modes, live radio, desktop UI and
TX. The broader [M4 hardware gate](CROSS_PLATFORM_IMPLEMENTATION_PLAN.md#m4--g2p2-receive-and-p1-simulated-receive)
remains open.
