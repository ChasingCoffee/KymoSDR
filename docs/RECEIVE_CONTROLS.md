# Simulator-backed receive mode and filter controls

The P2 receive owner now supports **USB and LSB**, with adjustable low/high
audio-frequency filter edges. This is a simulator-only M4 engine increment,
not a hardware receiver, audio output, UI or TX implementation.

Follow-up: [shared gain/mute/AGC controls](RECEIVE_GAIN.md) now apply to P1 and
P2. The fixed-gain descriptions and results below document this earlier checkpoint.

## API contract

```csharp
var ssb = new ReceiveDemodulation(ReceiveMode.Lsb, LowCutHz: 300, HighCutHz: 2700);
using var session = P2ReceiveSession.Open(nativeDirectory,
    new P2ReceiveOptions(simulator.BasePort, FrequencyHz: 14_201_000, Demodulation: ssb));
session.ConfigureDemodulation(new(ReceiveMode.Usb, 700, 1400));
ReceiveDemodulationState applied = session.Demodulation;
```

`simulator` must be an owned IPv4-loopback peer. The engine still rejects
non-loopback addresses and exposes no transmit, radio-routing or audio-device
control. Defaults remain USB, 300–3000 Hz, fixed unity AGC gain and panel gain.

- `0 <= LowCutHz < HighCutHz <= 12000`, with width at least 100 Hz. These are
  the initial SSB API bounds, not a qualification of every possible filter shape.
  Undefined modes, reversed/out-of-range/narrow edges and null updates are rejected.
- Edges are positive **audio frequencies**. Relative to the RF/spectrum center,
  USB selects `[low, high]` and LSB `[-high, -low]`. The public signed-edge
  properties use this RF convention, not raw WDSP coefficient arguments.
- Startup options are applied before the native receive worker starts.
  Applied settings are read back from the native owner, with generation 1 on
  each new session. A changed pair of mode/filter settings increments generation
  once. Identical updates are no-ops; rejected updates leave it unchanged.
- Reopening uses explicitly supplied settings (or defaults), not hidden settings
  retained by the previous session. A stopped/failed peer rejects updates.
- Updates clear the bounded queued audio tap without reporting an overrun.
  Internal DSP/I/O history still settles afterward: generation is **not** a
  sample-accurate audio boundary, and click-free switching is not promised.
- Spectrum is pre-demodulation. Mode/filter updates neither mirror its frequency
  axis nor change its tuning generation. No display-side filtering is applied.

The controls use separate native ABI 1 entry points; the existing 24-field
receive-state and spectrum ABIs are unchanged. The old native open function
keeps default USB behavior. The new managed engine explicitly requires the
controls ABI before allocating a session; rebuild/stage the matching native
library rather than using an older DLL/dylib/so.

## DSP and locking boundary

The bridge calls WDSP `SetRXAMode` and the collective `RXASetPassband`, updating
the primary notched passband, secondary bandpass and SNBA output bandwidth.
The existing Windows reference updates these same three filters. No inherited
DSP algorithm source is changed.

The loopback I/Q and spectrum contract uses `I+jQ`, with `exp(+j*w*t)` at positive
RF offset. In this source, `fir.c:fir_bandpass` generates negative-sine complex
coefficients. The bridge therefore negates/reverses RF edges for WDSP. The first
new signal test failed when it passed the RF signs directly to that collective
setter (wanted-tone RMS about 3e-9). The old fixed-tone test only set a secondary
bandpass, which was disabled in this SSB configuration; its active primary
filter still used constructor defaults. Explicit both-sideband/filter rejection
tests now cover the convention instead of relying on those defaults.

Physical Saturn/G2 wire samples have the opposite complex rotation for a
positive RF offset. The G2-only ingress therefore conjugates Q once before
both the spectrum tap and WDSP. The shared RF-edge translation above remains
unchanged, as do P1/P2 simulator codecs. This distinction was established by
live FT8 decoding during the [G2 checkpoint](G2_HARDWARE_RECEIVE.md): before
normalization, selecting LSB instead of USB recovered 14.074 MHz FT8. Native
hardware-profile fixtures now check positive/negative RF spectrum positions,
wanted-sideband audio and opposite-sideband rejection with legacy/Saturn-order
samples. A matching simulator alone is not independent wire-orientation proof.

Changes are dispatched to the existing native lifecycle thread to preserve
allocation locality. The existing managed gate serializes session operations;
the native command gate rejects reentrancy. Native updates acquire CM RX0's
update lock, then its recursive DSP lock, to apply both settings together.
The tap queue is cleared under the state lock after releasing the DSP lock.
No path waits for CM/DSP while holding that state lock. Reads remain on their
caller threads. Disposal/finalization still joins the receive and CM workers.

## Run and validate

