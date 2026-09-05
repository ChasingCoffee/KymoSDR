# SDR-VST3 macOS Port Plan

## Goal

Create a native macOS port of SDR-VST3 while preserving as much of the existing Thetis/SDR-VST3 radio, DSP, networking, and VST functionality as possible.

Primary target:

- macOS
- Apple Silicon (`osx-arm64`)
- .NET 10
- Native macOS audio
- Native macOS VST3 support
- Cross-platform GPU-accelerated panadapter/waterfall
- Existing ANAN/HPSDR networking and DSP behavior

Do **not** perform a wholesale rewrite.

The port should proceed incrementally, with each phase ending in a runnable and testable state.

---

# Starting Point

Use the modernized SDR-VST3 fork as the foundation:

```text
https://github.com/nubbyless/SDR-VST3
```

Start from the commit where the .NET 10 migration was merged:

```text
275f768
Merge net10-migration into master
```

Create a dedicated macOS branch from that commit:

```bash
git clone https://github.com/nubbyless/SDR-VST3.git
cd SDR-VST3

git checkout 275f768
git switch -c macos-port
```

Do **not** begin from the older .NET Framework 4.8 Thetis-VST tree.

The reason for using SDR-VST3 is that the .NET 10 migration has already removed a large preliminary migration step. The application is still Windows-specific in important areas such as WinForms and DirectX/Vortice, but those are the parts we already expect to replace or isolate for macOS.

---

# Keep ChasingCoffee/Thetis-VST as a Reference

Add the original VST-enabled Thetis repository as a second remote:

```bash
git remote add chasingcoffee https://github.com/ChasingCoffee/Thetis-VST.git
git fetch chasingcoffee
```

The original repository is a **reference implementation**, not the primary porting base.

Use it to identify any VST-related functionality, fixes, behavior, UI details, routing, or state handling that may not be present in the chosen SDR-VST3 base.

Do not automatically merge the old branch.

---

# Phase 0 — Divergence Audit

Before changing platform architecture, compare the chosen SDR-VST3 base against the original VST implementation.

Relevant reference branch:

```text
chasingcoffee/vst-support
```

The first CLI task should be:

> Compare the current SDR-VST3 source at commit `275f768` against `chasingcoffee/vst-support`. Identify functionality present in the ChasingCoffee VST implementation that is missing, changed, or substantially rewritten in SDR-VST3. Pay special attention to VST3 hosting, rack UI, routing, plugin state persistence, parameter handling, audio-chain behavior, scanner behavior, plugin editor behavior, and bug fixes. Do not merge or modify source yet. Produce a concise Markdown report.

Classify findings as:

```text
Already present in SDR-VST3
Improved/replaced in SDR-VST3
Missing from SDR-VST3
Needs manual review
Not relevant to macOS port
```

The purpose is to make sure useful VST behavior is not accidentally lost before beginning the Mac work.

---

# Phase 1 — Platform Audit

Audit the SDR-VST3 codebase and identify which code belongs to these categories:

1. Platform-independent C#
2. Windows UI / WinForms
3. DirectX / Vortice rendering
4. Win32 P/Invoke
5. Native DSP libraries
6. Audio
7. VST3 hosting
8. HPSDR / ANAN networking
9. Configuration and filesystem assumptions

Produce a short audit showing:

```text
Cross-platform already
Windows-specific
Native code requiring macOS build
Unknown / needs investigation
```

Pay particular attention to:

```text
Console/
HPSDR/
display.cs
win32.cs
portaudio.cs
vsthost.cs

ChannelMaster/
wdsp/
VstAudioHost/
VstHostBridge/
```

Do not perform large refactors during this phase.

---

# Phase 2 — Establish Cross-Platform Core Boundaries

Create a platform-neutral project:

```text
SDR-VST3.Core
```

Target:

```xml
<TargetFramework>net10.0</TargetFramework>
```

Move or reference platform-neutral functionality into this project where practical.

