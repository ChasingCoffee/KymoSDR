# Receiver desktop preview

The first M5 increment is an Avalonia/.NET 10 receiver window sharing the existing
P1/P2 native CM/WDSP engine. Simulators remain the default, with a separate,
explicitly confirmed **G2 Ethernet RX-only** source. No microphone or TX controls
exist. Hardware receive is restricted to the reviewed ANT1/20m profile and a
maximum of 60 seconds per connection; it does not complete M4/M5 qualification.

## Build and launch

Build the native libraries using [NATIVE_DSP.md](NATIVE_DSP.md). Linux additionally
requires `libasound2-dev`; desktop rendering requires a working graphical session
and font configuration (`libfontconfig1` on Ubuntu). No ASIO SDK is required.

From the repository root (macOS/Linux):

```sh
export AVALONIA_TELEMETRY_OPTOUT=1
dotnet restore Thetis.CrossPlatform.slnx --locked-mode
dotnet build Thetis.CrossPlatform.slnx -c Release --no-restore
dotnet run --project src/Thetis.Desktop -c Release --no-build --no-restore
```

PowerShell uses `$env:AVALONIA_TELEMETRY_OPTOUT = '1'` before the same dotnet
commands. If necessary, append `-- --native-dir ABSOLUTE_NATIVE_DIRECTORY` to
the launch command. Both `thetis_wdsp` and `thetis_audio` libraries must be in
that directory, with the same architecture as .NET.
The app requires playback ABI 2 plus additive routing/meter ABI 1: rebuild both
the native audio library and managed app, including published `native` copies.

The default native-directory search is `THETIS_NATIVE_DIR`, then a `native`
folder beside the published program, then `artifacts/native/stage/Release`
relative to the working directory. This is a developer preview, not a signed
macOS `.app`, Windows installer or self-contained release.

## Use

The following steps describe the default simulator sources. For a physical G2,
read the [hardware safety contract](G2_HARDWARE_RECEIVE.md) first, select
**G2 Ethernet · RX only**, and click **Discover radios · Ethernet**. Choose a G2
from the results to fill local Ethernet IPv4, radio IPv4 and MAC, or enter those
fields manually. Discovery is P2-only, bounded to four seconds across Ethernet
interfaces, uses subnet-directed broadcasts (not Wi-Fi/general broadcast), and
never starts receive or TX. Results show each route and idle/busy state; even a
single result needs a choice. Unsupported radio profiles are omitted with a count.
Partial scans are labelled; retry if needed. Disconnect/window close cancels and
joins discovery. A busy result is informational, not permission to take over.
Each connection requires fresh ANT1 receive-only confirmation and an idle,
matching Saturn discovery response. Selecting the source presets **14.074 MHz
USB, 100–3000 Hz**. The **FT8 preset** button restores those controls; Apply
commits them when already connected. The spectrum view shows a 12 kHz span with
−150..−50 uncalibrated dB limits (not dBm or an FT8 decoder).

The native deadline closes both radio receive and playback and returns the UI
to disconnected/muted. The last valid connection fields and source choice are
remembered, but there is no automatic discovery/reconnect, interface fallback or
restored ANT1 confirmation. Connect always repeats the targeted identity/idle
check on the selected Ethernet address. Editing the endpoint fields clears
confirmation. Optional launch arguments override the saved target and only
prefill the form; they never connect or unmute:

```sh
dotnet src/Thetis.Desktop/bin/Release/net10.0/Thetis.Desktop.dll --native-dir ABSOLUTE_NATIVE_DIRECTORY --no-settings --g2-nic LOCAL_ETHERNET_IPV4 --g2-target G2_IPV4 --g2-mac G2_MAC
```

1. For silent testing, select **No device (silent monitor)** and click **Connect simulator**.
   First run with a new settings store selects the supported system-default
   **audio output** without opening it; saved selections take precedence later.
   P2 starts with 192 kHz I/Q; P1 has one 48 kHz receiver. The synthetic signal is
   at 14.200 MHz, with the receiver initially tuned to 14.199 MHz USB.
