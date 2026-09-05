# Cross-platform portability audit

Date: 2026-09-04. Retains the planned filename; scope is a shared Windows/macOS application, with Linux accommodated in architecture and builds.

## Scope and recommendation

Primary source inspected: SDR-VST3 `275f7683291413d7499136ec5105421e39184803`. Later changes inspected through `80538e3884aff03f7e013ce7e7bae9723700cc65`. ChasingCoffee `cc62ad73d99131ea005762adef947fb0bebba8a0` supplied VST behavioral comparison.

Keep C#/.NET, preserve native DSP, and build the eventual shared desktop interface with the agreed Avalonia direction. The immediate engineering obstacle is native OS coupling and application initialization through forms. Changing the .NET target alone will not produce a portable engine.

Split the first milestone into **discovery without native libraries**, followed by **native receive and spectrum output**. Discovery already has a useful extraction boundary. RX requires native threading/socket/build work and explicit lifecycle orchestration; it is not a small console wrapper around the existing application.

This is a static audit. No source refactoring, radio traffic, installation, plugin loading or transmit testing was performed.

## Platform inventory

Paths in this section are relative to `Project Files/Source/`.

| Category | Status | Evidence and minimum action |
| --- | --- | --- |
| .NET projects | Windows-specific | `Console/Thetis.csproj` targets `net10.0-windows`, x64 and WinForms. Midi2Cat, RawInput, scanner and tests also target Windows. Introduce separate `net10.0` projects; do not retarget the entire solution blindly. |
| Platform-independent C# | Cross-platform candidates | `Console/HPSDR/clsRadioDiscovery.cs` uses BCL networking/diagnostics and the `HPSDRHW` enum. `enums.cs`, `Channel.cs`, and selected state/filter/catalog models are reusable candidates. “Candidate” is not a successful compilation claim. |
| Windows UI / WinForms | Windows-specific | `console.cs`, `setup.cs`, their designers, `Invoke/`, dialogs, rack UI and meter hosts. The main form also owns engine startup, frequency changes, settings and callbacks. Extract behavior incrementally into the shared engine. |
| DirectX / Vortice | Windows-specific | `display.cs`, `Display.*Mesh.cs`, `Display.SpectrumCompute.cs`, `MeterManager.cs`; D2D/D3D11 and HLSL dependencies. Separate spectrum frames/calibration/display state from rendering. The existing WARP or line-render fallback still depends on DirectX. |
| Win32/PInvoke | Windows-specific, plus portable native interfaces to redesign | `win32.cs`, `RawInput/Win32.cs`, `Midi2Cat.IO/WinMM.cs`, `dsp.cs`, `cmaster.cs`, `HPSDR/NetworkIOImports.cs`, `portaudio.cs`, VST bridge imports. Audit used APIs and ABI types; do not count every historical `.cs` file as compiled. |
| WDSP | Native code requiring macOS/Linux build | `wdsp/comm.h` includes Windows, process, intrinsics and avrt headers. Locks, semaphores, atomics, allocation and scheduling require adaptation. DSP algorithms should remain native and behaviorally comparable. |
| ChannelMaster | Native code requiring macOS/Linux build | `cmcomm.h`, `cmaster.c`, `cmsetup.c`, `pipe.c`, buffers, mixer, router and networking. It binds radio streams to WDSP and audio; it is substantially more than a library-loading shim. |
| HPSDR / ANAN networking | Mixed | Managed discovery is a strong reusable candidate. `NetworkIO.InitRadio()` reads the main form and selected radio UI. Streaming protocols are implemented in native `network.c`, `networkproto1.c`, `netInterface.c`, `router.c`; Winsock and Windows worker primitives need platform adaptation. |
| Local audio / VAC | Mixed, native build required | `ChannelMaster/ivac.c` uses PortAudio; the repository includes PortAudio source. `cmasio.c` and cmASIO are Windows-specific. NAudio/WASAPI/MME and newer `AudioStreamOut.cs` require platform implementations. A no-local-audio headless mode must still satisfy native initialization. |
| VST3 | Windows-specific host around reusable behavior | Native host uses Win32 process/IPC/window APIs and SDK `module_win32.cpp`; managed wrapper assumes `.dll`, `.exe`, Windows paths. See [VST audit](VST_DIVERGENCE_AUDIT.md). |
| Configuration/filesystem | Mixed | Some code uses `Path.Combine`, while `console.cs` and other modules concatenate Windows separators and derive runtime state through forms. Pass explicit writable data paths; separate shared settings from device IDs and platform-specific plugin locations. |
| CAT / MIDI / peripheral input | Mixed | CAT parsing/commands contain reusable logic but depend on `Console` state; serial/PTT hardware and RawInput/WinMM are separate platform concerns. These are outside the first RX milestone. |
| FreeDV/RADE and reporter (newer tip only) | Native rebuild and UI separation required | Adds RADE V1/V2, resampling, neural speech processing, microphone DSP and reporter UI. Several vendored implementations are absent; reporter manager uses WinForms, form events and Windows logging. Details in [baseline review](UPSTREAM_BASELINE_REVIEW.md). |
| Build and packaging | Windows-specific | `.vcxproj`/MSVC v145, Windows build events, Windows libraries and WiX. Release workflow builds Windows on tag pushes; it is not a cross-platform regression suite. |