Likely candidates:

- radio state
- DSP configuration
- HPSDR protocol logic
- network packet processing
- VFO/frequency logic
- receiver state
- transmitter state
- filters
- meters
- spectrum data models
- settings models
- VST chain models
- CAT logic that does not depend on Win32 APIs

Avoid moving code simply for architectural cleanliness.

The immediate purpose of the Core project is to make the radio/DSP engine callable without WinForms or DirectX.

Core should not directly depend on:

```text
System.Windows.Forms
Vortice.*
user32
gdi32
kernel32
HWND
WASAPI
ASIO
```

Introduce narrow interfaces only when needed to break platform dependencies.

Examples:

```csharp
public interface IAudioBackend
{
}

public interface ISpectrumRenderer
{
}

public interface IVstHost
{
}

public interface IPlatformServices
{
}
```

Do not over-engineer the abstraction layer.

---

# Phase 3 — Create a Minimal macOS Test Harness

Before porting the full UI, create:

```text
SDR-VST3.MacTest
```

Target:

```text
net10.0
osx-arm64
```

Initially make this a console application.

Goal:

Prove that the radio/DSP engine can run on macOS without WinForms, DirectX, or Windows DLLs.

Required milestones:

1. Application starts on Apple Silicon.
2. ANAN/HPSDR discovery works.
3. Radio can be opened.
4. Network packets are received.
5. Native DSP libraries load successfully.
6. RX1 can be started.
7. Spectrum/panadapter samples are produced.
8. Basic receive status is printed continuously.

Example success output:

```text
ANAN discovered: 192.168.1.xxx
Radio connected
RX1 started
Sample rate: 192000
Spectrum frames received: 1234
Signal: -93.2 dBm
```

Do not begin the main GUI port until this works.

---

# Phase 4 — Build WDSP for macOS

Create a macOS build for the existing WDSP native source.

Initial target:

```text
arm64
```

Expected output:

```text
libwdsp.dylib
```

Prefer CMake if practical.

Replace Windows-specific hard-coded imports such as:

```csharp
[DllImport("wdsp.dll")]
```

with platform-neutral native-library resolution.

Prefer:

```csharp
[DllImport("wdsp")]
```

if runtime resolution is sufficient.

Otherwise use:

```csharp
NativeLibrary.SetDllImportResolver(...)
```

Maintain compatibility with:

```text
wdsp.dll
```

on Windows.

Do not rewrite WDSP in managed C#.

---

# Phase 5 — Build ChannelMaster for macOS

Create a macOS build for:

```text
ChannelMaster
```

Initial target:

```text
arm64
```

Expected output:

```text
libChannelMaster.dylib
```

Adapt the existing C# P/Invoke layer so the same managed interface can use:

```text
ChannelMaster.dll
```

on Windows and:

```text
libChannelMaster.dylib
```

on macOS.

Verify:

- library loads
- initialization succeeds
- receiver creation works
- channel/sample-rate configuration works
- spectrum data is returned

---

# Phase 6 — ANAN/HPSDR Networking on macOS

Keep existing protocol code unchanged wherever possible.

Verify in the MacTest harness:

```text
radio discovery
radio identity
connection
RX stream startup
packet reception
sample-rate selection
frequency changes
mode/filter changes
disconnect/reconnect
```

Replace Windows networking APIs only when they actually block macOS execution.

Prefer modern .NET networking APIs rather than creating macOS-specific equivalents to Win32 calls.

Definition of done:

```text
The Mac discovers the ANAN, connects to it, starts RX1,
and continuously receives valid spectrum data.
```

This is the first major viability milestone.

---

# Phase 7 — Isolate Win32 APIs

Audit all P/Invoke usage, especially:

```text
win32.cs
```

Classify each call as:

```text
UI-only
timing
threading
memory
filesystem
process
audio
graphics
miscellaneous
```

Where possible replace Win32 calls with .NET APIs such as:

