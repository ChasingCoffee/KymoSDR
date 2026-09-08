# Simulator receiver desktop

The first M5 increment is an Avalonia/.NET 10 receiver window sharing the existing
P1/P2 native CM/WDSP engine. It is **simulator-only**: every connection owns an
IPv4 loopback peer, with no hardware-address, discovery, microphone or TX option.
This does not complete M4 hardware RX or the M5 audible-G2/endurance gate.

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
The clock-recovery increment requires playback ABI 2: rebuild both the native
audio library and managed app, including copies in any published `native` folder.

The default native-directory search is `THETIS_NATIVE_DIR`, then a `native`
folder beside the published program, then `artifacts/native/stage/Release`
relative to the working directory. This is a developer preview, not a signed
macOS `.app`, Windows installer or self-contained release.

## Use

1. Leave **No device (silent monitor)** selected and click **Connect simulator**.
   P2 starts with 192 kHz I/Q; P1 has one 48 kHz receiver. The synthetic signal is
   at 14.200 MHz, with the receiver initially tuned to 14.199 MHz USB.
2. Enter a frequency in Hz, choose USB/LSB, filter edges, AGC preset/maximum and
   AF gain, then click **Apply controls**. Enter in a numeric field also applies.
   Moving the AF slider changes its proposed value; Apply commits it.
3. **Mute output** applies immediately to the connected session. Unmuting the
   silent monitor measures PCM without playing sound. No physical output is
   selected automatically.
4. For optional listening, disconnect, click **Refresh devices**, explicitly
   select an output, then reconnect. It starts muted with AF gain at or below
   −40 dB. Lower the system/speaker volume before deliberately unmuting.
5. Disconnect before changing output. Reconnect always restores mute and a
   conservative gain. Receiver controls and window layout are persisted separately
   from legacy Thetis; audio-device selection and unmute/AF level are not restored.
6. After an output failure, the app stops the complete simulated receive session
   and clears the old output selection. Refresh devices, select an output and
   explicitly reconnect; it will remain muted until you unmute it again.
7. **Export diagnostics…** saves a local bounded session report during receive or
   after disconnect/failure. No waveform/device names/addresses/paths are included;
   nothing is uploaded by the app. See [diagnostics, safe settings and desktop
   endurance](DESKTOP_RELIABILITY.md), including `--no-settings` and opt-in long runs.

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
audio or unmute.

Source `e9c352ce2f90a686460a3aeb590915f068b70616` passes both headless tests and
published native-window launches on Windows x64, macOS 26 arm64 and Ubuntu
24.04 x64/X11 in CI. All three also verify exit 4 for missing native libraries.
See [exact hosted results](NATIVE_CI_RESULTS.md). No physical audio stream is
opened by these checks.

On the initial local macOS agent session, no attached display was reported and
Avalonia's native render timer failed with code −6661 before window startup.
Headless rendering passed; it is not evidence of a successful local native
window. The user subsequently confirmed that the window works and the simulated
tone is audible on their Mac. Actual device/rate, unplug/sleep/wake behavior and
long-session listening remain manual checks; automation does not establish them.
See [playback](AUDIO_PLAYBACK.md) for the audio contract and qualification limits.

Framework/test setup follows the [Avalonia headless documentation](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform).
Pinned framework/license provenance is in [DEPENDENCIES.md](DEPENDENCIES.md).