Primary evidence: [managed project](../Project%20Files/Source/Console/Thetis.csproj), [solution](../Project%20Files/Source/Thetis_VS2026.sln), [WDSP build](../Project%20Files/Source/wdsp/wdsp.vcxproj), [ChannelMaster build](../Project%20Files/Source/ChannelMaster/ChannelMaster.vcxproj).

## Concrete blockers and risks

### PORT-1: Radio initialization has an existing native signature mismatch

**High confidence from source, before any port changes.** `HPSDR/NetworkIOImports.cs:16` declares `nativeInitMetis` with six arguments. `ChannelMaster/network.c:84` implements it with seven. The seventh, `p2hw_uses_differnt_ports`, is actually read to choose Protocol 2's port base (`:106`). The managed caller does not supply it.

This is an ABI defect in all three audited source endpoints, not a FreeDV regression. Standard discovery port 1024 can mask the behavior because `port + 1` and 1025 coincide. Explicit custom-port testing is needed. Correct the signature and expose the intended option before reusing this call in the harness; do not infer a runtime crash from source alone. Sources: [managed declaration](../Project%20Files/Source/Console/HPSDR/NetworkIOImports.cs), [native implementation](../Project%20Files/Source/ChannelMaster/network.c).

### PORT-2: Discovery success does not establish streaming portability

`clsRadioDiscovery.cs` builds P1/P2 discovery packets and uses .NET sockets/interfaces. `NetworkIO.InitRadio()` obtains NIC, radio, model and protocol choices from `Console.getConsole().SetupForm.SelectedRadioList`. `Audio.Start()` additionally changes cursor state, shows dialogs and calls setup methods before `StartAudioNative()`.

Extract an explicit connection-options object and error result, retaining packet formats and radio identification. Adapt native socket operations at the transport boundary, including socket handles, errors, close semantics and timeout representation. Do not rewrite the packet-processing algorithm merely to change socket APIs. Sources: [discovery](../Project%20Files/Source/Console/HPSDR/clsRadioDiscovery.cs), [connection](../Project%20Files/Source/Console/HPSDR/NetworkIO.cs), [audio startup](../Project%20Files/Source/Console/audio.cs).

### PORT-3: Native initialization assumes more than an RX engine

`CMCreateCMaster()` configures eight input streams, five receivers, two subreceivers per receiver, one transmitter, callbacks and rates before `CreateRadio()` and `CreateRNet()`. `CreateRadio()` creates the master, pipe and synchronization infrastructure. `create_cmaster()` calls `create_cmasio()`; `create_pipe()` invokes scope/wave creation callbacks unconditionally and creates VAC/TCI objects even when their run flags are off.

A headless harness must provide required callback lifetimes and a defined no-device audio path. Leaving callback pointers null is insufficient. Keep the known topology initially, even though only RX1 is exercised; reducing resource counts requires checking native indexing assumptions. Sources: [managed setup](../Project%20Files/Source/Console/cmaster.cs), [native lifecycle](../Project%20Files/Source/ChannelMaster/cmsetup.c), [pipe construction](../Project%20Files/Source/ChannelMaster/pipe.c), [cmASIO](../Project%20Files/Source/ChannelMaster/cmasio.c).

### PORT-4: Rebuilding WDSP requires dependencies as well as a build file

Both native projects depend on Windows locks/semaphores, interlocked operations, `_beginthreadex`, aligned allocation and scheduling APIs. Port their semantics and shutdown behavior, not only their names. Preserve 32-bit atomic/control fields where the Windows code assumes them; audit C `long`, pointers, structs, exported booleans and strings across ABIs.