```csharp
Stopwatch
Task
Thread
PeriodicTimer
System.IO
System.Diagnostics.Process
```

Do not create macOS wrappers for Windows APIs that can simply be eliminated.

Platform-specific code that genuinely remains should live behind small platform-service interfaces.

---

# Phase 8 — macOS Audio

For the first implementation, retain PortAudio if this minimizes changes.

Use the macOS CoreAudio backend.

Create a platform-neutral audio layer such as:

```text
IAudioBackend
```

and implement:

```text
PortAudioMacBackend
```

Test:

1. enumerate audio devices
2. select output device
3. open output stream
4. hear RX audio
5. change sample rates
6. handle device switching
7. detect underrun/overrun
8. recover after device loss where practical

Do not reproduce ASIO-specific concepts on macOS.

Definition of done:

```text
ANAN RX audio can be heard through the selected Mac output device.
```

---

# Phase 9 — Replace the DirectX Display Backend

The current panadapter/waterfall code uses DirectX/Vortice.

Do **not** port DirectX calls individually.

Separate display data from rendering.

Create an abstraction such as:

```csharp
public interface ISpectrumRenderer
{
    void RenderSpectrum(...);
    void RenderWaterfall(...);
    void Resize(int width, int height);
}
```

Retain the current Windows renderer initially if practical:

```text
WindowsDirectXRenderer
```

Create a new macOS renderer:

```text
MacSkiaRenderer
```

Prefer SkiaSharp for the first macOS implementation.

Desired pipeline:

```text
ANAN
  ↓
DSP
  ↓
FFT / spectrum data
  ↓
display model
  ↓
SkiaSharp
  ↓
macOS GPU surface
```

First milestone:

```text
live spectrum trace
```

Then add:

```text
frequency scale
grid
signal labels
receiver passband
filter overlays
cursor
VFO markers
waterfall
meters
TX overlays
```

Do not aim for pixel-perfect Thetis reproduction initially.

Prioritize:

```text
correct data
smooth rendering
Retina scaling
responsive resizing
low CPU/GPU overhead
```

---

# Phase 10 — Minimal Avalonia macOS UI

Use Avalonia for the native macOS desktop UI.

Create a desktop project such as:

```text
SDR-VST3.Desktop
```

or:

```text
SDR-VST3.Mac
```

Target .NET 10 and Apple Silicon first.

The first GUI should contain only:

```text
Main Window
 ├── Panadapter
 └── Waterfall
```

Feed live spectrum data from the proven MacTest radio engine into the renderer.

Definition of done:

```text
A native macOS window displays a live ANAN panadapter and waterfall.
```

Do not port the full control surface before reaching this milestone.

---

# Phase 11 — Minimum Viable Radio Controls

After the live display works, add only the controls required to operate the radio:

```text
frequency / VFO
mode
filter
AF gain
RX gain
RX1 enable
MOX / PTT
S-meter
radio connect/disconnect
audio output selection
```

Then add secondary controls incrementally.

Recommended order:

1. radio setup
2. audio setup
3. display setup
4. DSP settings
5. band controls
6. equalizer
7. VST configuration
8. MIDI/CAT
9. calibration / advanced setup

Avoid trying to recreate every WinForms dialog before the primary radio is usable.

---

# Phase 12 — macOS VST3 Host

Treat VST hosting as a separate subsystem.

Retain the current VST3 DSP-chain concepts and behavior where practical.

Suggested structure:

```text
VstHostCommon
VstHostWin
VstHostMac
```

The macOS implementation should:

- use the Steinberg macOS VST3 module loader
- scan standard macOS VST3 locations
- support Apple Silicon plugins
- process audio without a GUI first
- restore plugin state
- expose parameters
- unload plugins safely

Standard plugin locations include:

```text
~/Library/Audio/Plug-Ins/VST3
/Library/Audio/Plug-Ins/VST3
```

Initial VST milestone:

