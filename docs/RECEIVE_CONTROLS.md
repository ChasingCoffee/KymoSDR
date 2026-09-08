# Simulator-backed receive mode and filter controls

The P2 receive owner now supports **USB and LSB**, with adjustable low/high
audio-frequency filter edges. This is a simulator-only M4 engine increment,
not a hardware receiver, audio output, UI or TX implementation.

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

Validation results will be recorded after the cross-platform run. This does not
qualify AM/FM/CW, AGC controls, arbitrary filter responses, P1 streaming, real
G2 receive or UI/audio output.
