# Simulator-to-native P2 receive checkpoint

This connects the .NET G2 simulator to the portable engine. Unlike the earlier
discard-only [transport probe](TRANSPORT_LOOPBACK.md), synthetic RF now traverses:

```text
G2 simulator UDP I/Q → native P2 decoder → existing ChannelMaster router
                    → CM input ring/worker → WDSP analyzer → latest spectrum frame
                                          → WDSP RX0/sub0 → bounded audio pull
```

This is an initial M3/M4 integration checkpoint, not a usable hardware receiver
or completion of M4. **Every endpoint is IPv4 loopback; no live radio is contacted.**
The physical G2 remains receive-only on ANT1. Previous approval of simulator TX
does not authorize hardware TX, and this receive API exposes no transmit control.

## Run the complete test

Build the [native module](NATIVE_DSP.md) and managed solution, then:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- receive-selftest --native-dir ABSOLUTE_NATIVE_STAGE_DIRECTORY
```

On macOS/Linux the directory is `$PWD/artifacts/native/stage/Release`; on Windows
use `(Resolve-Path artifacts/native/stage/Release).Path` in PowerShell. This CLI
creates its own simulator on an available loopback port layout. Do not start a
separate simulator or supply a radio address. There are no NIC, discovery, TX,
mode, rate or audio-device CLI options at this checkpoint.

The test configures DDC2 at 192 kHz, USB with a 300–3000 Hz passband, fixed unity
AGC gain and unity panel gain. A 14.200 MHz synthetic RF tone first produces
approximately 1 kHz audio at 14.199 MHz tuning, then 1.5 kHz at 14.1985 MHz.
It checks finite audio, RMS and frequency, plus advancing spectrum frames before
and after tuning: the baseband peak moves from about +1 kHz to +1.5 kHz while
its RF position stays at 14.200 MHz, within one FFT bin. It discards mic input,
observes status, and checks STOP reached the simulator. JSON schema version 2
adds `spectrumBeforeTuning` and `spectrumAfterTuning` to the audio measurements,
packet/buffer counters and final RX/TX state. No sound is played or UI drawn.

Each tone measurement has an eight-second wall-clock deadline, skips one second
of generated audio for settling, and measures at least 8192 frames. Cancellation
is checked during startup and measurement; Ctrl-C disposes both owners. Exit
codes: 0 pass/help, 2 syntax, 3 missing/incompatible native library, 4 execution or
measurement failure, 130 cancellation. Help and invalid arguments load no native
code and create no sockets. Each spectrum measurement has a five-second deadline,
checks two distinct frames and drains audio while waiting.

## Engine boundary and ownership

`P2ReceiveSession.Open(nativeDirectory, options)` supports one selected DDC0–9 at
48/96/192/384 kHz, routed to CM receiver 0 / WDSP subchannel 0. Other RX channels
and the TX channel remain inactive; the inherited 8-stream/5-RX/2-sub/1-TX core
topology is retained. DDC and rate are chosen at open, not changed in-place.
`Tune(frequencyHz)` changes the RF phase word at the next control heartbeat.
`ReadAudio(double[])` non-blockingly pulls interleaved L/R frames at 48 kHz.
`ReadSpectrum()` returns the latest unread `ReceiveSpectrumFrame`, or null.

Audio is tapped immediately after `fexchange0`, before the pipe, VST processing
and main audio mixer. It is **not** a device-output, VAC, TCI or mixer test.
The API uses a native pull ring, not a retained managed callback. Startup's
synchronous checkpoint delegate is rooted and exception-contained. SafeHandle
provides abandonment cleanup; use deterministic disposal for timely STOP.
The startup token does not remain registered after `Open`; callers must dispose
an active session when their operation is cancelled.

Managed owners exclude concurrent DSP diagnostics, offline CM sessions and the
old socket probe. Native startup checkpoints are completed CM core, RNet,
socket, stop event, and packet/control worker. Partial startup unwinds in reverse.
Legacy DSP constructors still fail fast on OOM/thread allocation failure; this
is rollback at completed boundaries, not a new recoverable-allocation contract.
Close signals and joins the network producer before destroying RNet, joins CM
consumers before destroying their DSP, then frees the audio ring's lock. WDSP's
formerly detached flush worker is now also explicitly joined before its input/
output buffer storage and synchronization objects are released.

## Spectrum frame contract

The tap is CM RX0 input **after the blankers, before demodulation/filter/AGC**.
It uses the analyzer already allocated by ChannelMaster (display 0), configured
once at startup: complex 4096-point FFT, Hann window, no averaging, neutral
calibration. A private synchronous adapter feeds complete I,Q blocks into
WDSP's existing `Cspectra`/elimination/detector/pixel kernels and pulls `GetPixels`.
There is no new DSP algorithm, legacy async dispatcher or detached FFT work.
The existing joined CM worker owns FFT execution; the normal legacy panadapter
run flag stays off. Joining CM before analyzer destruction covers active FFTs.

- A frame owns 4095 copied float levels; later pulls, retunes and disposal do
  not reuse that storage. Native storage holds only the latest unread frame,
  plus one partial FFT. Managed empty polls reuse scratch buffers and allocate
  no frame. `CoalescedFrames` counts unread frames replaced by newer ones.
- `Sequence` increases per published frame; `TuningGeneration` starts at 1 and
  increments on every Tune call. Both are session-local. `PublishedMonotonicMilliseconds`
  is native host publication time, not UTC, .NET Stopwatch ticks or a radio
  sample timestamp. Publication timing can include input backlog/scheduling delay.
- `RequestedCenterFrequencyHz`, DDC, sample rate and FFT size describe the
  configuration. WDSP's complex elimination omits the negative Nyquist bin;
  4095 pixels make its pixel interpolation exactly one FFT bin per pixel.
  `FrequencyAt(p) = requestedCenter - rate/2 + (p+1)*rate/4096`. This axis is
  symmetric around center, with DC at pixel 2047; do not assume pixel 0 is -Fs/2.
- Levels are **uncalibrated dB relative to unit complex amplitude**, not dBm or
  dB/Hz. `IsCalibrated` is false. A coherent 0.25-amplitude tone peaks near
  -12.04 dB; off-bin Hann scalloping changes its peak. At 192 kHz bin spacing
  is 46.875 Hz. No interpolation-based tone-frequency estimator is implied.
- Non-overlapping FFTs are sample-paced: approximately 11.72 frames/input-second
  at 48 kHz and at most 20 at 96/192/384 kHz, skipping input between FFT windows
  at higher rates. This is not a wall-clock or real-time performance guarantee.
  A future UI can read at its own cadence without building a history queue.
- Tune invalidates pending and partial frames atomically, waits for the matching
  control command to be sent, then discards spectrum input for 250 ms before
  accumulating a complete new FFT. Rapid retunes supersede earlier requests.
  P2 has no sample-accurate tuning acknowledgement in this boundary: this
  settling interval is a simulator-tested policy, **not proof that arbitrarily
  delayed I/Q belongs to the new tuning**. Metadata deliberately says requested,
  not hardware-confirmed, center. Audio retains its existing settling behavior.
- `MissingPackets` and `InputOverruns` are cumulative snapshots at publication,
  not a per-FFT discontinuity map. Missing samples are not filled; a frame may
  cross a gap. Non-finite output or analyzer failure increments `DspErrors` and
  does not publish that frame. A stopped peer does not erase the last unread
  frame: consumers should use timestamp/receiver health to indicate staleness.

The separate spectrum ABI 1 uses float pixels and twelve fixed-width int64
metadata values, leaving the original 24-value receive-state ABI unchanged.
Managed startup checks the new ABI before acquiring native resources. See
`native/interop/cm_p2_receive.h` for native capacities, fields and error returns.
This API is renderer-independent; no Avalonia, Skia or graphics dependency is added.

## Wire format and limits

The new native codec implements a **narrow validated P2 subset**, using the
inherited `network.c` control layout and signed-sample conversion plus the pinned
[Saturn reference already used by the simulator](G2_SIMULATOR.md#identity-and-provenance).
It calls the existing router, CM ring and WDSP algorithms; it does not port the
entire legacy read/keepalive/output-ring engine or duplicate the DSP algorithms.

- Exact 1444-byte RX packets: 16-byte header, 238 complex signed-24-bit I/Q pairs,
  big endian. Header bit-depth/count are checked before conversion. Timestamps
  are currently ignored. Signed conversion avoids the legacy signed-left-shift UB.
- General/RX setup is repeated every second, HP RUN/tuning every 100 ms. One
  worker owns all sends and performs a bounded 20 ms receive poll. STOP is sent
  best-effort on exit. General specifies relocated ports; base 1024–65515 reserves
  the full ten-DDC span through B+20. No handshake with discovered capabilities
  or firmware identity is implemented.
- Only the configured loopback IP and selected DDC source port are accepted for
  I/Q. Status at B+1 is counted; mic at B+2 is counted and discarded, never routed
  into TX. Other endpoints, malformed packets and oversize packets are counted.
- Sequence wraparound is supported. Gaps count missing packets; duplicates and
  older/out-of-order packets are discarded. There is no reorder queue, zero-fill,
  concealment, timestamp-based timing or real-time latency guarantee.
- CM input occupancy is bounded under the producer/consumer lock. An infusion
  that cannot fit is discarded and counted as `inputOverruns`, separately from
  network gaps. The 16384-frame audio pull queue drops its oldest frames when
  full and increments `audioDropped`; these policies are deliberate, not silent
  overwrites. Slow-consumer and ring-wrap tests cover both bounds.
- Socket errors or three seconds without accepted I/Q stop the worker and RUN
  heartbeats. `socketErrors` includes this inactivity timeout. The owner stays
  allocated until disposed; `socketWorkers` counts owned workers awaiting join,
  not a health flag. Further tuning is rejected after worker failure.
- PTT, CWX, DUC, drive, PA and relay fields stay zero. There is no TX-specific,
  TX-I/Q, speaker-audio, CW or arbitrary-packet send API. Both managed and native
  validation reject LAN targets; do not remove this gate to try the physical G2.

## Validation and remaining work

Local macOS tests cover the complete simulator chain, retuning, DDC0/2/9 across
all four rates, audio/spectrum amplitude, positive/negative spectrum offsets,
rapid tuning, frame storage isolation, packet loss, foreign traffic, slow readers,
peer disappearance, cancellation, concurrent disposal and SafeHandle cleanup.
Native CTest also feeds real loopback packets through WDSP, including sequence
wrap/gap/late/malformed fixtures, three active reconnect cycles, ten signed
startup rollbacks, event/thread failure injection, CM ring overflow/wrap and
worker restart. Spectrum tests check signed coherent tones, DC, both edge bins,
zero input, exact level scaling, ABI canaries, coalescing and tune invalidation.
The native fixture runs under ASan/UBSan, without .NET, so active
decoder/routing/DSP/FFT work is instrumented. Existing 100-cycle offline CM/probe
tests remain separate. See [the CI validation record](NATIVE_CI_RESULTS.md) for
source-specific platform results; these are finite tests, not soak qualification.

macOS's `task_threads` can temporarily count an exited pthread after a successful
join (also reproduced with a no-op pthread, without WDSP/CM). OS thread-count
assertions therefore poll at most 100 times with 1 ms sleeps when above the
baseline. Joined-owner counters must still be zero immediately. A platform test
holds a real worker alive to verify that this observation helper does not hide it.

Advancing renderer-independent spectrum frames are now exposed through this
same simulated receive session. The [receive endurance campaign](RECEIVE_SOAK.md)
adds sustained observation, resource/cadence reporting and isolated loss,
slow-reader, peer-disappearance and reconnect scenarios. Its long mode is opt-in;
see the validation record for the durations actually qualified on each OS.
Still pending: P1 receive,
multi-DDC/multi-RX and rate changes while running, native TX-to-simulator
integration, audio devices/UI/VSTs, firmware/Windows-reference comparisons,
long-run CPU/memory/latency budgets and explicitly authorized live G2 RX.
