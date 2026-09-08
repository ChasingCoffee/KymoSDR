# P1 simulated receive

This is a loopback-only, receive-only engine checkpoint, not P1 hardware support.
It adds one P1 UDP receiver at 48 kHz to the same ChannelMaster/WDSP lifecycle,
audio tap, spectrum analyzer and USB/LSB controls used by P2. No physical radio,
audio output device or transmit path is exercised.

## Run the deterministic signal campaign

Build the native library and managed solution as described in
[native bring-up](NATIVE_DSP.md), then run:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- \
  p1-receive-selftest --native-dir /absolute/path/to/artifacts/native/stage/Release
```

The command owns an ephemeral IPv4-loopback simulator; it has no radio-address,
LAN, TX or audio-device switches. JSON reports fourteen audio checks across USB
and LSB (wanted signal, opposite sideband, low/high cuts, narrow/restored band,
then retuning from 1 kHz to 1.5 kHz), four spectrum measurements, two native
session snapshots and simulator safety/STOP counters. Wanted RMS must be within
0.005 of `0.25/sqrt(2)`, tone frequency within 20 Hz, rejected RMS below 0.00056.
The spectrum must place the fixed 14.2 MHz RF tone within one FFT bin and recover
its complex amplitude within 1.6 dB. At 48 kHz the bin spacing is 11.71875 Hz.

Separate native/managed regressions exercise sequence wrap, missing/duplicate
packets, corrupt second-frame sync, wrong source ports, embedded microphone and
PTT/key sentinels, slow readers, all five startup rollback stages, event/thread
failure, peer loss, reconnect, concurrent disposal and abandoned SafeHandle cleanup.
CLI exits are 0 success, 2 syntax, 3 native loading/ABI, 4 execution failure and
130 cancellation. Cancellation disposes owners; there is no process-global radio stop.

## Shared engine and safety boundary

`P1ReceiveSession.Open(nativeDirectory, new P1ReceiveOptions(port))` defaults to
14.199 MHz, USB 300–3000 Hz. Only canonical IPv4 loopback addresses and ports
1024–65535 are accepted in both managed and native validation. P1 has no receiver
count/rate selector: DDC0 and 48 kHz are enforced natively too.

Both P1 and P2 derive from `ReceiveSession`, sharing `ReceiveState`, tuning,
demodulation, audio/spectrum pulls and disposal. The former managed
`P2ReceiveState` record is now protocol-neutral `ReceiveState`; its fields and
JSON names are unchanged. Lifecycle/FIR allocations and SafeHandle cleanup stay
on the existing persistent native-owner thread. P1, P2, offline and transport
sessions are mutually exclusive; this does not duplicate the inherited full
native topology or solve its roughly 1.2 GB active-memory footprint.

The added native `ThetisReceiveProtocolAbi()` / `ThetisReceiveOpenWithControls()`
select protocol 1 or 2. Existing P2-prefixed open/pull/control exports are retained
for ABI compatibility; the pulls operate on the single active receive owner.
State, controls and spectrum ABIs remain version 1. For P1, the mic-discarded and
status counters count accepted EP6 datagrams containing those embedded fields,
not standalone mic/status packets.

P1 transport uses a 1032-byte datagram: an 8-byte Metis header and two 512-byte Ozy frames. Each
frame holds three sync bytes, five control bytes and 63 `(I24,Q24,mic16)` groups.
Both sync headers are validated before publishing any of the 126 I/Q pairs.
Signed 24-bit samples are normalized by 8388608; mic and incoming PTT/key bits
never enter TX or the audio path. Sequence arithmetic handles wrap, counts gaps
and rejects late/duplicate packets. It does not reorder, conceal or repair loss.

Startup sends receive setup (48 kHz/one RX and explicit ADC0 mapping), RX0
frequency and `EF FE 04 01` START. Zero-filled host EP2 control packets refresh
frequency every 100 ms. START is retried every 250 ms only until the first valid
IQ packet; a peer stalled for three seconds stops the worker. Shutdown sends
`EF FE 04 00` STOP and joins the producer before CM consumers and socket teardown.
All MOX, drive and host audio/TX-IQ sample bytes remain zero. The ADC mapping
control group also contains TX-attenuation bits, left zero; it cannot key TX.
This control cadence is qualified against simulators, not hardware DAC clocks.

Spectrum/USB/LSB conventions and tuning-generation limitations are the same as
[P2](P2_RECEIVE_INTEGRATION.md) and [receive controls](RECEIVE_CONTROLS.md).
The requested frequency is not a sample-accurate acknowledgement; the engine
discards pending/partial spectrum and waits 250 ms after sending the matching tune.

## Independent hpsdrsim interoperability

The deterministic .NET peer alone is not an independent protocol oracle.
macOS/Linux additionally build the pinned g0orx/piHPSDR reference at
`f6c17bd4347a2d80cdf6080c3c19dbd915648cdc`:

```sh
bash scripts/build-hpsdrsim.sh --loopback
THETIS_NATIVE_DIR="$PWD/artifacts/native/stage/Release" \
THETIS_P1_REFERENCE_SIM="$PWD/artifacts/external/pihpsdr/hpsdrsim-loopback" \
dotnet test tests/Thetis.Engine.Tests -c Release --no-build --no-restore \
  --filter FullyQualifiedName~P1ReferenceTests --logger "console;verbosity=normal"
```

The test starts and finally terminates only its owned child process. The
confinement adapter includes unmodified upstream source, disables reusable
binding, binds both UDP and TCP to `127.0.0.1` on one ephemeral port, checks
outbound UDP destinations, and forces `-P1 -hermeslite2` with no user arguments.
P2 code is linked for upstream references but cannot be started in this mode.
It does not rewrite packet layouts, control decoding or signal generation.
The original `hpsdrsim` binary remains separate and must **not** be launched for
these tests: it listens on all interfaces. No VM or LAN bridge is needed.

The reference exposed the need for explicit ADC mapping: its initial RX ADC is
unselected, producing zeros otherwise. Its generator uses `I=sin,Q=cos`, giving
fixed **−800 Hz and −4000 Hz baseband tones**, plus noise. The test checks both
spectral lines, 800 Hz LSB audio, receipt of the RX frequency command, STOP and
native-port rebind. It cannot prove RF retuning because those upstream tones do
not move with the requested RX frequency. The deterministic peer provides that
complementary signal test. The reference profile also is not an HL2 hardware oracle.

Windows runs the same deterministic/native P1 suite; the POSIX-only reference
test is explicitly skipped without its fixture environment variable. CI runs
the reference separately on macOS/Linux. The inherited Windows application,
actual P1 hardware, higher rates, multiple receivers, TCP streaming and P1 TX
remain unqualified. The broader M4 G2 hardware gate also remains open.

## Validation record

Runtime `986467f5` passes [Windows/macOS/Linux native CI](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34175140277),
including fourteen P1 signal checks per OS, the native/managed fault and lifecycle
tests, independent pinned-reference interoperability on macOS/Linux, and ten
Linux ASan/UBSan/leak tests. Windows runs nine native CTests; macOS/Linux run ten.
The full managed suite has 155 passes and one explicit reference skip per OS;
macOS/Linux run that reference successfully in a separate step.

Local macOS arm64: all 156 managed tests and fourteen deterministic P1 signal checks pass, independent
pinned-reference interoperability passes, and all ten native CTests pass both
normally and under ASan/UBSan (macOS leak detection disabled). Hosted results
and production-build/Ctrl-C checks are recorded in [native CI results](NATIVE_CI_RESULTS.md).