2. Enter a frequency in Hz, choose USB/LSB, filter edges, AGC preset/maximum and
   AF gain, then click **Apply controls**. Enter in a numeric field also applies.
   Moving the AF slider changes its proposed value; **Apply needed** identifies
   an unapplied gain edit. Apply commits it; the tooltip shows applied AF.
3. **Mute output** applies immediately to the connected session. Unmuting the
   silent monitor measures PCM without playing sound. No device selected explicitly
   stays No device on later launches; missing saved outputs never fall back to speakers.
4. For listening, select an output and **Stereo output pair**, then Connect
   (or **Switch output** if already receiving). Use **Refresh devices** if needed.
   For the 828,
   Main Out is 1–2, Phones 1 is 11–12 and Phones 2 is 13–14. It starts muted with
   AF gain at or below −40 dB. Lower system/speaker volume before listening.
   **Start listening** unmutes at the applied gain for simulators. For G2 it
   explicitly applies AF **−10 dB**, medium AGC / max **80 dB**, then unmutes—the
   settings the user confirmed audible. It does not apply pending tuning edits.
   The mute checkbox still unmutes without changing the applied gain/AGC.
5. During receive, output/pair choices remain pending until **Switch output**.
   A successful handoff has a brief audio gap, keeps the radio/spectrum connected,
   and preserves applied frequency/filter/AGC/AF/mute—not pending control edits.
   Refresh while connected also briefly pauses audio, reopens the active route
   after metadata revalidation, and does not switch to a new system default.
   Reconnect always restores mute and conservative gain. Settings are separate
   from legacy Thetis. The selected device/pair is saved without its temporary
   index and matched to freshly enumerated metadata next launch; unmute/AF level
   are never restored. Missing, ambiguous or changed outputs stay unselected with
   a warning and no fallback. **Forget saved output** clears the bookmark.
6. After an output failure, the app stops the complete receive session
   and clears the active output selection. Refresh devices, review/choose an output and
   explicitly reconnect; it will remain muted until you unmute it again.
7. **Export diagnostics…** saves a local bounded session report during receive or
   after disconnect/failure. No waveform/device names/addresses/paths are included;
   nothing is uploaded by the app. See [diagnostics, safe settings and desktop
   endurance](DESKTOP_RELIABILITY.md), including `--no-settings` and opt-in long runs.

The L/R **output meter** shows 100 ms post-mute software levels: RMS bars and
peak labels in dBFS. Muted/disconnected/switching levels clear; an unmuted no-device output
is explicitly labelled **silent monitor · no speakers**. CLIP reflects the
current output's lifetime clipping counter (reset on switching). Meter activity proves software PCM exists,
not that the interface's mixer, headphones or speakers are audible. This is not
an RF S-meter. Output routing never adjusts hardware/system volume.

Tuning is requested frequency, not hardware acknowledgement. Spectrum levels
are pre-demodulation, uncalibrated dB, **not dBm**. The plot max-pools native bins
to at most 1,024 columns and retains 160 waterfall rows; a 33 ms UI timer reads
the latest immutable frame. New tuning generations reset the waterfall. Slow
rendering skips frames and cannot block DSP or a device callback. This establishes
bounded display storage, not a measured 30-fps/CPU/memory performance guarantee.

Disconnect/window close cancels startup, joins the playback pump, closes the
output, then joins the receiver and simulator. A reported output/receive-worker
failure mutes the pump and the controller closes the session even without a UI.
Stream-finished, driver-status and callback/polling-watchdog faults are latched;
the UI displays the reason. Independently clocked output automatically adjusts
the resampler within ±2,000 ppm; current correction is shown with health counters.
The silent monitor remains sample-driven and needs no correction. Driver close failure
is surfaced; potentially callback-owned native memory is retained and reconnect
is refused, requiring restart rather than risking use-after-free.

## Automated validation

```sh
THETIS_NATIVE_DIR=/absolute/native/stage/Release \
  dotnet test tests/Thetis.Desktop.Tests -c Release --no-build --no-restore \
  --blame-hang-timeout 2m
dotnet run --project src/Thetis.Desktop -c Release --no-build --no-restore -- \
  --smoke --native-dir /absolute/native/stage/Release
```

