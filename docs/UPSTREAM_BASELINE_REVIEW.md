# Upstream baseline and FreeDV review

Date: 2026-09-04. Static review of the 18 commits after the planned .NET 10 merge.

## Recommendation

Use `3518930bc12b976457130d681a5a80b569232e31` as the **next development baseline for initial headless bring-up**: it includes the first 15 newer commits without the three FreeDV-related imports. Keep FreeDV/RADE on the project roadmap and bring those final commits forward after restoring their build inputs and resolving the TX-chain interaction described below.

This is a development-baseline recommendation, not certification that the existing Windows application is defect-free. The newer streaming tap and inherited engine/VST defects also need attention. Avoid starting broad code extraction and then letting this baseline decision drift indefinitely.

The working `cross-platform` branch remains at `275f7683291413d7499136ec5105421e39184803` for this audit. No source commits were merged or cherry-picked. The newer tip remains available at `upstream/master` (`80538e3884aff03f7e013ce7e7bae9723700cc65`).

Why this split: through `3518930`, the trees of WDSP, ChannelMaster, VST bridge, VST audio host, shared VST IPC and scanner are unchanged from `275f768`. The final three commits add compulsory native dependencies and change the RX/TX processing path. Starting with all 18 is also possible, but then a build-time FreeDV boundary or complete dependency recovery becomes part of the first native bring-up.

## Commit-by-commit assessment

“Take” means suitable to include in a development baseline on the evidence reviewed, subject to normal builds and regression checks. None was runtime-tested in this audit.

| Commit | Change | Assessment |
| --- | --- | --- |
| `d6241a88` | GPU peak-hold overlay and branding | Take. Windows display/reference behavior; preserve data semantics when replacing the renderer. |
| `c3e0b935` | Band-stack window sizing | Take. Local Windows layout fix. |
| `d786ed46` | RX1 filter control layout | Take. Local Windows layout fix. |
| `7625cdb3` | Auto ATT checkbox layout | Take. Local Windows layout fix. |
| `d2429434` | Courtesy tone at TX start/release | Take with TX regression coverage. Adds a PTT/timing interaction to preserve, including future RADE end-of-over handling. |
| `86e6e70c` | Remove DXVorticeCompat rectangle shim | Take. Windows rendering cleanup; does not make the display portable. |
| `ed1d6403` | Default courtesy-tone asset | Take with the preceding tone feature; verify asset packaging. |
| `30620b2d` | Additional 11 m channel text | Take as channel-label data; preserve its database integration. |
| `be24ba30` | Post-VST RX/TX streaming output for capture applications | Take as optional Windows functionality, with the callback issue below recorded. Port the tap abstraction independently of NAudio/WASAPI. |
| `ef4f5bd6` | Bundle .NET desktop runtime in Windows installer | Take as Windows packaging. Irrelevant to Mac native-library portability. |
| `00773bb2` | Remove old migration/handoff notes | Take. Historical notes remain in Git history. |
| `b2ff51e7` | Stop tracking WDSP conversion plan | Take. Documentation cleanup. |
| `536c230b` | Remove old root PDF changelogs | Take. Repository cleanup; user-created `docs/` is unaffected. |
| `db953a4b` | Remove old root office changelogs | Take. Repository cleanup. |
| `3518930b` | Country-data tests and ignore/test-project changes | Take. Last pre-FreeDV commit; test infrastructure still has Windows and prebuilt-DLL assumptions. |
| `d9a25852` | Opus DNN headers and Windows prebuilt libraries | Defer with FreeDV as a group. The committed tree is not the complete source described by its provenance note. |
| `fcf6772f` | RADE/FreeDV native wrapper, mic DSP, resampling and setup | Defer until dependency recovery/feature isolation and TX routing validation. This is the substantial native-engine change. |
| `80538e38` | FreeDV Reporter and console/meter integration | Defer with RADE. Useful reporting/UI functionality, but coupled to the existing forms and RADE controls. |

Scope evidence: `git log --reverse 275f768..80538e38`; endpoint diffs for `Project Files/Source` and `.github`; source inspection of startup, audio callbacks, routing, build definitions and added subsystems. The final Source/workflow diff is 53 files, 11,671 insertions and 462 deletions, excluding the added vendored dependency tree and other root changes.

## What the FreeDV work provides

The native wrapper implements RADE V1/V2 mode selection, two receiver decode paths, one TX path, resampling, neural speech processing, microphone conditioning and end-of-over/callsign handling. The reporter adds a station list and reporting through `qso.freedv.org`. This is a useful feature to preserve. Do not equate this import with support for every historical FreeDV mode.

