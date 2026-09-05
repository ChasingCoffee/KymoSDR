# Thetis cross-platform implementation plan

Date: 2026-09-04. Status: discovery and initial macOS native DSP implemented; cross-platform qualification and desktop work pending.

## Outcome and scope

Build one C#/.NET 10 application with a shared Avalonia interface for Windows and macOS, eventually replacing the existing Windows interface. Preserve the native WDSP/ChannelMaster engine and HPSDR protocol behavior through incremental extraction and platform adaptation. Keep Linux in the build and test strategy from the beginning.

This is the implementation successor to the [original macOS plan](SDR-VST3_macOS_Port_Plan.md), which remains unchanged as historical context. Where they differ, this plan proposes the updated direction: one shared Windows/Mac UI, discovery before native bring-up, the reviewed pre-FreeDV baseline, and G2/P2 hardware plus P1 simulation.

Planning inputs:

- [Portability audit](MACOS_PORTABILITY_AUDIT.md): source boundaries, native dependencies, radio-init ABI defect and simulator options.
- [VST divergence audit](VST_DIVERGENCE_AUDIT.md): behavior to preserve and catalog/lifecycle defects to correct.
- [Upstream/FreeDV review](UPSTREAM_BASELINE_REVIEW.md): assessment of the 18 newer commits and dependency/TX-routing findings.
- [WDSP baseline review](WDSP_BASELINE_REVIEW.md): authoritative TAPR 2.00 reference, local extensions and the retained older PureSignal implementation.

The audits are static evidence, not proof of successful builds or operation. Milestone acceptance below requires new test results.

### Initial support targets

| Target | Commitment during development | Release qualification |
| --- | --- | --- |
| Windows x64 | Build and test the new engine and shared desktop app; retain the legacy application as a behavioral reference. | Primary release target. The new Windows app must pass the same feature gates as the Mac app. |
| macOS Apple Silicon | Native arm64 dependencies, headless engine, audio, shared UI and arm64 VST3 hosting. | Primary release target. No dependency on Windows DLLs or Rosetta. |
| Linux x64 | Managed CI immediately; native CI as libraries are adapted; simulator and desktop smoke checks as those features arrive. | Experimental until audio, desktop, packaging and hardware tests pass on a named distribution. |
| Intel Mac, Windows ARM64, Linux ARM64 | Avoid unnecessary architectural barriers, but do not add these to the initial delivery matrix. | Later qualification if requested and test hardware is available. |

The available live radio is the user's **ANAN G2 headless, Protocol 2**. P1 development uses a pinned `hpsdrsim` version with its Hermes Lite 2 profile. Simulator coverage is not a claim of full P1 hardware support.

