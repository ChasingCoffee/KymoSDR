# Simulator-to-native P2 receive checkpoint

This connects the .NET G2 simulator to the portable engine. Unlike the earlier
discard-only [transport probe](TRANSPORT_LOOPBACK.md), synthetic RF now traverses:

```text
G2 simulator UDP I/Q → native P2 decoder → existing ChannelMaster router
                    → CM input ring/worker → WDSP RX0/sub0 → bounded audio pull
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
It checks finite audio, RMS and frequency, discards mic input, observes status,
and checks STOP reached the simulator. The JSON includes audio measurements,
packet/buffer counters and the simulator's final RX/TX state. No sound is played.

Each tone measurement has an eight-second wall-clock deadline, skips one second
of generated audio for settling, and measures at least 8192 frames. Cancellation
is checked during startup and measurement; Ctrl-C disposes both owners. Exit
codes: 0 pass/help, 2 syntax, 3 missing/incompatible native library, 4 execution or
measurement failure, 130 cancellation. Help and invalid arguments load no native
code and create no sockets.

## Engine boundary and ownership

`P2ReceiveSession.Open(nativeDirectory, options)` supports one selected DDC0–9 at
48/96/192/384 kHz, routed to CM receiver 0 / WDSP subchannel 0. Other RX channels
and the TX channel remain inactive; the inherited 8-stream/5-RX/2-sub/1-TX core
topology is retained. DDC and rate are chosen at open, not changed in-place.
`Tune(frequencyHz)` changes the RF phase word at the next control heartbeat.
`ReadAudio(double[])` non-blockingly pulls interleaved L/R frames at 48 kHz.

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
all four rates, amplitude, packet loss, foreign traffic, slow audio readers,
peer disappearance, cancellation, concurrent disposal and SafeHandle cleanup.
Native CTest also feeds real loopback packets through WDSP, including sequence
wrap/gap/late/malformed fixtures, three active reconnect cycles, ten signed
startup rollbacks, event/thread failure injection, CM ring overflow/wrap and
worker restart. The native fixture runs under ASan/UBSan, without .NET, so active
decoder/routing/DSP work is instrumented. Existing 100-cycle offline CM/probe
tests remain separate. See [the CI validation record](NATIVE_CI_RESULTS.md) for
source-specific platform results; these are finite tests, not soak qualification.

Next: expose advancing spectrum frames from the existing RX analyzer and feed
them through this same simulated receive session. Still pending: P1 receive,
multi-DDC/multi-RX and rate changes while running, native TX-to-simulator
integration, audio devices/UI/VSTs, firmware/Windows-reference comparisons,
long-run CPU/memory/latency budgets and explicitly authorized live G2 RX.
