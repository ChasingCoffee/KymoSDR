# VST divergence audit

Date: 2026-09-04. Method: static source and Git tree comparison; no plugin execution.

## Scope and conclusion

Compared:

- SDR-VST3 baseline: `275f7683291413d7499136ec5105421e39184803`.
- ChasingCoffee reference: `cc62ad73d99131ea005762adef947fb0bebba8a0` (`vst-support`).
- Newer SDR-VST3: `80538e3884aff03f7e013ce7e7bae9723700cc65` (`upstream/master`).

SDR-VST3 retains the original VST3 hosting architecture and adds useful class-ID handling, profile persistence, import/export, and routing controls. Use it as the porting foundation. Do not merge the reference branch wholesale. There are specific lifecycle and catalog defects to address before carrying this subsystem into the shared engine.

The repositories have **no common Git ancestor** in the fetched, non-shallow histories. `git merge-base` returns no result. This audit therefore compares endpoint trees, ignoring whitespace when inspecting changes; it does not infer feature absence from unmatched commit IDs.

None of the 18 later SDR-VST3 commits changes `VstHostBridge`, `VstAudioHost`, `VstCommon`, `VstPluginScanner`, `Console/vsthost.cs`, or the `Console/Vst*.cs` files. Findings in those files apply to both SDR-VST3 endpoints. Later changes to audio callbacks, meters, and FreeDV still affect integration; see [the baseline review](UPSTREAM_BASELINE_REVIEW.md).

## Behavior comparison

Paths below are relative to `Project Files/Source/`. “Present” means implemented in source, not runtime-validated.

| Area | Classification | Evidence and consequence |
| --- | --- | --- |
| Separate RX/TX chains; add/remove/reorder; enable and bypass | Already present in SDR-VST3 | `Console/vsthost.cs`, `VstHostBridge/vst_chain.cpp`, and `vst_host_bridge.h` retain the chain operations and processing snapshots. |
| Out-of-process audio hosting, shared audio buffers, control messages, host-state reporting and restart/state replay | Already present in SDR-VST3 | `VstHostBridge/vst_host_bridge.cpp`, `VstAudioHost/host_process.cpp`, `VstCommon/vst_ipc.h`. Preserve the separation when replacing Windows IPC. Isolation is at host-process scope; do not assume every individual plugin has its own sandbox. |
| Plugin state blobs and editor-driven parameter changes | Already present in SDR-VST3 | `vst_runtime.cpp` retains component/controller state handling, `performEdit`, input parameter transfer, and output parameter caching. State persistence remains opaque plugin data. |
| General parameter enumeration/editing API for a headless managed client | Needs manual review | Neither bridge header exposes a general enumerate/get/set-parameter API. Editor parameter plumbing exists; the plan's future headless parameter milestone still needs an explicit interface. This is not a lost reference feature. |
| Multiple VST3 effects in one bundle, especially WaveShell | Improved/replaced in SDR-VST3 | `ClassId`/CID added to managed models, native descriptors, probe results, IPC and `VST_AddPlugin`; scanner enumerates all classes. Native and managed binaries must be rebuilt together because the ABI changed. See catalog defect below. |
| Scanner metadata, diagnostics and slow plugins | Improved/replaced in SDR-VST3 | `VstPluginScanner/Program.cs` and `vsthost.cs` add multi-class probing and progress/timing. `PluginProbeTimeoutMs` is now 1,800,000 ms (30 minutes); the synchronous wait does not observe cancellation while the child is running. Improve cancellation before porting this workflow. |
| Profile-specific RX/TX chains; chain import/export; failed-plugin rows | Improved/replaced in SDR-VST3 | `VstProfileState`, `SetCurrentProfile`, `SaveProfileChains`, and requested/failed-plugin tracking in `vsthost.cs`; `ExportChain`, `ImportChain`, `BuildDisplayPluginList` in `VstChainManagerForm.cs`. Preserve unavailable entries and class IDs during migration. |
| Rack view, compact view, drag reorder and scrolling fix | Already present in SDR-VST3 | `VstRackView.cs` retains the reference layout/scrolling logic. Its functional diff adds chain-bypass appearance, tooltips and class-aware artwork rather than replacing scrolling. Visual behavior still needs desktop testing. |
| Floating/on-screen racks | Improved/replaced in SDR-VST3 | Reference `VstRackContainerManager.cs` is removed; `VstRackMeterHost.cs` embeds racks in regular meter gadgets, wired through `MeterManager.cs`. Dock/float behavior now follows meter infrastructure. |
| Automatic migration of old standalone rack geometry | Missing from SDR-VST3 | Reference `VstUiState.RxContainerVisible`/`TxContainerVisible` and associated position/size/floating fields are removed. No conversion to meter-container state was found. Preserve audio chains, but expect a separate UI-layout migration decision. |
| Rack/front-panel enable/bypass synchronization | Improved/replaced in SDR-VST3 | Native dirty-state callbacks added to bypass/enable operations; managed `ChainStateChanged` subscriptions and `VstRackView.ChainBypassed` added. |
| Plugin editor windows, resize, close/reopen and artwork capture | Already present in SDR-VST3 | `vst_runtime.cpp` retains editor sessions; `VstEditorCapture.cs` retains capture, with class-aware artwork keys. Implementation remains HWND/Win32-specific. |
| VAC/TCI processed-versus-dry routing | Improved/replaced in SDR-VST3 | `ChannelMaster/cmaster.c:vst_tx_chain_active`, `ivac.c`, `tci.c`, plus managed audio settings add explicit TX source routing and post-VST TCI RX feed. Existing persisted names containing `Bypass_TX_VST` now back “Apply TX VST” controls: check migration semantics rather than assuming names reflect behavior. |
| VST2 DLL hosting | Not relevant to macOS port | Explicitly removed, including `vst2_runtime.*`, VST2 format values, manual-load control and three VST2 tests. The agreed scope is VST3. Old VST2 presets cannot be assumed compatible. |