1. discover plugins
2. load one known arm64 VST3
3. instantiate it
4. configure sample rate/block size
5. process stereo audio
6. modify a parameter
7. save state
8. restore state
9. unload safely

Do not implement plugin editor windows until DSP hosting is reliable.

---

# Phase 13 — Native VST3 Editor Windows

After VST audio processing works:

- remove HWND assumptions
- use macOS-native VST3 editor embedding
- host plugin editors using `NSView`
- support editor resize
- support editor close/reopen
- support multiple plugin windows

Preserve out-of-process plugin hosting if practical because crash isolation is valuable.

Use the ChasingCoffee repository as a behavior/reference source when validating:

```text
rack ordering
plugin enable/disable
state restoration
preset behavior
routing
plugin insertion/removal
parameter persistence
```

---

# Phase 14 — Configuration and Paths

Remove Windows filesystem assumptions.

Use platform-aware .NET paths.

macOS application data should live under an appropriate location such as:

```text
~/Library/Application Support/SDR-VST3/
```

Separate:

```text
configuration
profiles
DSP state
VST state
logs
temporary files
```

Do not write runtime state into the `.app` bundle.

---

# Phase 15 — Preserve Windows Compatibility

Do not unnecessarily break the existing Windows implementation.

Desired long-term structure:

```text
src/

    SDR-VST3.Core/

    SDR-VST3.Desktop/
        Avalonia/shared UI if appropriate

    SDR-VST3.Windows/
        Windows-specific services
        DirectX renderer
        Windows audio

    SDR-VST3.Mac/
        macOS services
        Skia renderer
        CoreAudio/PortAudio

native/

    wdsp/
    ChannelMaster/

    VstHostCommon/
    VstHostWin/
    VstHostMac/
```

Do not force this full directory structure immediately if moving the existing Windows application would create unnecessary risk.

Incremental extraction is preferred.

---

# Phase 16 — macOS Packaging

Only begin packaging after the app runs correctly from the development environment.

Initial publish target:

```text
osx-arm64
```

Example:

```bash
dotnet publish \
    -c Release \
    -r osx-arm64 \
    --self-contained true
```

Bundle all required native components:

```text
libwdsp.dylib
libChannelMaster.dylib
PortAudio/native audio dependencies if required
VST host executable/library
Skia native libraries
```

Then implement:

```text
.app bundle
codesigning
hardened runtime
notarization
```

Review required entitlements for:

```text
network access
audio input
plugin hosting
native code
```

---

# Phase 17 — Testing

## Radio

Test:

```text
ANAN discovery
connect/disconnect
RX1
RX2 where supported
frequency changes
sample-rate changes
mode changes
filters
band switching
PTT
TX
```

## DSP

Test:

```text
spectrum
RX audio
filters
AGC
NR
NB
ANF
equalizer
metering
TX processing
```

## Audio

Test:

```text
device enumeration
device switching
sample-rate changes
long-duration stability
underrun behavior
sleep/wake behavior
```

## Display

Test:

```text
smooth panadapter
waterfall stability
Retina scaling
window resizing
full screen
CPU/GPU usage
```

## VST3

Test:

```text
plugin discovery
plugin loading
plugin unloading
audio processing
parameters
presets/state restoration
plugin GUI
multiple plugins
rack order
plugin crash isolation
```

---

# Required Development Order

Use this order unless an actual dependency makes it impossible:

```text
1. Checkout SDR-VST3 commit 275f768
2. Add ChasingCoffee/Thetis-VST as reference remote
3. Audit VST divergence
4. Audit Windows/platform dependencies
5. Establish cross-platform Core boundaries
6. Create MacTest console application
7. Build WDSP for macOS arm64
8. Build ChannelMaster for macOS arm64
9. ANAN discovery
10. ANAN connection
11. RX1
12. Spectrum data
13. Basic RX audio
14. Avalonia application shell
15. Skia panadapter
16. Waterfall
17. Essential radio controls
18. Settings
19. VST3 DSP hosting
20. VST3 state/presets
21. VST3 editor windows
22. Remaining UI
23. TX validation
24. Packaging
25. Signing/notarization
```