WDSP links FFTW, RNNoise and libspecbleach. FFTW is supplied here as Windows binaries/headers. RNNoise and libspecbleach implementation sources and build notes exist under `Project Files/lib/NR_Algorithms_x64/src/`; use that starting point and verify their ARM paths. The included DLLs and x86/AVX variants are not Apple Silicon libraries. Turning a UI noise-reduction option off does not remove a native link dependency.

ChannelMaster additionally links PortAudio, cmASIO and avrt. Preserve Windows cmASIO behavior while introducing a no-ASIO path for macOS/Linux. Sources: [WDSP common header](../Project%20Files/Source/wdsp/comm.h), [ChannelMaster common header](../Project%20Files/Source/ChannelMaster/cmcomm.h), [native build dependencies](../Project%20Files/Source/wdsp/wdsp.vcxproj), [NR source/build notes](../Project%20Files/lib/NR_Algorithms_x64/src/HowTo/how.txt).

### PORT-5: Spectrum extraction must avoid inheriting display dependencies

`HPSDR/specHPSDR.cs` mixes analyzer settings with `Display` globals. `clsSpectrumProcessor.cs` also includes display calibration and a WinForms test form. Extract analyzer configuration and frame delivery rather than pulling these whole files into Core.

There is an existing native additional-analyzer API: `alloc_analyzer`, `run_analyzer`, `free_analyzer` in `ChannelMaster/analyzers.c`, fed by the standard receiver loop in `cmaster.c`. It offers a candidate spectrum-only endpoint. Validate allocation IDs, buffer sizing, analyzer configuration and ownership before choosing it over the existing RX1 display analyzer. `GetPixels` includes a `pixel_ref` output in this WDSP tree; the managed wrapper already accommodates it. Sources: [analyzer allocation](../Project%20Files/Source/ChannelMaster/analyzers.c), [spectrum wrapper](../Project%20Files/Source/Console/HPSDR/specHPSDR.cs), [native analyzer API](../Project%20Files/Source/wdsp/analyzer.h).

### PORT-6: Newer FreeDV is not optional at library initialization

At `80538e38`, `create_pipe()` unconditionally calls `create_radae()`, and the ChannelMaster project unconditionally links its added libraries. Disabled RX/TX checkboxes return early in processing, but do not remove construction/linking requirements. A future build/runtime feature boundary must cover initialization, symbols and dependencies. See [FreeDV findings](UPSTREAM_BASELINE_REVIEW.md).

## Minimum files and boundaries for the harness

These are proposed slices, not files already extracted or compile-proven.

| Slice | Existing source to reuse or extract from | New boundary needed |
| --- | --- | --- |
| Discovery-only executable | `Console/HPSDR/clsRadioDiscovery.cs`; `Console/enums.cs` (or its `HPSDRHW` enum) | `Thetis.Core` targeting `net10.0`, plus `Thetis.Headless` targeting `net10.0`; explicit CLI options and structured results. Link existing files initially to avoid duplication. |
| Connection options | Discovery `RadioInfo`/`NicRadioScanResult`, `NetworkIO.InitRadio`, required hardware/model definitions | Selected NIC/IP, model, P1/P2, discovery/listen ports and P2 custom-port behavior, supplied without a form. |
| Native interop | Relevant declarations from `dsp.cs`, `cmaster.cs`, `NetworkIOImports.cs`, analyzer portion of `specHPSDR.cs` | Shared ABI definitions and per-platform library resolution. Cover initialization, rates, tuning, mode/filter, channel run state, frames and shutdown. |
| Lifecycle and callbacks | `RadioDSP.CreateDSP`, `CMCreateCMaster`, `SendCallbacks`, native `cmsetup.c`/`pipe.c` | Explicit session ownership, rooted delegates, required scope/wave callbacks, cancellation/cleanup and no-device audio behavior. |
| Radio configuration | Required parts of `CMLoadRouterAll`, `NetworkIO`, `RadioDSPRX` and native router/control functions | Preserve model/protocol-specific routing and initialization sequence; do not import all of `console.cs`, `audio.cs`, `radio.cs`, or setup forms. |
| Spectrum | `specHPSDR.cs`, `analyzers.c`, `wdsp/analyzer.*` | Analyzer configuration, a bounded reusable pixel buffer and frame metadata independent of the renderer. |
| Native execution | WDSP and ChannelMaster compile lists and their transitive libraries | CMake builds plus narrow OS adaptations, beginning with arm64 macOS and retaining Windows builds. |