## Findings requiring correction or focused validation

### VST-1: Incremental scanning loses sibling classes from a shared bundle

**High confidence from source; present at both SDR-VST3 endpoints.** `BuildCatalogPluginMap` keys a single plugin by normalized file path (`vsthost.cs:2194`). A bundle with classes A and B writes both to the same dictionary key, retaining only the last. The cached branch of `ScanPluginCatalog` (`:1200`) then adds that one entry and returns. A full scan can discover all classes, while a later unchanged-file scan can collapse them to one.

Preserve a list of class entries per bundle for cache reuse, and use bundle path plus class ID where identifying an individual effect. Verify with two effects sharing one `.vst3` path, then full scan → incremental scan → restart. Source: [catalog/scanner implementation](../Project%20Files/Source/Console/vsthost.cs).

### VST-2: Deferred destruction can spin after its first notification

**High confidence from source; runtime CPU measurement pending.** `vst_chain.cpp:144` creates `g_deferred_event` as a manual-reset event. The worker waits on it (`:106`) and exits its inner drain loop when the queue is empty, but never resets that event. After the first `SetEvent`, an empty queue can repeatedly wake the worker until shutdown.

Use a notification/drain design that cannot lose wakeups or remain permanently signaled. Verify idle CPU after removing a plugin. Sources: [worker implementation](../Project%20Files/Source/VstHostBridge/vst_chain.cpp), [Windows event semantics](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createeventw).

### VST-3: Deferred destruction adds an unmatched runtime reference

**High confidence from source; memory/thread growth measurement pending.** `release_processing_state` transfers each runtime to `deferred_destroy_runtime` and clears the old pointer (`vst_chain.cpp:355`). The queue function increments its reference count (`:179`); the worker later decrements it once. The original processing-state reference is never released on this path. For a sole owner, the sequence is 1 → 2 → 1, so final destruction is not reached.

Define whether the queue takes ownership or borrows a retained reference, and balance both sides. Test repeated insert/remove/reorder and shutdown with open editors. Sources: [processing-state release](../Project%20Files/Source/VstHostBridge/vst_chain.cpp), [reference-count operations](../Project%20Files/Source/VstHostBridge/vst_plugin_runtime.cpp).

### VST-4: Broad teardown workarounds need deliberate handling

`vst_runtime.cpp` suppresses module unloading and its `HostCommandShutdown` skips the ordinary runtime/plugin termination sequence. These changes are described as workarounds for plugins that hang during teardown. They are not proof of safe unloading and should not become the macOS lifecycle contract. Retain process-level recovery while validating clean close, repeated load/unload and bounded shutdown. Source: [runtime shutdown and module ownership](../Project%20Files/Source/VstHostBridge/vst_runtime.cpp).

### VST-5: Class identity and path migration need consistent treatment

`RemoveRequestedPlugin` still identifies entries by path alone, and collapsed-rack state also stores paths. Distinct effects within one bundle can therefore remain ambiguous outside the new native CID support. Windows plugin paths in saved profiles and the seeded catalog also cannot identify an installed Mac plugin by themselves. Define cross-platform resolution by class ID, preserving unresolved entries and user intent. Cross-OS plugin state compatibility must be tested per plugin. Sources: [managed models and persistence](../Project%20Files/Source/Console/vsthost.cs), [rack state](../Project%20Files/Source/Console/VstRackView.cs).

## Porting boundary

Keep shared chain models, state migration and user-visible behavior. Replace the Windows implementations of process startup, named pipes/shared-memory signaling, plugin module loading, editor window management and editor capture. `VstCommon` is not portable merely because of its name: its packet layouts include native `wchar_t` arrays.

Use explicit-width fields and a defined string encoding for a portable native interface. Current .NET Unicode marshaling is UTF-16, which must not be treated as the platform's native `wchar_t` layout on Unix. Source: [IPC packet](../Project%20Files/Source/VstCommon/vst_ipc.h), [Microsoft marshaling reference](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/charset).

## Validation limits and follow-up

Existing `Thetis.Tests/VstHostTests.cs` mostly tests managed models/helpers. Its SDR-VST3 diff removes VST2-specific tests; it does not add coverage of class-bundle cache reuse or the new lifecycle worker. The test project targets `net10.0-windows` and references a prebuilt Debug `Thetis.dll` plus a legacy Newtonsoft package path. No .NET executable was found on this machine's PATH, and no Windows or plugin runtime tests were run.

Before VST migration, address VST-1 through VST-3 with focused regression checks, preserve the original reference for behavioral comparison, and validate state/profile round trips, missing-plugin restoration, rack order, RX/TX routing and editor lifetime. VST work need not block the first discovery-only harness.