Evidence: [RADE wrapper](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/ChannelMaster/radae.c), [reporter sources](https://github.com/nubbyless/SDR-VST3/tree/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/Console/FreeDVReporter).

## FDV-1: Four native dependency implementations are omitted

At the reviewed SDR-VST3 tip, Git's complete tracked-file inventory contains **zero `.c`, `.cpp` or `.cc` implementation files** beneath each of these four directories:

| Dependency | Supplied in SDR-VST3 | Missing for reproducible source builds |
| --- | --- | --- |
| `lib/radae_c` | Headers; Windows Release `rade.lib`/symbols; provenance note | Implementation, generated/model data and build definition. The note identifies another fork, but does not record its exact commit hash. |
| `lib/opus_dnn` | Headers; Windows Debug/Release `opus.lib`; provenance note | Opus implementation, RADE-specific data tables and CMake build inputs. The note names upstream Opus commit `940d4e5af64351ca8ba8390df3f555484c567fbb`, but the added neural tables need provenance too. |
| `lib/libebur128` | Headers; Windows Release `ebur128.lib`; provenance note | Implementation and source build. |
| `lib/WebRTC_AGC` | Headers; Windows Release library; provenance note | Implementation and CMake build. |

The Opus note says it is a complete vendored copy; the tracked tree contradicts that claim. Audit the Git inventory, not just the local upstream author's build notes. RADE, libebur128 and WebRTC notes explicitly describe their source omissions.

The ChannelMaster project links these libraries without a feature condition. Its Debug library search also expects configuration-specific directories, while three of the dependencies provide only Release artifacts; a clean Windows Debug build needs checking too. Sources: [ChannelMaster project](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/ChannelMaster/ChannelMaster.vcxproj), [Opus provenance](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/lib/opus_dnn/commit_pin.txt), [RADE provenance](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/lib/radae_c/commit_pin.txt).

### Recovery is plausible, but not yet verified as a matching build

The named [sv1eia/Thetis-RADE source](https://github.com/sv1eia/Thetis-RADE) is available. A recursive GitHub tree inventory returned `408f2b5232ff0a2aec9b538a40d4cb1b02627b17`, with `truncated=false`, and these implementation counts: radae_c 24, opus_dnn 223, libebur128 1, WebRTC_AGC 2. This is a candidate recovery source, **not proof it matches the prebuilt libraries or their model versions**.

Before import, record exact commits and hashes for wrapper dependencies and neural data, compare exported APIs/structs against the included headers, and build them from source for macOS arm64 and Windows. The r8brain header/FFT slice and FreeDV text codec implementations are already present in SDR-VST3. Keep all dependency notices with recovered sources; this audit is not a license-compliance determination.

## FDV-2: Disabling the feature does not avoid its startup cost/dependencies

`ChannelMaster/pipe.c:116` calls `create_radae()` unconditionally. That function initializes the library, opens modem instances and allocates associated buffers even before an RX enable flag is used. `xradae_rx`/`xradae_tx` return early when disabled, but those guards are later in processing.

For a shared engine, define a real optional build and lifecycle boundary, including the new native symbols and managed setup calls. A disabled checkbox alone will not let a Mac build link or initialize without these dependencies. Sources: [pipe startup](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/ChannelMaster/pipe.c#L116), [RADE initialization](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/ChannelMaster/radae.c#L443).

## FDV-3: TX VST processing can receive an already-encoded modem waveform

**High-confidence call-order finding; on-air impact untested.** The new `xradae_tx(buff)` is inside the TX input branch of `xpipe`. `xcmaster` calls that branch, then `xdexp`, then the configured TX VST chain, then WDSP TX processing.

```text
TX input selection → xpipe / RADE encoder → expander → TX VST → WDSP TX
```

With RADE and a non-bypassed microphone TX chain enabled, a speech EQ/compressor/gate can therefore process the encoded modem waveform rather than microphone speech. The existing VST callback/source gate does not check RADE mode. No coordinated VST bypass was found in the inspected RADE enable handlers. Assess the post-encoder expander/WDSP processing as well.

Define the intended mode-specific chain and test it with captured buffers/loopback. Candidate designs are speech processing before encoding, or an explicit RADE bypass policy for incompatible processors. Validate PTT/end-of-over flush and courtesy-tone interactions too. Do not move callbacks casually, since VAC/TCI selection and TX state timing also depend on their positions. Sources: [TX splice](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/ChannelMaster/pipe.c#L263), [TX processing order](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/ChannelMaster/cmaster.c#L426), [enable handlers](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/Console/setup.cs#L33791).

## UP-1: The new streaming tap is not strictly nonblocking/allocation-free

`AudioStreamOut.cs` describes its callback as nonblocking and allocation-free, but `Feed` takes `lock (sink.Gate)` and can allocate a new scratch byte array when the block grows. Treat those header comments as intent rather than guarantees. The feature is optional and Windows-specific; keep it disabled for initial timing measurements and redesign the shared tap around bounded preallocated buffers. Sources: [Feed implementation](https://github.com/nubbyless/SDR-VST3/blob/80538e3884aff03f7e013ce7e7bae9723700cc65/Project%20Files/Source/Console/AudioStreamOut.cs#L132), managed `cmaster.cs` RX/TX callback integration.

## Validation and next action

No .NET executable is available on this machine's PATH; there was no Windows, hardware, audio or plugin execution. No FreeDV-specific tests were found in the inspected test project. Existing Windows release workflow success, if obtained separately, would still not establish Mac rebuildability or FreeDV/VST signal correctness.

The smallest next implementation remains the discovery harness in [the portability audit](MACOS_PORTABILITY_AUDIT.md). Settle the baseline at `3518930` before extraction, then recover/isolate FreeDV dependencies and validate its TX chain as a bounded follow-up. The existing radio-init ABI mismatch and VST lifecycle/catalog defects are recorded in the other audits and predate these 18 commits.