Exact minimum OS versions, Linux distribution and package versions will be pinned during implementation. Avalonia documents Windows, macOS and Linux desktop support, with version-specific support tiers; our support promise must also account for .NET, native dependencies and our own tests. [Avalonia platform documentation](https://docs.avaloniaui.net/docs/supported-platforms).

### What the first usable release is not

The first usable checkpoint is a receive-only desktop preview, not full Thetis parity. Full TX, VST3, FreeDV, CAT/MIDI, VAC/TCI, advanced calibration and multi-receiver workflows have explicit later gates. No Qt rewrite, managed DSP rewrite, VST2 host, whole-radio DAW plugin, or simultaneous GUI redesign of every existing dialog is included.

## Architecture and migration rules

### Shared application, replaceable platform implementations

The desktop and headless tools must use the same engine; neither may start a hidden WinForms console to obtain radio functionality. Keep the legacy Windows application buildable, but do not require migrating it to the shared engine before proving the new application.

Proposed layout, created only as needed:

```text
src/
  Thetis.Core/          # net10.0: discovery, options, state, commands, frame contracts
  Thetis.Engine/        # native bindings, RadioSession lifecycle, audio integration
  Thetis.Headless/      # CLI using Core; adds Engine when native RX is ready
  Thetis.Desktop/       # shared Avalonia UI, view models and spectrum rendering
  Thetis.Vst/           # shared chain/catalog/state models and host coordination
native/
  CMakeLists.txt        # builds existing source in place, not a second DSP copy
  platform/            # narrow Windows/POSIX adaptations as needed
tests/
  Thetis.Core.Tests/
  Thetis.Engine.Tests/
  Thetis.Desktop.Tests/
  fixtures/            # versioned, small protocol/DSP/state fixtures
docs/
  decisions/           # short records for choices with lasting consequences
```

Existing implementation remains under `Project Files/Source/` initially. Add CMake build definitions around it rather than moving thousands of files. Link reusable managed files first; extract shared ownership only when behavior or testability requires it. Avoid maintaining two independently edited copies of the same implementation. New tests reference new projects, not a previously built legacy `Thetis.dll`.

### Boundaries to enforce

1. **Discovery and control:** `Thetis.Core` has no WinForms, Avalonia, Vortice or native-library load requirement. Explicit connection options carry NIC, radio address, protocol, model/capabilities and ports. UI and CLI submit the same commands; radio state is not stored in controls.
2. **Native engine:** `Thetis.Engine` owns native handles, buffers, callbacks and startup/shutdown. Preserve native P1/P2 streaming code; adapt sockets and synchronization where it lives. Managed discovery does not imply moving streaming into C#.
3. **Interop:** specify field widths, struct layout, calling conventions, ownership and string encoding. Correct the six-versus-seven-argument `nativeInitMetis` mismatch before use. Use logical library names and an explicit resolver where necessary, with diagnostic errors for missing/wrong-architecture dependencies. [.NET native loading documentation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading).
4. **Real-time processing:** keep UI work, disk/network logging, plugin scanning and configuration serialization off audio/radio callbacks. Use bounded, preallocated handoffs with explicit ownership. Slow rendering may skip old display frames; it must not stall radio processing. Measure allocations, waits and overruns rather than assuming callbacks are real-time safe.
5. **Session lifecycle:** implement explicit disconnected, starting, receiving, stopping and faulted states; add TX states only with the TX milestone. Define cleanup for every partially completed startup step. Serialize control transitions without holding UI or callback locks across them. Keep delegates rooted until native workers stop.
6. **Platform services:** isolate audio devices, scheduling, native loading, plugin processes/windows and OS paths. Do not introduce an empty catch-all platform abstraction. Preserve Windows ASIO behavior where supported; a Mac audio backend need not emulate ASIO concepts.
7. **Settings and diagnostics:** use separate application data from the legacy app, schema-versioned settings, atomic saves and explicit import/export. Do not overwrite legacy profiles or calibration data. Include source/dependency versions, device identity, counters and errors in diagnostics from the first harness. Reporting to external services is opt-in.

Initially retain ChannelMaster's known stream/receiver topology even when exercising only RX1. A receive-only user experience does not mean removing all native TX/VAC/TCI objects or passing null callbacks into constructors that expect them.

## Delivery sequence

Each milestone ends in a runnable artifact or independently executable tests. A build on one OS is not evidence for another, and a skipped hardware test is recorded as unverified rather than passed.

| Milestone | Observable result | Dependency |
| --- | --- | --- |
| M0 — Baseline and toolchains | Pinned source/toolchain manifests and reproducible reference build procedure. | Existing audits |
| M1 — Discovery harness | Same CLI discovers G2/P2 and the P1 simulator without native DSP. | M0 |
| M2 — Portable WDSP | Native DSP processes synthetic fixtures through shared interop. | M0; integrate with M1 harness |
| M3 — Portable ChannelMaster | Headless engine initializes and tears down repeatedly without a UI/audio device. | M2 |
| M4 — Live headless RX | G2/P2 produces advancing spectrum frames; P1 simulation exercises its streaming path. | M1, M3 |
| M5 — Receive desktop preview | Shared Windows/Mac app displays spectrum/waterfall and plays RX audio. | M4 |
| M6 — Basic TX | Explicitly enabled, controlled TX and return-to-RX work on G2. | M5 |
| M7 — VST3 processing | Reliable out-of-process chains, parameters and state without plugin editors. | M5; M6 for live TX integration |
| M8 — VST3 editors | Platform-native plugin windows work with the shared rack UI. | M7 |
| M9 — FreeDV/RADE | Rebuildable optional modem with validated RX/TX processing order. | M6, M7 for integrated TX gate |
| M10 — Compatibility and feature coverage | Prioritized radio/DSP/peripheral workflows and non-destructive settings migration. | M5 onward, with TX/VST prerequisites as applicable |
| M11 — Release qualification | Reproducible, tested Windows/Mac packages and an explicit Linux status. | Required feature gates above |

The default order prioritizes proving the engine before expanding UI work. FreeDV dependency recovery can be prepared separately after M0, but it must not introduce compulsory dependencies into M1–M5. Compatibility work can proceed in small slices after the RX preview; it need not wait for every plugin editor.

### M0 — Establish the implementation baseline

- Record the current branch at `275f7683291413d7499136ec5105421e39184803`, then advance the implementation baseline to reviewed `3518930bc12b976457130d681a5a80b569232e31` in a dedicated source-history step, preserving local documents and unrelated changes. Do not include extraction changes in that step.
- Retain `upstream/master` at reviewed `80538e38` as the later-feature reference and ChasingCoffee `cc62ad73` as behavioral reference. Do not merge ChasingCoffee wholesale or automatically follow future upstream commits.
- Verify/install the .NET 10 SDK through the normal approval workflow; it was absent from PATH at audit time. Record SDK, CMake, compilers and native dependency revisions. Pin the SDK without inadvertently breaking the legacy build.
- Establish a clean Windows reference build and smoke-test procedure. Record existing failures before port changes; do not silently treat a prebuilt binary as a successful source build. Add repeatable build scripts and CI definitions without publishing releases.
- Begin a dependency manifest with source revisions, patches, model/asset hashes, build options and redistribution notices. Define per-platform artifact output directories and ignores.

**Exit:** source version and local changes are accounted for, toolchain prerequisites are reproducible, and Windows reference build status is explicit. Offline/shared scaffolding can proceed if access to a Windows test machine is pending, but cross-platform validation cannot be marked complete.

### M1 — Discovery-only executable and fixtures

- Create only `Thetis.Core`, `Thetis.Headless` and `Thetis.Core.Tests` initially, targeting `net10.0` in a separate solution.
- Start from `Console/HPSDR/clsRadioDiscovery.cs` and the needed enum in `Console/enums.cs`. Reuse its existing discovery options/service. Extract small parser or socket seams only where tests, bounded cancellation or portability need them.
- Provide NIC listing/selection, P1/P2/both selection, port and bounded timeout options, plus readable and JSON results. Distinguish successful discovery, no radio, invalid input and socket errors through documented exit codes. Ctrl-C must release sockets promptly.
- Test valid P1/P2 responses, duplicates, malformed/truncated packets, timeouts and interface selection offline. Keep captured fixture provenance; use small sanitized payloads, not uncontrolled live network captures in CI.
- Pin/build `hpsdrsim` as external test tooling. Establish its behavior against the Windows reference before using it as an oracle. Record whether it runs on the same machine or another LAN host and verify that topology's broadcast/NIC behavior.
- Discover the real G2 from Windows and macOS and record firmware/server versions and returned capabilities. Run managed build/tests on Windows, macOS and Linux; live radio tests are opt-in, not required by ordinary CI.

**Exit:** one platform-neutral CLI completes finite scans without loading WDSP/ChannelMaster and reports the expected G2/P2 and simulator/P1 identity. Hardware results are documented separately from fixture results.

**Not included:** radio streaming, native ABI changes, audio, Avalonia or transmission.

### M2 — Native dependency builds and WDSP

- Build FFTW and the required RNNoise/libspecbleach sources for the target architectures; verify ARM-specific code paths. Use the sources already present where complete, recovering/pinning missing source inputs rather than linking Windows artifacts on Mac.
- Add WDSP CMake targets and narrow portability adaptations for locks, atomics, allocation, threads and scheduling. Check existing cross-platform WDSP implementations as references before inventing equivalents, but compare versions/APIs before importing code.
- Use pinned TAPR/OpenHPSDR-wdsp 2.00 as the authoritative reference, while building this project's modified source in place. Match local ABI extensions (including five-argument `GetPixels`); do not conflate its `200` version value with stock TAPR feature parity. Adopting full TAPR PureSignal 3.0 is a separate integration decision, not part of M2 portability.
- Introduce `Thetis.Engine` interop and a small native ABI test target. Check exported signatures, representative struct sizes, buffer ownership, and library resolution on each OS. Keep dependency loading out of discovery-only execution.
- Exercise known tones, impulse/noise fixtures, filtering, rates and analyzer output. Define numerical tolerances and fixture metadata before comparing platforms; do not require bit-identical floating-point output or call uncalibrated values dBm.

**Exit:** Windows x64 and macOS arm64 builds load from their staged artifact directories and pass deterministic DSP checks. Add Linux native builds/tests here and record any remaining POSIX differences explicitly. Inspect transitive libraries to catch dependencies on a developer's machine.

### M3 — ChannelMaster lifecycle without forms

Implementation is split into **M3a: offline DSP/pipe lifecycle** and **M3b: RNet/
socket lifecycle** so native transport is not accidentally qualified by a
no-packet test. See [the M3a boundary and tests](CHANNELMASTER_OFFLINE.md).

- Correct PORT-1's radio-init ABI on both managed and native sides, covering default and custom P2 port selection with focused tests before invoking it in the harness.
- Adapt ChannelMaster's native sockets, workers, synchronization and allocation; retain packet-processing/routing algorithms. Define a non-Windows no-ASIO path and explicit no-device audio behavior.
- Extract the needed setup sequence from `cmaster.cs`, `radio.cs`, `NetworkIO.cs` and `audio.cs` into the session owner. Replace form reads with validated options and explicit errors.
- Supply rooted scope/wave callbacks or a tested headless lifecycle boundary. Account for VAC/TCI construction and every required callback even when processing is disabled. Native FreeDV remains absent from this baseline.
- Test library initialization, known topology/rates, repeated open/close, cancellation, missing dependencies and forced failure at intermediate startup stages. Verify all workers stop before callbacks and buffers are freed.

**Exit:** a no-radio/no-audio-device harness can initialize and dispose the engine at least 100 times without hangs, crashes or continuing resource growth after warm-up. No WinForms startup or Windows DLL loading occurs on macOS. Do not interpret this as live RX proof.

### M4 — G2/P2 receive and P1 simulated receive

- Add bounded `receive` operation to the CLI: select discovered radio/interface, start RX1 at one confirmed supported rate, tune, set mode/filter, read spectrum and stop.
- Preserve G2-specific capability, routing and port handling. Do not treat all P2 boards as interchangeable. Compare control sequences and observable receive behavior with the Windows reference.
- Choose the existing RX1 analyzer or additional-analyzer API after validating IDs, configuration and ownership. Expose renderer-independent frames with sequence, time, frequency/rate, scaling and calibration status.
- Instrument packet sequences/loss, spectrum cadence, audio-buffer flow, CPU, allocation rate and memory. Test rate/frequency/filter changes, missing radio, disconnect/reconnect and clean cancellation. Use fixtures/fault injection for malformed, reordered and dropped traffic where practical.
- Run the same session path against P1 `hpsdrsim`, including start/stop and control changes. Keep automatic TX, MOX/PTT and transmit commands unavailable.

**Exit:** headless Windows and Mac sessions each receive from the G2 for at least 30 minutes, produce finite advancing spectrum data and complete ten start/stop/reconnect cycles. Synthetic P1 spectra match the fixture expectations. Publish measured counters and distinguish network loss from application overruns. Linux runs native/simulator smoke checks as available.

This is the first major engine-viability gate. If it fails, resolve transport, DSP or lifecycle issues before beginning the desktop port.

### M5 — Shared receive-only desktop preview

Deliver two sub-gates, using the proven engine:

1. **Audio:** reuse PortAudio initially where it fits the native integration, with macOS CoreAudio and a tested Windows backend. Enumerate/select outputs, handle rate conversion, expose underrun/overrun counters, and test device change/loss and mute/gain. Add Linux audio smoke coverage using a documented backend. PortAudio supplies a cross-platform audio API; backend behavior still needs our tests. [PortAudio documentation](https://www.portaudio.com/docs/v19-doxydocs/index.html).
2. **Desktop:** create one Avalonia application for Windows/Mac, with a shared spectrum/waterfall control and receive controls: radio/interface selection, connect/disconnect, VFO, mode/filter, AF/RF gain, AGC, RX state and audio output. Keep display data independent of rendering; prototype Avalonia/Skia drawing and profile before selecting a custom GPU path. Do not retain DirectX as a requirement of the new Windows UI.

Add versioned preferences, keyboard/focus behavior, DPI/Retina resizing, useful errors and responsive shutdown. Build a simple unpacked preview artifact early to verify native-library staging outside the development tree. Do not expose controls whose behavior has not been implemented.

**Exit:** the same UI/engine runs on Windows and Mac with audible G2 receive, live spectrum/waterfall, working essential controls and a 60-minute session without application-induced audio underruns in the documented test setup. Target 30 display updates/second initially; record achieved cadence, frame drops and CPU/memory. Test resize, reconnect, device loss and sleep/wake. TX remains disabled. Linux gets a desktop-launch check without implying full support.

### M6 — Controlled basic TX

- Extend session state and command validation for explicit TX enable, MOX/PTT, source selection, drive limits and transition back to RX. Startup, reconnect and profile restoration must never automatically assert transmit.
- First verify microphone/tone input, resampling, modulation, routing and end-of-transmission using offline buffers or a suitable test peer. Define what happens on device loss, radio disconnect, cancellation and application shutdown; confirm radio watchdog/fail-safe behavior rather than assuming the host can recover from every failure.
- Perform live tests with the user present, at a suitable low-power dummy-load setup. Start with one documented voice mode; qualify CW/keying and digital-source paths separately. Record actual RF/monitor evidence, not just successful socket writes.
- Validate courtesy-tone timing and return-to-RX. Keep VST, RADE and advanced TX routing bypassed until their own integration tests pass.

**Exit:** Windows and Mac pass the same repeated PTT, audio-source, interruption and recovery scenarios on the G2, with correct observed TX/RX transitions. Publish which modes are qualified. P1 TX and PureSignal remain unqualified until real-hardware tests are available.

### M7 — VST3 processing and persistence

- Before extraction, add regression coverage and fix VST-1's multi-class cache collapse, VST-2's permanently signaled event and VST-3's unmatched runtime reference. Include cancellation of hung scans and consistent path-plus-class identity in catalog/removal/state operations.
- Preserve separate RX/TX chains and out-of-process audio hosting. Define versioned, fixed-width IPC with explicit string encoding, bounded audio handoff and a tested host failure/restart policy. Native Windows and macOS implementations supply process/IPC/module-loading details; Linux remains a separate qualification target.
- Use known architecture-matched test plugins to validate scan/load, audio, reorder, enable/bypass, sample-rate/block-size changes, parameter enumerate/get/set and state save/restore without editors. The general managed parameter API is new work, not an existing capability to assume.
- Preserve requested-but-unavailable plugins and resolve across OS installations by class ID, without assuming every plugin's state is portable. Test RX/TX/VAC/TCI source-selection semantics as each route becomes available. Keep optional streaming taps bounded; address UP-1 before reusing the new Windows streaming callback.

**Exit:** repeatable chain/state tests pass on Windows and Mac, including a bundle with multiple effects, 100 insert/remove cycles, scan cancellation and forced host failure. Idle CPU/resources recover after removal; audio fallback is deliberate and tested. No plugin editor is required for this gate.

### M8 — VST3 editors and shared rack UI

- Implement platform-native editor windows with the correct UI-thread and parent-view lifetime. Prototype window ownership across the existing process boundary before committing to an embedding design; detached host-owned windows are acceptable initially.
- Add shared rack ordering, bypass/enable state, failed-plugin rows, parameter controls and profile actions. Keep plugin-specific editors native; do not translate their controls into Avalonia.
- Test resize, DPI changes, focus/keyboard handling, close/reopen, multiple windows, plugin removal while open and host crash. Validate representative third-party plugins in addition to a test plugin.
- Prototype packaged-host signing/loading constraints on macOS now, including third-party plugins, rather than discovering them only at release time.

**Exit:** Windows/Mac editor and rack workflows pass the same lifecycle tests with bounded shutdown. Record per-plugin issues. Old standalone rack geometry is either explicitly migrated or reset with notice; audio chains/state must not be lost with layout migration.

### M9 — FreeDV/RADE as an optional feature

- Recover exact source/model inputs for `radae_c`, `opus_dnn`, `libebur128` and `WebRTC_AGC`; pin revisions/hashes and compare them to the wrapper headers. Build both Debug/Release and target architectures from source. The named Thetis-RADE fork is a recovery candidate, not proof of a matching binary/model version.
- Bring forward the three reviewed FreeDV-related commits in a controlled integration step. Establish a genuine build/runtime feature boundary: a disabled build must neither link missing modem libraries nor call RADE constructors; an enabled build must handle initialization failure cleanly.
- Validate decode from recorded reference signals first, then encode/decode loopback and live G2 receive. Qualify RADE V1/V2 specifically; do not label this as support for all historical FreeDV modes.
- Resolve FDV-3 before TX use: define speech processing before encoding or explicit bypass for incompatible post-encoder processors. Test the expander, TX VST, WDSP stages, PTT/end-of-over and courtesy tones with captured buffers. Avoid incidental callback reordering that changes VAC/TCI routing.
- Port reporter logic separately from forms and add shared UI only after modem behavior works. Require explicit opt-in for station/reporting data; test reconnect/error handling without sending live reports in CI.

**Exit:** enabled and disabled builds pass; Windows/Mac reference decode, loopback and controlled live TX tests succeed with documented chain policy and latency/CPU measurements. Missing modem assets produce a clear unavailable-feature result rather than breaking ordinary RX.

### M10 — Compatibility and replacement-readiness

Create a living feature matrix at M0 with states: not started, implemented, fixture-tested, simulator-tested, hardware-tested and deferred. Port cohesive workflows, not dialogs one at a time:

- RX2/multiple receivers, model-specific routing, diversity and band/VFO behavior.
- DSP controls and metering: AGC, filters, NB/NR/ANF, EQ, calibrated levels and profile state.
- Audio routes: VAC-equivalent paths, TCI, digital applications and optional stream capture. Keep platform device names separate from shared routing intent.
- CAT, serial/PTT, MIDI and peripheral controls using shared commands plus platform implementations.
- CW/keying, calibration, antenna/relay controls, protection indicators and PureSignal, each with suitable hardware acceptance tests.
- Non-destructive legacy settings/profile import, including calibration, missing plugin entries and the historical “Bypass” versus “Apply TX VST” setting semantics.

**Exit:** each release-required workflow has a reproducible Windows/Mac test and recorded result, or an explicit user-approved deferral. Back up and read legacy state; migration must be repeatable and must not modify the old installation. Obtain an external P1 tester or borrowed radio before claiming P1 hardware support. A G2 test cannot qualify every ANAN model.

Do not retire the old Windows interface merely because the new one opens: retirement requires agreement on the feature matrix and demonstrated migration/operating workflows.

### M11 — Packaging, reliability and release

- Build self-contained per-architecture application artifacts with pinned native dependencies and plugin host/scanner components. Verify native search paths, macOS install names/rpaths and Windows runtime prerequisites on clean machines without SDKs or development libraries.
- Complete Windows packaging and macOS `.app` signing/notarization. Validate audio-input permissions and plugin-host requirements with the actual packaged app. Signing identities/accounts are release prerequisites, not prerequisites for M1–M5 development.
- Run long-duration receive tests (initial target: eight hours), repeated connect/disconnect and device changes, sleep/wake, plugin failure/recovery and settings upgrades. Record CPU, memory, callback timing, packet loss and audio underruns against the documented reference environment.
- Ship dependency notices, build instructions, diagnostics/support instructions and an explicit supported OS/radio/feature matrix. Linux may ship as experimental only with an honest list of tested distribution/audio/display/plugin combinations.

**Exit:** clean-machine Windows/Mac install, RX, qualified TX/features, update and uninstall checks pass. Release artifacts are reproducible, recovery of prior settings is tested, and no deferred feature is described as supported.

## Verification policy

Every code change supplies an appropriate test or a reproducible manual procedure, with platform and fixture/hardware identity. Keep offline tests fast and independent of radio availability. Tests that require a radio, audio device or installed plugin are explicitly selected.

- **Every change:** new managed projects build/test on Windows/macOS/Linux; native tests join this matrix as their targets arrive. Preserve a separate legacy Windows build check. Validate API/ABI changes against every managed/native caller.
- **Protocol:** parser fixtures, simulator integration and G2 sessions are separate result categories. Record simulator revision/topology and radio firmware/server versions. For P1 tooling details and limitations, use the [audited simulator notes](MACOS_PORTABILITY_AUDIT.md#hardware-and-simulator-coverage).
- **DSP/audio:** maintain deterministic inputs and expected numerical tolerances. Store baseline spectra/buffer measurements, not subjective “sounds right” alone. Profile steady-state allocation/waits and expose underrun/sequence counters.
- **UI:** test the same commands/state transitions through CLI and desktop; resizing/rendering must not affect stream continuity. Verify actual packaged graphics behavior on both primary platforms.
- **Resource lifetime:** repeated startup/failure/shutdown and plugin churn must not produce continuing handle/thread/memory growth. Use native diagnostics/sanitizers where supported, with timing benchmarks in normal release builds.
- **Performance:** milestone durations and the 30 Hz display target are initial acceptance budgets, not measured capabilities. Set numerical DSP, callback, latency and resource budgets before each relevant gate; changes to a budget require a recorded reason, not silently relaxing a failed test.

A milestone record contains the source/dependency revisions, commands, platform, device/fixture, result, counters and unresolved limitations. No radio or plugin access means that portion remains unverified, while independent work can continue.

## First implementation batch

This is the bounded assignment to start coding after plan approval. Do not combine it with a native port or Avalonia implementation.

1. **Baseline/toolchain:** complete M0's source selection and SDK checks, preserving the existing docs and worktree. Record prerequisites and Windows-reference status.
2. **Scaffold:** add the three M1 projects, a separate solution, SDK/package pins and ignores for new build artifacts. Add Windows/macOS/Linux managed CI definitions; do not alter release publishing.
3. **Reuse discovery:** link the existing discovery code and its enum dependency; introduce only the small seams needed for tests and cancellation. Verify Core builds without the legacy application or native libraries.
4. **CLI:** implement NIC listing and bounded discovery with JSON output and documented exit codes. Add offline valid/malformed/timeout/no-radio tests.
5. **Validation:** run available builds/tests, document exact commands, then perform opt-in G2 and pinned P1-simulator discovery checks. Request the user's help only for unavailable hardware access or missing environment information.

Hand off a working discovery tool, its tests and a result log. Explicitly report any platform/hardware tests not run. Do not claim receive capability, fix unrelated VST code, merge FreeDV, or change radio firmware in this batch.

## Decisions to resolve at the relevant gate

- **M0/M1:** Windows reference machine/runner availability; installed G2 firmware/server versions; test NIC topology and simulator host. These do not require a hardware purchase before starting.
- **M2:** dependency pinning/vendor strategy, ABI conventions and numerical comparison fixtures; record choices before broad native edits.
- **M5:** minimum OS versions, preferred audio device/backend and initial UI/control priorities; validate rendering performance before committing to custom GPU work.
- **M6/M10:** the user's required TX modes, station routing and release-blocking compatibility workflows. Start with the narrow qualified path and expand intentionally.
- **M7/M8:** representative plugins, architecture support and editor-window ownership. No promise of Intel-only plugin bridging in the arm64 release.
- **M9/M11:** exact RADE/model provenance and release signing/distribution requirements.

Re-estimate effort after M4. The largest uncertainty is native dependency/lifecycle/radio adaptation, not the initial desktop shell. Completion should be tracked by these observable gates rather than an unsupported calendar estimate.

## Current status

Repository setup and the three static audits are complete. The first implementation batch has now advanced the baseline to `3518930b` and added the discovery CLI, shared projects, tests and CI definitions. The macOS arm64 Release build and 43 offline tests pass; P1 simulator discovery, real Ctrl-C behavior, and live G2 P2 broadcast/targeted discovery also pass locally. See [M1 results](M1_DISCOVERY_RESULTS.md) and [Getting started](GETTING_STARTED.md).

M0/M1 acceptance is still partial: managed CI now passes on Windows, macOS and Linux, but the legacy Windows reference build and Windows live G2/simulator comparisons remain unverified. The G2's raw discovery fields are recorded; installed server/FPGA release versions still need separate recording. Discovery is checkpointed at `77792260`.

M2's initial cross-platform offline gate passes on Windows x64, macOS arm64 and Linux x64; see [CI results](NATIVE_CI_RESULTS.md), [initial local M2 results](M2_NATIVE_RESULTS.md) and [native build instructions](NATIVE_DSP.md). M3a's offline ChannelMaster DSP/pipe lifecycle is implemented with a .NET owner, 100-cycle tests, cancellation/rollback, no-device audio and radio-init argument correction. Its qualification and limits are tracked in [ChannelMaster offline](CHANNELMASTER_OFFLINE.md). M3b's RNet/socket lifecycle and M4–M11 remain pending. No radio streaming or live TX capability has been implemented or tested.
