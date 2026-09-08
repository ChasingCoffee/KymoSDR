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
   conservative gain; selections/settings are not persisted to disk.

Tuning is requested frequency, not hardware acknowledgement. Spectrum levels
are pre-demodulation, uncalibrated dB, **not dBm**. The plot max-pools native bins
to at most 1,024 columns and retains 160 waterfall rows; a 33 ms UI timer reads
the latest immutable frame. New tuning generations reset the waterfall. Slow
rendering skips frames and cannot block DSP or a device callback. This establishes
bounded display storage, not a measured 30-fps/CPU/memory performance guarantee.

Disconnect/window close cancels startup, joins the playback pump, closes the
output, then joins the receiver and simulator. A reported output/receive-worker
failure mutes the pump and the UI disconnects with an error. Driver close failure
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
PNG captures. Await asynchronous headless-session disposal; blocking disposal
can wait on its own dispatch continuation.

`--smoke` opens a native window, connects only its P2 simulator with muted
no-device output, waits for rendered frames, retunes, reports JSON and exits.
Optional `--screenshot ABSOLUTE_PNG` saves a rendered window capture. Linux CI
uses `xvfb-run -a`; this tests X11, not Wayland. Smoke waits have deadlines, and
CI adds a three-minute process timeout. No smoke command can select physical
audio or unmute.

On the initial local macOS agent session, no attached display was reported and
Avalonia's native render timer failed with code −6661 before window startup.
Headless rendering passed; it is not evidence of a successful local native
window. Launch from a logged-in graphical desktop for manual verification.
See [playback](AUDIO_PLAYBACK.md) for the audio contract and qualification limits.

Framework/test setup follows the [Avalonia headless documentation](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform).
Pinned framework/license provenance is in [DEPENDENCIES.md](DEPENDENCIES.md).