After building the native module and managed solution:

```sh
dotnet run --project src/Thetis.Headless -c Release --no-build -- receive-controls-selftest --native-dir ABSOLUTE_NATIVE_STAGE_DIRECTORY
```

The command starts its own ephemeral loopback simulator; do not start a second
peer or provide a radio address. Its fixed fixtures exercise two sessions:
USB at 14.199 MHz and reopened LSB at 14.201 MHz, both receiving the same
14.200 MHz synthetic signal. Each session checks startup passband, opposite
sideband rejection, high-cut rejection, low-cut rejection, narrow passband and
restored passband, then confirms the pre-filter RF spectrum is unchanged.

Measurements drain 48,000 stereo frames for settling, check both channels are
finite even during settling, then measure at least 8192 frames within an
eight-second deadline. Passbands require RMS `0.25/sqrt(2) ±0.005` and tone
`1000 ±20 Hz`; rejection requires RMS `<0.00056` (approximately 50 dB down).
Zero-crossing frequency at the rejected noise floor is diagnostic only.
Native errors, overruns, missing packets and audio drops must stay zero.
Both sessions must stop, release their native port, and produce no TX packets
or watchdog stops. Ctrl-C cooperatively cancels startup/measurement and disposes
owners. Native hangs still require the external test/process watchdog.

Exit codes are 0 pass/help, 2 syntax, 3 incompatible/missing native library,
4 execution/measurement failure, and 130 cancellation. JSON schema 1 records
all twelve checks, applied controls/generations and native/simulator counters.

Native CTest adds six live signal phases at 48 kHz, ABI/canary/validation tests,
custom-LSB startup rollback at all five stages and close/reopen checks. The
managed suite adds the 192 kHz campaign, argument validation, generation/no-op
behavior, cross-thread updates concurrent with pulls/disposal, failed-peer
rejection and cancellation cleanup. CI runs the new CLI on Windows/macOS/Linux
and the native fixtures under Linux ASan/UBSan/LeakSanitizer.

## Local validation

Implementation source: `06416f06ee276687f1cb99ab37a38165a6c615e4`.
macOS arm64 passes all 145 managed tests (111 Core, 34 Engine), eight native
CTest cases and eight local ASan/UBSan CTests. Local LeakSanitizer is disabled;
the separate Linux CI job enables it. The Release build has no warnings.

The production native build (`BUILD_TESTING=OFF`) passes the complete controls
CLI in 15.146 seconds: wanted RMS 0.176734–0.176778, wanted-tone frequency
996.215–1002.028 Hz, maximum rejected RMS 1.797e-8. Native socket/DSP errors,
input overruns, missing packets and audio drops are zero; TX packets and
watchdog stops are zero. Raw report:
`artifacts/receive-controls-production.json` (ignored local artifact).
An actual terminal Ctrl-C during the command exits 130 after owner disposal.

## Cross-platform validation

The [native workflow at 06416f06](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34172762561)
passes on all three platforms, recorded 2026-09-07:

| Target | Native CTest | Managed tests with native library | Controls CLI | Wanted RMS range | Maximum rejected RMS |
| --- | --- | --- | --- | --- | --- |
| Windows x64 | 7/7 | 145/145, no skips | 12/12 in 16.506 s | 0.176719–0.176824 | 1.7972e-8 |
| macOS arm64 | 8/8 | 145/145, no skips | 12/12 in 18.662 s | 0.176736–0.176820 | 1.7983e-8 |
| Linux x64 | 8/8 | 145/145, no skips | 12/12 in 17.064 s | 0.176712–0.176847 | 1.7977e-8 |

Native socket/DSP errors, input overruns, missing packets and audio drops are
zero in all three controls reports; STOP, port rebind and no-TX checks pass.
The existing DSP/receive CLIs, short receive fault/soak campaign, 100-cycle
offline CM and 100-cycle loopback transport diagnostics also pass on every OS.
The Linux sanitizer job passes eight native CTests with ASan, UBSan and leak
detection enabled, without suppressions.

The existing Linux 20-cycle async and six-caller reconnect memory guards remain
green with the new filter initialization, without changing their thresholds or
using forced GC/allocator trimming. Maximum post-warmup RSS growth is 14,901,248
and 15,118,336 bytes respectively; reserved growth is 126,976 and 106,496 bytes.
These are reconnect checks, not a long-duration arbitrary-filter-churn budget.
The [managed-only workflow](https://github.com/ChasingCoffee/KymoSDR/actions/runs/34172762512)
passes all three OSes with 120 passes / 25 native-dependent skips. The opt-in
legacy Windows-reference build remains skipped.

This does not qualify AM/FM/CW, AGC controls, arbitrary filter responses, P1
streaming, real G2 receive or UI/audio output.