Full RX therefore needs selected logic from several large files plus native changes. There is no already-separated engine project that the harness can simply reference.

## Proposed first implementation step

1. Create `src/Thetis.Core` and `src/Thetis.Headless` as platform-neutral .NET 10 projects alongside the existing Windows solution.
2. Link `clsRadioDiscovery.cs` and its enum dependency. Add CLI options for NIC, P1/P2, discovery port and timeout/profile; print discovered model, protocol, firmware, address and selected interface.
3. Provide finite completion with an explicit no-radio result. Validate saved P1/P2 discovery responses and malformed packets with focused tests, then perform a hardware discovery smoke test on Windows and macOS. Do not claim RX capability at this stage.
4. Establish .NET 10 build checks for the new projects on Windows/macOS/Linux; keep a separate Windows application build check. No need to replace or reorganize the existing solution first.

Acceptance: the same harness builds without WinForms, Vortice or native radio libraries, and correctly discovers the user's **ANAN G2 headless over Protocol 2** on Windows and Apple Silicon. Record the installed firmware/server versions and discovered capabilities with the result; do not hard-code a generic P2 board's routing assumptions.

Next, correct PORT-1 and establish native dependency builds/OS primitives. Bring up WDSP with synthetic samples before live RX. Then extract the session sequence, obtain radio packets, configure RX1 and report advancing finite spectrum frames, rates and sequence/error counters. Include orderly stop/reconnect and retain TX disabled for this receive milestone. Calibrated dBm and full radio correctness require additional reference measurements.

## Hardware and simulator coverage

The user has an ANAN G2 headless (P2) and no longer has the Hermes Lite 2. Make the G2 the first live-radio target; buying replacement P1 hardware is not a prerequisite for initial development.

**First simulator candidate: piHPSDR's `hpsdrsim`.** The inspected [g0orx implementation](https://github.com/g0orx/pihpsdr/blob/master/hpsdrsim.c) explicitly supports `-P1`, `-P2`, and `-hermeslite2`, plus other board profiles. It generates synthetic I/Q tones/noise and handles radio control traffic. The [Makefile](https://github.com/g0orx/pihpsdr/blob/master/Makefile) includes a separate `hpsdrsim` target; source contains macOS compatibility handling. After building that version, the P1/HL2 invocation is:

```sh
./hpsdrsim -P1 -hermeslite2
```

This is source-verified syntax, not a simulator build/run result on this machine. Pin the selected simulator revision and establish a known-good session with the existing Windows application before treating its output as a regression fixture.

**Alternative: [kosciej/hpsdr-emu](https://github.com/kosciej/hpsdr-emu).** Its Python implementation documents P1/P2, while the Rust implementation is P1-only. It supports synthetic signals and P1 TX echo. Its README explicitly reports nonfunctional P2 TX processing and incorrect P1 power/SWR metering, so it is not an acceptance oracle for those features.

Use three complementary layers: saved packet fixtures for parsing/error tests; a P1 simulator for discovery, control, stream framing and synthetic spectrum checks; and the real G2 for P2 integration and sustained receive/reconnect tests. Simulator success cannot establish physical RF behavior, calibration, firmware quirks, or reliable TX/PureSignal operation on actual P1 hardware. Obtain a real P1 test session, borrowed hardware or an external tester before advertising full P1 support.

## Development environment and verification limits

- Checked all three source endpoints using the baseline checkout and temporary sparse checkouts; no tracked application/native source was edited.
- `dotnet` is not on PATH here. CMake and Clang are present. No SDK installation was performed, and no .NET build/test success is claimed.
- The Windows solution depends on MSVC/Windows libraries; it cannot serve as a Mac viability test unchanged.
- Existing tests target Windows, use a prebuilt application DLL and a legacy package hint path. The newer release workflow is Windows/tag oriented and does not run a test command. No radio/plugin/FreeDV execution was performed.
- Old handoff documents are historical notes, not verified architecture. In particular, this application hosts VST effects; the source inspected is not evidence that the whole radio application is itself a DAW-loadable VST3 plugin.

The [upstream baseline review](UPSTREAM_BASELINE_REVIEW.md) recommends how to handle the 18 newer commits and preserve FreeDV as a planned feature.