---

# First Concrete CLI Assignment

Do **not** tell the CLI to "port SDR-VST3 to Mac."

Give it this bounded assignment first:

> We are creating a macOS Apple Silicon port of SDR-VST3. The working base is SDR-VST3 commit `275f768`, the commit where the .NET 10 migration was merged. The original VST-enabled Thetis repository is available as the `chasingcoffee` remote and should be treated only as a behavioral/reference implementation.
>
> First, do not modify source code.
>
> 1. Compare this codebase with `chasingcoffee/vst-support` and produce `VST_DIVERGENCE_AUDIT.md`, identifying useful VST3 functionality or fixes that are missing or changed.
> 2. Audit the current SDR-VST3 tree for macOS portability and produce `MACOS_PORTABILITY_AUDIT.md`.
> 3. Categorize code as platform-neutral, WinForms-specific, DirectX/Vortice-specific, Win32/PInvoke-specific, native DSP, audio, VST3, or HPSDR/networking.
> 4. Identify the minimum set of files/classes needed to create a headless `SDR-VST3.MacTest` .NET 10 console application capable of ANAN discovery and RX.
> 5. Propose the smallest safe set of changes required to reach that test harness.
>
> Do not refactor the UI, do not replace the renderer, do not implement Avalonia, and do not rewrite native DSP yet. Stop after producing the two audit documents and the proposed first implementation step.

Review those audit documents before allowing the CLI to make architectural changes.

---

# First Implementation Milestone

After reviewing the audits, the first code milestone is:

```text
macOS arm64
    ↓
.NET 10 console app
    ↓
ANAN discovery
    ↓
ANAN connection
    ↓
ChannelMaster
    ↓
WDSP
    ↓
RX1 spectrum samples
    ↓
console output
```

Definition of done:

```text
The Mac discovers the ANAN, connects to it, starts RX1,
and continuously receives valid spectrum data without
WinForms, DirectX, or Windows DLLs.
```

If this succeeds, the macOS port is technically viable.

---

# Second Implementation Milestone

Create a minimal Avalonia application:

```text
Window
 └── Spectrum Control
```

Feed the live spectrum data into a SkiaSharp renderer.

Definition of done:

```text
A native macOS window displays a live ANAN panadapter.
```

Do not add the complete Thetis control surface yet.

---

# Third Implementation Milestone

Add RX audio through the macOS audio backend.

Definition of done:

```text
ANAN RX audio can be heard from the selected Mac output device
while the live panadapter and waterfall are running.
```

At this point the three critical systems are proven:

```text
radio networking
DSP
native macOS display/audio
```

Only after these three milestones should broader UI migration and macOS VST3 support begin.

---

# Important Constraints

Throughout the port:

- Start from SDR-VST3 commit `275f768`.
- Keep ChasingCoffee/Thetis-VST as a reference, not the primary source tree.
- Preserve DSP behavior wherever possible.
- Preserve ANAN/HPSDR protocol behavior wherever possible.
- Do not rewrite radio protocol code unless necessary.
- Do not rewrite WDSP in C#.
- Do not port DirectX APIs one call at a time.
- Do not attempt the entire GUI before proving headless RX.
- Do not implement VST editor windows before VST DSP processing works.
- Do not introduce unnecessary abstractions.
- Do not convert everything to async.
- Keep commits small and buildable.
- Keep Windows behavior working where practical.
- Every phase should end with something runnable or independently testable.

The immediate objective is **not**:

```text
Compile SDR-VST3 for Mac
```

The immediate objective is:

> Get the existing SDR engine running headless on Apple Silicon and receiving valid spectrum data from an ANAN.

Solve that first. Everything else should build outward from that working core.