The first command uses Avalonia's headless platform **with real Skia drawing**:
actual controls connect/tune/apply/mute/reconnect both protocols, validate bad
input, resize, and close during startup. Set `THETIS_DESKTOP_CAPTURE_DIR` to save
PNG captures. A further test injects output loss, verifies selection clearing and
explicit muted reconnect without opening a physical device.
The test harness owns one headless application/UI thread per test
process, serializes UI bodies, closes each window/controller and joins the thread
at assembly cleanup. It avoids two hazards encountered with manual
`HeadlessUnitTestSession` use: blocking disposal from its own continuation and a
12.1.2 startup race that can publish the session before its task field is assigned.
The framework dependency itself is not patched.

`--smoke` opens a native window, connects only its P2 simulator with muted
no-device output, waits for rendered frames, retunes, reports JSON and exits.
Optional `--screenshot ABSOLUTE_PNG` saves a rendered window capture. Linux CI
uses `xvfb-run -a`; this tests X11, not Wayland. Smoke waits have deadlines, and
CI adds a three-minute process timeout. No smoke command can select physical
audio or unmute. Smoke/endurance also reject G2 form prefill and cannot select
the hardware source. Hardware form/validation tests use injected factories and
never contact a radio.

The initial source `e9c352ce2f90a686460a3aeb590915f068b70616` passes both headless tests and
published native-window launches on Windows x64, macOS 26 arm64 and Ubuntu
24.04 x64/X11 in CI. All three also verify exit 4 for missing native libraries.
See [exact hosted results](NATIVE_CI_RESULTS.md). No physical audio stream is
opened by these checks.

The [desktop reliability checkpoint](DESKTOP_RELIABILITY.md) adds bounded report
export, safe preferences and 27 desktop regressions. A 30-minute local headless
Skia run and three-OS 60-second native-window campaigns now exercise retune,
resize and P2/P1/P2 reconnects. Exact source revisions, counts and observed
cadence/resource limits are recorded in [the results log](NATIVE_CI_RESULTS.md#desktop-diagnostics-endurance-and-preferences-checkpoint).
These do not close the physical audio, 30 Hz display or G2 hardware gates.

The later [G2 hardware checkpoint](G2_HARDWARE_RECEIVE.md) adds 31 local desktop
regressions, bounded physical speaker transport and real FT8 recovery through
the shared desktop owner at 14.074 MHz USB. It fixes Saturn I/Q orientation at
ingress, without changing simulator sideband behavior. The refreshed local
developer publish also passes a native-window simulator smoke check (22 distinct
frames in 2.333 s). The user subsequently confirmed hearing G2 audio through
the 828 at AF −10 dB / AGC max 80. Long-session/performance and systematic
GUI-to-hardware/output-route qualification remain separate checks.

The subsequent stereo-routing/meter increment passes **35 desktop regressions**,
including listening presets, pair selection, mute/reconnect and rendered layout
at 900×700. The current published app is refreshed, but this increment's local
native-window smoke could not start: both dotnet and apphost report macOS
render-timer error −6661, with no attached display reported by `system_profiler`.
Headless Skia captures pass; the earlier native-window result above is not a new
graphical smoke pass for this increment. Restart the interactive app to load
the new managed/native code and check the desired physical output pair.

The later discovery/remembered-connection increment adds nine regressions
(**44 desktop cases**) for migration, metadata matching, missing outputs,
selection without connection, Ethernet policy and cancellation. Its local
native-window simulator smoke now passes (21 distinct frames in 2.061 s);
the previous render-timer failure was not reproduced in this session. A separate
discovery-only en7 subnet scan finds the expected idle G2 in 830 ms with no
socket error. It sends two P2 discovery requests, not receive-start/TX packets.

On the initial local macOS agent session, no attached display was reported and
Avalonia's native render timer failed with code −6661 before window startup.
Headless rendering passed; it is not evidence of a successful local native
window. The user subsequently confirmed that the window works and the simulated
tone is audible on their Mac. Actual device/rate, unplug/sleep/wake behavior and
long-session listening remain manual checks; automation does not establish them.
See [playback](AUDIO_PLAYBACK.md) for the audio contract and qualification limits.

Framework/test setup follows the [Avalonia headless documentation](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform).
Pinned framework/license provenance is in [DEPENDENCIES.md](DEPENDENCIES.md).
