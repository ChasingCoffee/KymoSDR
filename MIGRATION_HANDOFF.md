# .NET 10 MIGRATION HANDOFF - SDR-VST3

Created: 2026-08-19
Branch: `net10-migration` (from `wdsp2-conversion` at `f4cca3d`)
Starting Version: v4.5

## Motivation

The display system (spectrum, waterfall, meters) is limited by the dead SharpDX 4.2 dependency (abandoned 2019, no .NET 5+ support). Migrating to .NET 10 unlocks modern GPU-accelerated graphics libraries (Vortice, compute shaders, etc.) for a significantly improved display pipeline.

## Goal

Migrate the entire solution from .NET Framework 4.8 to .NET 10, replacing SharpDX with Vortice and enabling modern display rendering.

---

## Solution Inventory

| # | Project | Type | Current TFM | Format | Migration Action |
|---|---|---|---|---|---|
| 1 | **Thetis** | WinExe | net4.8 | Legacy csproj (1255 lines) | SDK-style → net10.0-windows, 600+ Compile items, 69 NuGet packages |
| 2 | **Midi2Cat** | Library | net4.8 | Legacy csproj | SDK-style → net10.0-windows, ~20 source files |
| 3 | **RawInput** | Library | net4.8 | Legacy csproj | SDK-style → net10.0-windows, ~12 source files |
| 4 | **VstPluginScanner** | Exe | net4.8 | Legacy csproj | SDK-style → net10.0-windows, 1 source file |
| 5 | **Thetis.Tests** | Library | net48 | SDK-style | Update TFM → net10.0-windows |
| 6 | wdsp | C++ DLL | N/A | vcxproj | Untouched |
| 7 | ChannelMaster | C++ DLL | N/A | vcxproj | Untouched |
| 8 | cmASIO | C++ DLL | N/A | vcxproj | Untouched |
| 9 | VstHostBridge | C++ DLL | N/A | vcxproj | Untouched |
| 10 | VstAudioHost | C++ Exe | N/A | vcxproj | Untouched |
| 11 | Thetis-Installer | WiX | N/A | wixproj | Separate concern (later) |

---

## Phase Plan

### Phase 1: Mechanical Migration (current)

**Goal**: Clean compile on net10.0-windows. No functional changes.

- [ ] Convert Thetis.csproj → SDK-style with explicit `<Compile>` list
- [ ] Convert Midi2Cat.csproj → SDK-style
- [ ] Convert RawInput.csproj → SDK-style
- [ ] Convert VstPluginScanner.csproj → SDK-style
- [ ] Update Thetis.Tests.csproj TFM to net10.0-windows
- [ ] Replace packages.config → `<PackageReference>` for all projects
- [ ] Audit all 69 NuGet packages for .NET 10 compatibility
- [ ] Remove dead references: System.Runtime.Remoting (11 Invoke files), System.Web imports
- [ ] Remove obsolete packages: System.ValueTuple, Microsoft.Bcl.Memory/HashCode/AsyncInterfaces (built into .NET 10), Microsoft.NETFramework.ReferenceAssemblies
- [ ] Verify/replace: FTD2XX.Net, WindowsFirewallHelper, Svg for .NET 10
- [ ] Replace System.Windows.Forms.DataVisualization.Charting (AmpView) with LiveCharts2 or ScottPlot
- [ ] Preserve PreBuildEvent (VersionInfo.cs generation)
- [ ] Preserve PostBuildEvent (native DLL copies: fftw, rnnoise, specbleach, CATStructs.xml, etc.)
- [ ] Preserve AssemblyInfo.cs manual versioning
- [ ] Fix all compile errors
- [ ] Verify output: Thetis.exe + native DLLs land correctly in bin/x64/Release

### Phase 2: SharpDX → Vortice (future)

**Goal**: Working GPU rendering via Vortice.Direct2D1/Direct3D11.

- [ ] Replace SharpDX packages (6) with Vortice equivalents (4)
- [ ] Rewrite display.cs SharpDX rendering (~322 lines, ~447 draw calls)
- [ ] Rewrite MeterManager.cs DXRenderer (~482 lines, ~180 dispose calls)
- [ ] Test all display modes: 2D pan, 3D pan, waterfall, meters

### Phase 3: Display Improvements (future)

**Goal**: Modern GPU-accelerated rendering leveraging Vortice. Tiered roadmap — see "3D Panadapter Modernization Plan" section below for full detail.

- [x] Tier 1: Quick visual wins inside existing D2D renderer (temporal interpolation, edge smoothing, side walls, perceptual colormaps, grid floor, exponential fog) — **CODE COMPLETE 2026-08-20, RUNTIME VERIFIED 2026-08-22** (see session history)
- [x] **MANDATORY: GPU→CPU fallback architecture (applies to ALL GPU features below)** — implemented 2026-08-22 as part of Tier 3 mesh: DXRenderPath enum, HW→WARP init chain, ForceCPURendering setting, tryWarpDowngrade mid-session auto-downgrade, single dispatch point (RenderGpuMesh3D) with automatic D2D fallback on failure. See "GPU Fallback Architecture Requirement" section.
- [x] Tier 3: GPU mesh-based 3D panadapter (replacing per-column DrawLine; fixes edge stepping geometrically) — **FIRST SLICE + TIER-1 PARITY DONE, RUNTIME VERIFIED, COMMITTED `89f05af` + follow-up 2026-08-23** (crest hairlines/side walls/grid floor ported; slope shading was already at parity). Remaining: GPU% measurement, RDP/WARP fallback sanity test. See session history.
- [x] GPU compute shaders for spectrum/waterfall — **CODE COMPLETE 2026-08-25**: Display.SpectrumCompute.cs implements two D3D11 compute shaders (cs_5_0): (1) waterfall colour conversion (dBm → BGRA via 1024-entry LUT texture, supports all 7 schemes), (2) spectrum normalisation (dBm → [0..1] heights). Integration: DrawWaterfallDX2D dispatches compute before mesh commit/D2D scroll; TryRenderSpectrumFillMesh dispatches compute before CPU normalisation loop. Both paths fall back to CPU on any failure. GPU sync via D3D11 Event query. Experimental checkbox "GPU compute shaders (exp.)" added to DirectX Display Settings group. **RUNTIME TESTING PENDING.**

### GPU Fallback Architecture Requirement (added 2026-08-22)

End users may have no discrete/integrated GPU, an underpowered GPU, a remote-desktop session, or broken drivers. Every GPU-accelerated feature added in Phase 2+ MUST degrade gracefully to a CPU path. Rules for all future display work:

1. **Never delete the D2D line-based renderer when the GPU mesh path (Tier 3) lands** — it becomes the permanent CPU fallback and must stay maintained.
2. **Capability detection at DX init** (display.cs `initDX2D` / existing `DX2Adaptors()`): probe adapter + feature level; on failure OR user override setting → set a render-path flag (`_renderPath = GpuMesh | D2DCpu | WarpSoftware`). Re-validate on device-lost events; auto-downgrade mid-session if DeviceRemoved persists past the retry budget.
3. **Single draw entry point**: `DrawPanadapter3DHistoryDX2D` dispatches to mesh or line path behind one interface/local function so fallback is a branch, not a fork of the whole render pipeline.
4. **WARP as middle tier**: Microsoft's software rasterizer (`D3D_DRIVER_TYPE_WARP`) can back Tier 2 effects and even the Tier 3 mesh on GPU-less machines — try HW → WARP → pure-D2D-CPU in order.
5. **Compute shaders (final item) are optional acceleration only** — spectrum/waterfall data must always be produced on CPU too; compute result feeds the same buffers.
6. **User-visible setting**: "Display rendering: Automatic / Force CPU" in setup, persisted like other display options; plus log the chosen path at startup to ErrorLog.txt for support.
7. **Test matrix before shipping any GPU feature**: hardware GPU, WARP-only, D2D-CPU forced, and remote-desktop session.

**NEXT SESSION START HERE — GPU fallback scoping facts (gathered 2026-08-22; FIRST SLICE IMPLEMENTED same day, see session history)**:
- ~~Only DX init call site: display.cs ~1262 in the `displayTarget` setter → `initDX2D(DriverType.Hardware, _display_adaptor)`. No WARP retry exists~~ → now an attempt chain (preferred adaptor → default HW → WARP) inside initDX2D via `createDX2DDevice`; forced-CPU skips straight to WARP.
- `DX2Adaptors()` at display.cs ~3450 enumerates adapters (used for adaptor preference); device/swapchain teardown block ~3390–3415.
- The legacy non-DX2 GDI draw path is GONE — D2D/Vortice is the only renderer, so "D2DCpu" tier = current line-based renderer (keep it maintained per rule 1), "WarpSoftware" tier = WARP-backed device (`DriverType.Warp`), live since first slice.
- Remaining from this slice's scope: runtime verification only (HW machine normal boot, checkbox toggle re-init, RDP/WARP behaviour). MeterManager dxInit fallback NOW ALSO IMPLEMENTED (same day follow-up, see session history).
- Still future: ~~Tier 3 GPU mesh dispatch behind one entry point (rule 3)~~ → **DONE 2026-08-22**: `Display.Pan3DMesh.cs` `RenderGpuMesh3D()` is the single dispatch (called pre-BeginDraw in RenderDX2D; returns false → D2D line path draws, incl. try/catch auto-fallback per rules) + compute shaders (rule 5).

---

## NuGet Package Audit (69 packages)

### Keep As-Is (compatible with net10.0)
| Package | Version | Notes |
|---|---|---|
| Discord.Net.Commands | 3.18.0 | netstandard2.0 compatible |
| Discord.Net.Core | 3.18.0 | netstandard2.0 compatible |
| Discord.Net.Interactions | 3.18.0 | netstandard2.0 compatible |
| Discord.Net.Rest | 3.18.0 | netstandard2.0 compatible |
| Discord.Net.Webhook | 3.18.0 | netstandard2.0 compatible |
| Discord.Net.WebSocket | 3.18.0 | netstandard2.0 compatible |
| ExCSS | 4.3.1 | net48 target, likely compatible |
| HtmlAgilityPack | 1.12.4 | netstandard2.0 compatible |
| Markdig | 1.2.0 | net462+ compatible |
| Microsoft.CodeAnalysis.Common | 5.3.0 | netstandard2.0 compatible |
| Microsoft.CodeAnalysis.CSharp | 5.3.0 | netstandard2.0 compatible |
| Microsoft.CodeAnalysis.CSharp.Scripting | 5.3.0 | netstandard2.0 compatible |
| Microsoft.CodeAnalysis.Scripting.Common | 5.3.0 | netstandard2.0 compatible |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.8 | net462+ compatible |
| Microsoft.NET.StringTools | 18.6.3 | net472+ compatible |
| NAudio | 2.3.0 | net472+ compatible |
| NAudio.Asio | 2.3.0 | netstandard2.0 compatible |
| NAudio.Core | 2.3.0 | netstandard2.0 compatible |
| NAudio.Midi | 2.3.0 | netstandard2.0 compatible |
| NAudio.Wasapi | 2.3.0 | netstandard2.0 compatible |
| NAudio.WinForms | 2.3.0 | net472+ compatible |
| NAudio.WinMM | 2.3.0 | netstandard2.0 compatible |
| Newtonsoft.Json | 13.0.4 | netstandard2.0 compatible |
| SkiaSharp | 3.119.2 | net462+ compatible |
| SkiaSharp.NativeAssets.macOS | 3.119.2 | Platform native assets |
| SkiaSharp.NativeAssets.Win32 | 3.119.2 | Platform native assets |
| System.CodeDom | 10.0.8 | Microsoft package, compatible |
| System.Collections.Immutable | 10.0.8 | Microsoft package, compatible |
| System.Drawing.Common | 10.0.8 | Microsoft package, compatible |
| System.Formats.Nrbf | 10.0.8 | Microsoft package, compatible |
| System.Interactive.Async | 7.0.1 | netstandard2.0 compatible |
| System.IO.Compression | 4.3.0 | Built into .NET 10 but pkg is fine |
| System.IO.Compression.ZipFile | 4.3.0 | Built into .NET 10 but pkg is fine |
| System.Linq.Async | 7.0.1 | netstandard2.0 compatible |
| System.Linq.AsyncEnumerable | 10.0.8 | Microsoft package, compatible |
| System.Reflection.Metadata | 10.0.8 | Microsoft package, compatible |
| System.Resources.Extensions | 10.0.8 | Microsoft package, compatible |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | Microsoft package, compatible |
| System.Security.AccessControl | 6.0.1 | Microsoft package, compatible |
| System.Security.Principal.Windows | 5.0.0 | Microsoft package, compatible |
| System.Text.Encoding.CodePages | 10.0.8 | Microsoft package, compatible |
| System.Threading.Tasks.Extensions | 4.6.3 | Built into .NET 10 but pkg is fine |

### Replace (incompatible / dead)
| Package | Version | Replacement | Notes |
|---|---|---|---|
| SharpDX | 4.2.0 | Vortice.Direct2D1 | Dead (abandoned 2019) |
| SharpDX.Desktop | 4.2.0 | (merged into Vortice) | Dead |
| SharpDX.Direct2D1 | 4.2.0 | Vortice.Direct2D1 | Dead |
| SharpDX.Direct3D11 | 4.2.0 | Vortice.Direct3D11 | Dead |
| SharpDX.DXGI | 4.2.0 | Vortice.DXGI | Dead |
| SharpDX.Mathematics | 4.2.0 | Vortice.Mathematics | Dead |
| System.Windows.Forms.DataVisualization.Charting | (framework) | LiveCharts2 or ScottPlot | Not available on .NET 10 |

### Remove (built into .NET 10)
| Package | Version | Reason |
|---|---|---|
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3 | Not needed on .NET 10 |
| Microsoft.NETFramework.ReferenceAssemblies.net48 | 1.0.3 | Not needed on .NET 10 |
| Microsoft.NETFramework.ReferenceAssemblies.net35 | 1.0.3 | Not needed on .NET 10 |
| System.ValueTuple | 4.6.2 | Built into .NET 10 |
| Microsoft.Bcl.Memory | 10.0.8 | Built into .NET 10 |
| Microsoft.Bcl.HashCode | 6.0.0 | Built into .NET 10 |
| Microsoft.Bcl.AsyncInterfaces | 10.0.8 | Built into .NET 10 |
| System.Buffers | 4.6.1 | Built into .NET 10 |
| System.Memory | 4.6.3 | Built into .NET 10 |
| System.Numerics.Vectors | 4.6.1 | Built into .NET 10 |
| Microsoft.CSharp | 4.7.0 | Built into .NET 10 |
| Microsoft.Win32.Registry | 5.0.0 | Built into .NET 10 (via WindowsDesktop) |
| System.Reactive | 6.1.0 | Check if still needed; may have native net10 support |

### Verify
| Package | Version | Concern |
|---|---|---|
| FTD2XX.Net | 1.2.1 | USB driver interop, check for net10 compatible version |
| WindowsFirewallHelper | 2.2.0.86 | Firewall rules, check for net10 compatible version |
| Svg | 3.4.7 | SVG rendering, check for net10 compatible version |
| Microsoft.CodeAnalysis.Analyzers | 5.3.0 | Development dependency, likely fine |

---

## AmpView Chart Replacement

**Decision: Use `WinForms.DataVisualization` NuGet package (v1.10.2) instead of rewriting.**

This is a maintained .NET 6+ port of the original `System.Windows.Forms.DataVisualization.Charting` from the dotnet/winforms-datavisualization project. Drop-in replacement — no code changes needed in AmpView.cs or AmpView.Designer.cs. The package provides the same namespace and types.

### Current Implementation

**AmpView.cs** (552 lines):
- 7 chart series: GainFlatTop, GainNormal, PHFlatTop, PHNormal, MagFlatTop, MagNormal, GainDbAvg
- Dual Y axes: Left = Magnitude (dB), Right = Phase (degrees)
- Dual modes: Normal (flat-top calibration) and Gain (gain view)
- Custom pixel-to-data coordinate mapping for mouse interaction
- Timer-driven updates at configurable interval (100ms default)
- Public `UpdateData()` method called by main app

**AmpView.Designer.cs** (242 lines):
- Designer-generated Chart control initialization
- 7 series with specific chart types (FastLine, FastPoint)
- ChartArea with dual Y-axes, legend, axis titles
- CheckBox controls: StayOnTop, PhaseZoom, LowRes, ShowGain

### Recommended: LiveCharts2
- Modern, GPU-accelerated (SkiaSharp-based, already a dependency)
- WinForms support via `LiveChartsCore.SkiaSharpView.WinForms`
- Supports dual axes, fast line series, zoom/pan
- Active development, .NET 10 compatible

---

## Key Build Concerns

### PreBuildEvent (must preserve)
Generates `VersionInfo.cs` with build date — uses `wmic os get localdatetime`. Must work in SDK-style context.

### PostBuildEvent (must preserve)
Copies native DLLs to output:
- `libfftw3-3.dll`, `libfftw3f-3.dll` (FFTW)
- `rnnoise.dll`, `rnnoise_avx2.dll` (noise reduction)
- `specbleach.dll` (spectral bleach)
- `CATStructs.xml`
- `calculus`, `rnnoise_weights_small.bin`, `rnnoise_weights_large.bin`
- `libSkiaSharp.dll` (from SkiaSharp.NativeAssets.Win32)

### AssemblyInfo.cs
Kept manual with version 4.5. SDK-style projects auto-generate assembly attributes — must set `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>` to avoid conflicts.

### app.config / app.manifest
Must be preserved. SDK-style handles these differently — verify `<ApplicationManifest>` and `<None Include="app.config">` work correctly.

---

## Decisions Log

| Date | Decision | Rationale |
|---|---|---|
| 2026-08-19 | Keep explicit `<Compile Include>` list in SDK-style csproj | Safety — avoid accidental inclusion of stale/broken files |
| 2026-08-19 | Keep manual versioning in AssemblyInfo.cs | Existing workflow, no reason to change |
| 2026-08-19 | Tackle AmpView chart replacement in Phase 1 | Small scope, validates charting library choice before Phase 2 |
| 2026-08-19 | Three-phase approach | Separates mechanical migration from graphics rewrite |
| 2026-08-22 | All GPU features (Tier 2/3, compute) must have CPU fallback path | Users without GPUs / RDP sessions must keep full functionality — see "GPU Fallback Architecture Requirement" |

---

## Build Command

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" "Project Files\Source\Thetis_VS2026.sln" -p:Configuration=Release -p:Platform=x64 -m -nologo
```

Output: `Project Files/bin/x64/Release/` — Thetis.exe + all native DLLs.

---

## Progress Checklist

### Phase 1
- [x] Thetis.csproj → SDK-style
- [x] Midi2Cat.csproj → SDK-style
- [x] RawInput.csproj → SDK-style
- [x] VstPluginScanner.csproj → SDK-style
- [x] Thetis.Tests.csproj → net10.0-windows
- [x] packages.config removed, PackageReference entries added
- [x] NuGet audit complete, incompatible packages flagged
- [x] Dead references removed (System.Web.UI unused import)
- [x] Obsolete packages removed from references (built-in .NET 10 packages)
- [x] AmpView Chart → WinForms.DataVisualization NuGet (drop-in)
- [x] System.Web.UIWebRequestHandler → HttpClientHandler in clsThetisSkinService.cs
- [x] System.IO.Ports → added as NuGet package
- [x] System.ComponentModel.Composition → added as NuGet package
- [x] System.Management → added as NuGet package
- [x] PreBuildEvent preserved (VersionInfo.cs generation)
- [x] PostBuildEvent preserved (native DLL copies)
- [x] AssemblyInfo.cs conflicts resolved (GenerateAssemblyInfo=false)
- [x] Clean build: 0 C# errors on all 5 C# projects
- [x] Suppressed SYSLIB0011 (BinaryFormatter obsolete) — needs proper fix later
- [x] Suppressed NU1510 (NuGet pruning) — resolved packages redundant with .NET 10
- [x] NU1701 warnings (4) from SharpDX — resolved: SharpDX packages removed in Phase 2
- [x] Output verification: native DLLs now copied by MSBuild `<Copy>` tasks (PostBuild target rewritten; lib path corrected to `Project Files\lib`)

### Phase 1.5 — Shutdown Hang Fixes
- [x] MultiMeterIO: added Join(2000) timeout to all 4 connector Stop() methods
- [x] PSForm.CloseAmpView: added Wait(2000) timeout, removed Thread.Abort()
- [x] MeterManager.Shutdown: capped nWait at 500ms to prevent overflow
- [x] Console: set draw_display_thread.IsBackground=true
- [x] **BinaryFormatter → System.Text.Json** in SerializeToBase64/DeserializeFromBase64 (ROOT CAUSE)
- [x] Replaced FormatterServices with direct field copy in ucOtherButtonsOptionsGrid
- [x] Removed all Thread.Abort() calls (15 in console.cs, 4 in TCIServer.cs, 3 in TCPIPcatServer.cs)
- [x] Set all foreground threads to IsBackground=true (TCIServer, TCPIPcatServer, ampvThread, DXRenderer._dxRenderThread)
- [x] ampv.Invoke → ampv.BeginInvoke (PSForm.CloseAmpView)
- [x] _pause_DisplayThread=true before display loop shutdown (releases _objDX2Lock)
- [x] pollOverloadSyncSeqErr: this.Invoke → this.BeginInvoke (prevents UI deadlock)
- [x] Added _is_closing_now guard to prevent Console_Closing re-entry loop

### Phase 2
- [x] Vortice.Direct2D1 3.8.3 + Vortice.Direct3D11 3.8.3 packages added
- [x] MeterManager.cs DXRenderer ported to Vortice — builds clean x64 Release
- [x] display.cs ported to Vortice (~322 refs; only 1 remains, in a comment)
- [x] SharpDX packages removed (all 6 PackageReferences deleted from Thetis.csproj; NU1701 warnings gone)
- [x] All display modes functional — VERIFIED 2026-08-22 (user sign-off after extended runtime use incl. 2D/3D pan, waterfall, meters, GPU fallback toggling; RDP/device-removal auto-downgrade test separately deferred)

### Post-Migration Polish (settings + branding)
- [x] GetSetting JsonElement fix (ConvertLegacyJsonValue) — user verified meters round-trip
- [x] "2|" typed IG-settings blob format; "1|" legacy blobs still readable
- [x] `_default_settings` regenerated as JSON (932 entries)
- [x] `Resources\cty.txt` regenerated as JSON (346 entries) — DXCC/prefix lookups restored
- [x] Old-DB import tested end-to-end: structure auto-upgrades, TX profiles/meters survive, one-time MMIO blob loss warned
- [x] MultiMeterIO warning reworded (actionable: remove & re-add UDP/TCP/serial connections)
- [x] Database Manager fully rebranded to SDR-VST3 (prompts, messages, export filenames) + "sucessfully" typos fixed
- [x] Broader rebrand sweep — Bucket A (safe UI text) DONE 2026-08-27: all user-facing "Thetis" strings → "SDR-VST3" (MessageBoxes, error/dialog titles incl. "DirectX", App About title/label, Startup Log, Meter window title, shutdown splash, CPU-meter "Thetis Only" menu text, settings tooltips/labels, setup.resx label, Midi2Cat startup progress). Bucket B (exe/assembly identity, TCI/CAT protocol strings, N1MM IDs, root-namespace resource names, control names, `ThetisVersion` data key) and Bucket C (upstream links/URLs, User-Agent, GPL attribution, skin-version metadata, type-qualified resx assembly refs) deliberately left as-is. Clean x64 Release build (EXIT=0, pre-existing CA1416 only).
- [ ] DXCC/country prefix lookup runtime verification (validates cty.txt regen)

### Phase 3
- [x] Tier 1: temporal interpolation, edge smoothing, side walls, Turbo/Viridis colormaps, grid floor, exponential fog — **CODE COMPLETE 2026-08-20 (below), RUNTIME VERIFIED 2026-08-22 (user sign-off)**
- [x] **BLOCKER RESOLVED 2026-08-22: uncheck-Waterfall-Sync crash — root cause was `Pan3DLineColor` setter disposing `m_bDX2_3d_fill_brush` WITHOUT nulling it → Classic+Sync-OFF frame drew through a disposed COM brush. Fixed (dispose+null + stopsColl leak); user verified "seems fixed so far". Dumps/WER key intentionally left in place for now. See 2026-08-22 session entries**
- [x] Tier 2: bloom/glow (ID2D1DeviceContext effects graph) — **DONE + RUNTIME VERIFIED 2026-08-22 (panadapter trace glow "Line Glow", HW-only)**
- [ ] Tier 3: GPU mesh 3D panadapter (replaces per-column DrawLine; fixes edge stepping) — **FIRST SLICE + TIER-1 PARITY DONE, RUNTIME VERIFIED, COMMITTED `89f05af`+this commit 2026-08-23** (crest hairlines/side walls/grid floor ported; sheet made fully opaque after user feedback — see 2026-08-23 session entry). Remaining: GPU% measurement, RDP/WARP fallback sanity test
- [ ] GPU compute shaders for spectrum

---

## Session History

### 2026-08-19
- Created `net10-migration` branch from `wdsp2-conversion` (at `f4cca3d`)
- Completed full investigation of .NET 10 migration feasibility
- Mapped all 69 NuGet packages for compatibility
- Analyzed AmpView chart replacement scope
- Documented all build concerns (PreBuild, PostBuild, AssemblyInfo)
- Created this handoff document

### 2026-08-19 (continued)
- Phase 1: All 5 C# projects converted to SDK-style .NET 10, 0 compile errors
- Phase 1.5: Fixed shutdown hang — MultiMeterIO infinite Join, PSForm infinite Wait, display thread foreground blocking
- Phase 2: Added Vortice packages, began DXRenderer migration research

### 2026-08-20 (MeterManager.cs Vortice port)
- Created `DXVorticeCompat.cs` (namespace Thetis): `DXRectF` struct (SharpDX.RectangleF-compatible: settable X/Y/Width/Height + Left/Top/Right/Bottom, Inflate, Offset, Contains, implicit→RawRectF) + `DrawText`/`DrawBitmap` extension methods; added to Thetis.csproj Compile items
- Ported MeterManager.cs DXRenderer (~10K lines) SharpDX→Vortice via bulk regex transform + manual rewrites of dxInit/dxRender/ShutdownDX/bitmapFromSystemBitmap/resizeDX/buildDXFonts
- bitmapFromSystemBitmap rewritten dropping WIC entirely: GDI+ LockBits Format32bppPArgb → CreateBitmap(SizeI, scan0, pitch, BitmapProperties); stream cache preserved
- Key API findings (verified by reflection dumps in %TEMP%\opencode\vorticedump\):
  - `RawRectF.Left/Top/Right/Bottom` are readonly FIELDS — assign whole struct, never mutate
  - `Matrix3x2.Translation` is Vector2; no TranslationVector property
  - No `ID2D1Factory.CreateRenderTarget` — use `CreateDxgiSurfaceRenderTarget(IDXGISurface, RenderTargetProperties)`
  - `IDXGIFactory2.CreateSwapChainForHwnd` returns the swap chain (no out param); QI from factory1
  - `Vortice.Direct2D1.FeatureLevel` also exists — bare `FeatureLevel` is ambiguous with Direct3D's; alias required
  - `EndDraw()` returns Result; RecreateTarget retry loop preserved (≤10 attempts)
- Fixed Thetis.csproj PostBuild: Exec+cmd copy broke under .NET SDK (cmd can't resolve literal `..` segments in absolute paths; trailing `\";` quoting fragile). Replaced with MSBuild `<Copy>` tasks and corrected lib path (`..\..\lib`, lib lives at Project Files\lib not repo-root\lib)
- Build now succeeds x64 Release. Next: port display.cs (~322 refs), then remove SharpDX packages

### 2026-08-20 (display.cs Vortice port — Phase 2 code complete)
- Ported display.cs (~12.7K lines) via bulk transform script (`%TEMP%\opencode\transform_display.ps1`) + manual rewrites of initDX2D/ShutdownDX2D/DX2Adaptors/getGPUNameInUse/resizeDX2D/pauseDisplay/ResetWaterfallBmp(2)/SDXBitmapFromSysBitmap/buildFontsDX2D/present section
- SDXBitmapFromSysBitmap rewritten dropping WIC/DataStream: GDI+ LockBits Format32bppPArgb → CreateBitmap(SizeI, scan0, |stride|, BitmapProperties); old per-pixel loop was a byte-order no-op
- pauseDisplay: staging texture via `_device.CreateTexture2D(desc)`; snapshot via `CreateSharedBitmap(dxgiSurface, props)` (Vortice has NO CreateBitmapFromDxgiSurface on render targets)
- Additional API findings:
  - `Vortice.Direct2D1` has NO AlphaMode — it's `Vortice.DCommon.AlphaMode`; bare `FeatureLevel`/`FactoryType`/`TextAntialiasMode` ambiguous between Direct2D1/Direct3D(DirectWrite) namespaces → qualify or alias
  - Legacy `SwapChainDescription` fields: `BufferDescription`, `BufferUsage`, `OutputWindow` (IntPtr), `Windowed` (not SharpDX's ModeDescription/Usage/OutputHandle/IsWindowed); BufferCount is uint
  - `ModeDescription(uint, uint, Rational(uint,uint), Format)`; enums are `ModeScanlineOrder`/`ModeScaling` (not DisplayMode*)
  - `IDXGIFactory2.CreateSwapChain(device, desc)` by value; `GetBuffer<T>` not GetBackBuffer; `ID2D1RenderTarget.Dpi` returns Vortice.Mathematics.Size; `Texture2DDescription.CPUAccessFlags` (capital CPU)
  - `CppObject.IsDisposed` is protected in Vortice — drop the checks
  - Vortice.Mathematics.Color4 has R/G/B/A props (not Red/Green/Blue)
  - **Vortice.Mathematics.Rect ctor is (left, top, right, bottom)** — SharpDX RectangleF was (x,y,w,h); DXRectF implicit→Rect extension fixed accordingly
  - Do NOT add implicit DXRectF→Rect conversion: makes DrawRectangle(Rect vs RawRectF overloads) ambiguous CS0121; use explicit DrawText extension overloads instead
  - D3D11CreateDevice(adapter overload) requires adapter typed as IDXGIAdapter (not IDXGIAdapter1) for overload resolution
- Removed all 6 SharpDX PackageReferences from Thetis.csproj; solution builds clean x64 Release with zero NU1701 warnings
- Also converted RawInput.csproj + Midi2Cat.csproj PostBuild from Exec+cmd copy to MakeDir/Copy tasks (same cmd `..` path bug surfaced when building full .sln with VS MSBuild.exe; dotnet CLI had masked it). Full-solution build via VS MSBuild.exe: 0 errors, 6 pre-existing warnings (MSB8012 C++ TargetPath mismatches, MSB3277 test-assembly version conflicts)
- Note: `dotnet build` cannot build the C++ vcxproj projects (MSB4278) — use VS MSBuild.exe (`C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`) for full-solution builds
- Phase 2 remaining: runtime verification of all display modes only

### 2026-08-20 (settings serialization fixes — BinaryFormatter→JSON fallout)
- User-verified: meter settings now round-trip (save→restart→restore) after these fixes
- **GetSetting JsonElement bug fixed** (MeterManager.cs clsIGSettings): System.Text.Json deserializing `ConcurrentDictionary<string, object>` boxes values as `JsonElement`; the old hard cast `(T)_settings[setting]` threw InvalidCastException on every read → RestoreSettings failed at startup ("issue restoring MultiMeter" error). Added `ConvertLegacyJsonValue(object, Type)` which unboxes JsonElement per target type (int/uint/long/float/double/bool/string/Color/Guid/enums/string[]/Guid[], generic JSON fallback otherwise); wired into both GetSetting overloads
- **Version "2|" typed blob format for IG settings**: ToString2 now emits `"2|"` + base64(JSON of List<SettingBlobEntry>{k,t,v}) where t is a compact type token (int/float/bool/color/guid/fontstyle/reading/barstyle/units/strings/guids, or `aq:<AssemblyQualifiedName>` fallback) so Color/Guid/enums survive the JSON round trip; TryParse2 materializes values eagerly. Version "1|" still parsed (lazy conversion via ConvertLegacyJsonValue)
- **Regenerated `_default_settings`** (database.cs:11387): was a BinaryFormatter payload decoded only during ImportAndMergeDatabase State-table merge — silently failing (`catch {}`) since the serializer switch. Decoded old blob via PS 5.1 (.NET Framework) BinaryFormatter → re-encoded as JSON+gzip+base64 matching Common.SerializeToBase64; 932 entries preserved
- **Regenerated `Resources\cty.txt`** (country/prefix data): was also a stale BinaryFormatter payload (`Thetis, Version=2.10.3.13` reference) → clsCountryData static ctor silently failed → all DXCC/prefix lookups dead. Decoded with SerializationBinder shim redirecting Thetis.CountryData+PrefixData to a local type; re-encoded as JSON; 346 entries / prefix lists intact. File rewritten BOM-less UTF8
- Discord token blob: fails gracefully (bot just doesn't connect, user re-logs in). MultiMeterIO connections from pre-upgrade DBs: warning box shown, one-time loss accepted (no legacy BinaryFormatter reader shipped — disabled/risky on modern .NET)
- Upgrade path summary: DataSet XML structure auto-upgrades (VerifyTables/VerifyTXProfileColumns); TX profiles import cleanly via ExpandOldTxProfileTable; serialized blobs from .NET 4.8 era are lost once with warnings
- Regen script kept at %TEMP%\opencode\regen_blobs.ps1 (PS 5.1 required for BinaryFormatter decode; note: single-quoted here-string needed so PS doesn't eat the backtick in List`1)

### 2026-08-20 (runtime verification + UI text/branding fixes)
- User runtime testing confirmed:
  - Meter settings round-trip verified ("meters make the round trip now")
  - Old (.NET 4.8-era) database imported via DB Manager, made active, restart auto-upgraded DB version as expected
  - During that test the MultiMeterIO warning appeared but meter settings were intact — explained: Options-table merge special-cases meter keys (`meterContData_*`/`meterData_*`/`meterIGData_*`/`meterIGSettings_*`, database.cs ~11167–11230): current-in-use rows win, old DB only fills missing keys. The warning was `multimeter_io2` only — a regular key where old wins the merge — holding an unreadable BinaryFormatter blob (the accepted one-time loss)
  - Stuck meter container that couldn't be deleted was simply LOCKED (`chkLockContainer` disables btnContainerDelete, setup.cs ~32975) — not a bug
- Upgrade-path clarification: data dir is fixed (`%APPDATA%\OpenHPSDR\SDR-VST3-x64\`, console.cs ~692) so installing a new build over an old SDR-VST3 upgrades the same database in place (no manual import). Pre-serializer-switch installs get the same one-time blob loss; post-f82ce68 installs are seamless (their `"1|"` blobs restore correctly thanks to ConvertLegacyJsonValue and get re-saved as `"2|"`)
- UI text changes (all rebuilt clean):
  - setup.cs:2197 MultiMeterIO warning reworded per user: "This version of SDR-VST3 requires restoring the settings for MultiMeterIO.\n\nAny existing meter input/output connections (UDP, TCP/IP or serial) will need to be removed and re-added."
  - setup.cs:2203 "restart Thetis" → "restart SDR-VST3"
  - clsDBMan.cs:477 DB-upgrade prompt "...of Thetis requires your database..." → "...of SDR-VST3..."
  - clsDBMan.cs:510 "sucessfully" → "successfully"; clsDBMan.cs:1419 same typo + "Thetis will now restart" → "SDR-VST3 will now restart"
  - clsDBMan.cs:964 activate-database prompt "cause Thetis to restart" → "cause SDR-VST3 to restart"
  - clsDBMan.cs:1736/1785/1787 export filename defaults `Thetis_database_export_*` → `SDR-VST3_database_export_*`
- Database Manager is now fully rebranded (no remaining user-facing "Thetis" strings in clsDBMan.cs)
- OPEN DECISION — broader rebrand sweep paused: ~96 "Thetis" strings remain codebase-wide, categorized:
  - Bucket A (~40, safe): dialog texts/tooltips/window titles ("Thetis DirectX" titles in display.cs/MeterManager.cs, "Thetis Meter [#####]" title, "Thetis Startup Log", shutdown splash, console.cs DB-misconfig dialogs, radio.cs wisdom prompt, setup.cs radio-model dialog, firewall/admin prompts, Midi2Cat startup messages, setup tooltips)
  - Bucket B (functional, risky): exe still named Thetis.exe (AssemblyName=Thetis — firewall rules/shortcuts/installers reference it); TCI/CAT protocol strings (`sendPongFrame("Thetis")`, `"#Thetis TCP/IP Cat"`); N1MM defaults `Thetis_1/2`; VST editor window class; embedded resource names tied to RootNamespace
  - Bucket C (keep): upstream GitHub URLs (version.json/discord.json/skin_servers.json/manuals live in ramdor/Thetis repos), About-box credits
  - Note: `dotnet build` handles the 5 C# projects only; the 5 native vcxprojs always require VS MSBuild.exe/toolchain (MSB4278 otherwise). Prebuilt native DLLs would be the way to hide C++ behind dotnet build if ever wanted

### 2026-08-20 (Phase 3 Tier 1 — 3D panadapter visual upgrades, code complete)
All six Tier-1 items implemented in `DrawPanadapter3DHistoryDX2D` (display.cs) + setup UI; builds clean x64 Release (0 errors, only pre-existing CA1416 noise). Runtime verification pending.
- **Temporal interpolation**: rows now sample a *fractional* frame index `fIdx = line - phase` where `phase = (now - _3dLastPushTicks)/interval` (same interval calc as the push throttle). Lerps between adjacent ring frames into cached `_3dLerpRows[line][]`; content is continuous across push boundaries so the surface morphs at display rate instead of jumping at ~25 FPS. Zero extra draw calls (one lerp pass per row).
- **Edge smoothing**: ridge outline pass (PASS 2) wrapped in `AntialiasMode.PerPrimitive` (save/restore around pass); aliased axis-aligned clip kept. Kills the staircase silhouette on converging edges.
- **Side walls / end caps** (`_pan3DSideWalls`, default ON): per side, `ID2D1PathGeometry` filled with front-row edge-colour darkened ×0.32 + mid-depth fog blend; polygon = edge-trace polyline → back bottom → front bottom → close (walls extend to absolute bottomY to match column fills). Drawn under PerPrimitive AA before row fills so rows occlude correctly. Uses `_d2dFactory.CreatePathGeometry()` + sink pattern from MeterManager.
- **Perceptual colormaps** (`Pan3DColorMap`: 0=Classic/1=Turbo/2=Viridis/3=Inferno): lazy-built static LUT `_colormapLUT` (3×256×RGB bytes); Turbo+Viridis via published polynomial approximations, Inferno via 11-stop matplotlib landmark table with linear interp. When ≠0 it overrides waterfall-sync/gradient/line-colour branches; outline brightening still applies. Shared colour selection factored into local function `SelectSurfaceColour(dBm, strength, out RGB)` used by fills, outlines and walls.
- **Perspective grid floor**: 6 receding gridlines along smoothstep baselines (alpha 0.10→0.03, line colour) + 2 straight side rails (insets/baselines are linear in tSmooth ⇒ rails are single segments); drawn first so surface occludes.
- **Exponential fog**: `haze = strength * 0.35 * (1 - exp(-2.5·tSmooth))` replaces linear `t*strength*0.35` — saturating, more natural mid-depth falloff; applied consistently to fills/outlines/walls via `FogFor(tS)`.
- **Setup UI** (grp3DPanadapter, tpDisplayGeneral): added `chk3DSideWalls` ("Side Walls") + `lbl3DColorMap`/`combo3DColorMap` (DropDownList: Classic/Turbo/Viridis/Inferno); group regrown 244→255 px (fits tab, nothing below), existing controls repositioned to 17–18px pitch. Wired: init defaults block, end-of-init Display push block, `needsRecovering` entries, `chk3DSideWalls_CheckedChanged`, `combo3DColorMap_SelectedIndexChanged`, `btn3DResetDefaults_Click`. Persistence is free via Common.SaveForm/RestoreForm (control-name keyed).
- Note: colormap combo restores by Text via RestoreForm — item strings must stay "Classic/Turbo/Viridis/Inferno".
- **Fix (same day): mid-frame colormap race** — `SelectSurfaceColour` snapshotted `useColormap` at frame start but indexed `_colormapLUT` with the *live* `_pan3DColorMap`; switching to Classic mid-frame produced a negative LUT offset → `IndexOutOfRangeException` every frame (freeze + error spam). Now the map index is snapshotted once per frame (`colorMapIdx`) and used for both the branch and the offset.
- **Fix (same day): live trace now follows colormaps** — the 2D live trace/fill drawn on top of the 3D stack only knew waterfall-sync/plain line colour, so Turbo/Viridis/Inferno never recoloured it. Added a `liveUseColormap` branch in the panadapter live-trace loop (display.cs ~5437): when 3D is enabled and a colormap is active it overrides waterfall-sync and colours line (alpha 1.0) + fill (alpha 0.55) from the same LUT with the same `(dBm - grid_min)/yRange` strength mapping as the surface, so the live trace blends into the front row. Shares the per-frame `liveWfBrushCache` (key includes alpha bits; no collisions).
- **Fix (same day): hard crash (process exit) when Waterfall Sync unchecked** — WER/event log showed `c0000005` AV inside `Win32.memcpy` ← `DrawPanadapterDX2D`. Root cause: the DX2 device-setup path (display.cs ~3700, runs on rebuilds: resize/DPI/device-loss/skin) reallocated `_3dHistoryBuffer`/`_3dMedianPrev` in place with `float[1]` slots while the display thread pushed history; `fixed` re-reads the field after the length guard, so it could pin a fresh 4-byte slot and memcpy ~65KB into it. With Waterfall Sync ON pushes are waterfall-paced (race window tiny); unchecking sync switches pushes to `_3dPushIntervalTicks` (40ms ≈ every frame), multiplying exposure — hence "uncheck → crash". Fix: push block and draw method now snapshot the ring arrays + head/count into locals once (stale-ref use is harmless; guard and pin always see the same buffers), and the setup path builds new arrays locally and publishes with a single atomic reference assignment. Note: corrupted-state exceptions are uncatchable in .NET 10 (SYSLIB0032), so memory-safety was the only cure.
- **UPDATE: crash persisted after the snapshot fix** (identical coreclr fault offset across 8+ crashes; user repro: always dies on unchecking Waterfall Sync, any path). Diagnostic build: ALL `Win32.memcpy` calls in the panadapter/waterfall render paths converted to bounds-checked managed `Array.Copy` (panadapter RX1 data copies ~5171-5181, 3D push block ~5185-5260, waterfall RX1/RX2 data copies ~7520-7560) and the push block is wrapped in try/catch that logs buffer lengths to ErrorLog.txt via `Common.LogString`/`LogException` and skips the push instead of dying. Interpretation guide for next test run: crash gone → one of those memcpys really was the culprit; crash gone + ErrorLog entries → logs name the bad lengths; crash persists with same signature → stack attribution is misleading, corruption originates elsewhere (suspect unmanaged DSP core or D2D interplay), next step is converting remaining memcpys (display.cs ~11125+, ~11797+) and/or capturing a real dump (`dotnet-dump collect -p <pid>` at the crash dialog / procdump -e).
- **UPDATE 2 (end of day): memcpy theory ELIMINATED — dump captured, analysis pending.** User reproduced 3× on the Array.Copy build (process starts 23:02:38 / 23:03:32 / 23:05:04, exe written 23:02:07): identical signature (`c0000005`, coreclr.dll 10.0.1026.32716 offset `0x35967f`), and ErrorLog.txt stayed EMPTY → push-block try/catch never fired, none of the converted copies is the fault. Key deduction: a genuine msvcrt `memcpy` overrun would fault inside msvcrt.dll/ucrtbase.dll — but the faulting module is **coreclr.dll at a byte-identical offset every time**. Fixed-offset AV inside coreclr during managed execution = classic symptom of managed heap corruption discovered by GC/runtime helpers (or smashed thread frame chain), with the crash site unrelated to the corrupter. The "Win32.memcpy ← DrawPanadapterDX2D" frames in the .NET Runtime event log are an artifact of the CLR's post-mortem stack walk — do not chase them further.
  - **Dump capture armed**: WER LocalDumps registered for Thetis.exe under `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\Thetis.exe` (DumpFolder=`C:\Users\W4YNY\Documents\thetisvst\dumps`, DumpType=2 full, DumpCount=10; needed one UAC elevation). First catch: `dumps\Thetis.exe.21132.dmp` (2.1 GB, 23:10:11). REMOVE this registry key once debugging is done.
  - **Tooling note**: `dotnet tool list` claimed dotnet-dump was installed but the shim exe was missing from `~\.dotnet\tools`; fixed by uninstall+reinstall (`dotnet-dump` 9.0.661903). Run as: `& "$env:USERPROFILE\.dotnet\tools\dotnet-dump.exe" analyze dumps\Thetis.exe.21132.dmp -c "<cmd>"`.
  - **Analysis status**: only `pe` ran so far → "no current managed exception on this thread" (default thread isn't the faulting one). Next commands: `threads` to enumerate, find faulting thread (WER AV dumps: look for the thread whose native context matches exception record; try `clrstack` per candidate), then `verifyheap` (THE heap-corruption confirmation), `ip2md`/`u` near faulting IP if SOS can bind it, `dumpheap -stat` for anomalies.
  - **Leads to check while analyzing** (gathered from code reading):
    - Data pump console.cs ~24230-24340: `SpecHPSDRDLL.GetPixels(id, which, pinned ptr, ref flag)` — NATIVE code writes into managed pooled arrays (`Display.new_display_data`, `new_waterfall_data`, +bottom variants) through raw pointers. The pump thread does NOT hold `_objDX2Lock`.
    - `initDisplayArrays` (display.cs ~3000-3050, UI thread, under `_objDX2Lock`) `Return`s those same arrays to `ArrayPool.Shared` and re-`Rent`s — race vs GetPixels can double-rent/live-resize arrays the native side still writes. Worse: **waterfall arrays are rented `Rent(W)` (current width), panadapter ones `Rent(BUFFER_SIZE)`** — if the native side's configured pixel count exceeds current W after a shrink, GetPixels overruns the array end → exactly the corruption pattern suspected.
    - `GetPixels` DllImport declaration NOT yet located (grep missed it — maybe unusual formatting/generated); find it to see which native DLL and exact signature. Also `clsSpectrumProcessor.cs:878` calls GetPixels into `_workingPixels` sized `_pixels` — check that sizing path too.
    - Repro detail: always dies on unchecking Waterfall Sync (any path); often on the SECOND reset-defaults→uncheck cycle. Uncheck ⇒ 3D history pushes go from waterfall-paced to every 40 ms ⇒ allocation/GC cadence spikes ⇒ corrupted heap gets touched sooner. Treat "uncheck" as an accelerant, not necessarily the cause.
    - **STATUS 2026-08-21: the GetPixels/ArrayPool lead above is DEPRIORITIZED but not fully disproven** — `verifyheap` came back clean and the crash is a fail-fast on a native AV inside D2D calls (see 2026-08-21 session entry for the proven mechanism). The initDisplayArrays rent/return race is still real hygiene debt worth fixing separately, just unlikely to be THIS signature.

### 2026-08-20 (3D panadapter UI decluttered — settings moved to popup window)
User request: setup tab was too cluttered. The 3D group box is gone; the display tab now has only `chkDisplay3DPanadapter` ("Enabled", at 394,150) + `btn3DSettings` ("3D Settings...", at 394,172) directly on `tpDisplayGeneral`. Builds clean x64 Release (0 errors).
- **New file `frm3DPanadapter.cs`** (single-file form, no .Designer.cs, added to Thetis.csproj as `<Compile><SubType>Form</SubType>`): owns ALL 3D settings controls — same control names as before (`chk3DWaterfallSync`, `chk3DSideWalls`, `ud3DXOffset`=Perspective, `ud3DYOffset`=Depth, `ud3DRidgeHeight`, `ud3DHaze`, `ud3DLineCount`, `ud3DSpeed`, `clrbtn3DLineColor`, `combo3DColorMap`, `btn3DResetDefaults`). FixedSingle non-resizable, ShowInTaskbar=false.
- **Lifecycle pattern** copied from frmCFCConfig: ctor does `RestoreForm(this, "3DPanadapter", false)` + `ForceFormOnScreen` inside an `_initializing` guard, then one `PushAllSettings()` to Display.*; FormClosing always `SaveForm(this, "3DPanadapter")`, and on UserClosing hides+cancels for reuse. Setup keeps a lazy singleton (`_frm3DPanadapter`) shown modeless with `Show(this)` — owner-close passes through (only UserClosing is cancelled).
- **setup.designer.cs**: grp3DPanadapter + all child instantiations/config blocks/SuspendLayout/EndInit/field declarations removed (~350 lines spliced); chkDisplay3DPanadapter repositioned onto the tab; btn3DSettings added (TabIndex 95/96).
- **setup.cs**: init-defaults block, end-of-init push block and needsRecovering entries reduced to just chkDisplay3DPanadapter/Pan3DEnabled; all moved handlers deleted; `chkDisplay3DPanadapter_CheckedChanged` simplified (no longer forces waterfall-sync — that control lives in the popup now, default ON there). Stale-DB cleanup extended: all 10 moved control names are purged from the "Options" table via `_oldSettings` (foreach, NOT a lambda — `getDict` is a ref param and CS1628 forbids capturing it).
- **Persistence note**: settings now save under DB table "3DPanadapter" instead of "Options"; first launch after this change falls back to designer defaults (identical values), then persists normally. Colormap combo still restores by Text — item strings must stay "Classic/Turbo/Viridis/Inferno".

### 2026-08-21 (dump fully analyzed; mechanism proven; root-cause theory below was later RETRACTED — see 2026-08-22)
**Bug**: unchecking Waterfall Sync (3D panadapter) kills the process deterministically. Signature byte-identical across 8+ crashes: `c0000005`, `coreclr.dll+0x35967F` (runtime 10.0.1026.32716). **NEW USER CLUE: only crashes with Classic colormap selected; Turbo/Viridis/Inferno never crash.**

Theories ELIMINATED this session:
- ~~memcpy overrun~~ (Array.Copy conversion changed nothing)
- ~~managed heap corruption~~ (`verifyheap`: 776,232 objects, **0 errors**)
- ~~stack overflow~~ (faulting-thread RSP had ~32 KB margin above TEB StackLimit)

**PROVEN MECHANISM** (from dump `dumps\Thetis.exe.21132.dmp`):
- Faulting thread = display thread (OS id 0x1950/6480): `RunDisplay` → `RenderDX2D` → `DrawPanadapterDX2D`, frozen at display.cs:**5610** (a `_d2dRenderTarget.DrawLine` P/Invoke in the live-trace block).
- Capture RIP = `KERNELBASE!RaiseFailFastException+0x188`; `clrthreads` shows the thread carrying bare `System.ExecutionEngineException` (HRESULT 80131506, no message/stack = preallocated fail-fast sentinel).
- ExceptionAddress `coreclr+0x35967F` resolves (with real PDB) to **`ProcessCLRException+0x13F` = exceptionhandling.cpp : line 621** = `EEPOLICY_HANDLE_FATAL_ERROR(pExceptionRecord->ExceptionCode)` inside the `IsProcessCorruptedStateException(...)` branch (first/searching pass).
- ⇒ A **native access violation fired during D2D COM calls**; the CLR personality routine classifies c0000005 as corrupted-process-state and **fail-fasts without ever running managed catch blocks**. This is why ErrorLog.txt stayed empty — try/catch around the push block was never going to fire.
- Old event-log "Win32.memcpy ← DrawPanadapterDX2D" frames were CLR post-mortem stack-walk artifacts. Dead end, confirmed.

**~~ROOT CAUSE — render/lifecycle RACE on `_d2dRenderTarget`~~ — RETRACTED 2026-08-22:**
- ~~The render loop never acquires `_objDX2Lock`...~~ **WRONG**: `RenderDX2D` takes `lock (_objDX2Lock)` at display.cs:**3998** and holds it around its ENTIRE frame body (3998–4406). The original grep looked for lock sites *inside* lines 4000–8700 and missed that the lock at 3998 encloses the region. All locked lifecycle paths are therefore mutually exclusive with the frame — no dispose-during-frame race through this lock exists.
- The "why Classic-only" timing theory (slow frames widen race window) is also dead. The real explanation is branch selection — see 2026-08-22 entry.
- Everything else in this entry (fail-fast mechanism, dump toolchain) remains valid.

**Why Classic-colormap-only** *(superseded)*: Classic colours by *continuous* brightness × line colour × alpha → per-frame `brushCache` misses nearly every column → thousands of `CreateSolidColorBrush` COM calls/frame → very slow frames → **huge race window**. LUT colormaps quantize to ≤256×alpha keys → tiny brush set → fast frames → microscopic window. Colormap affects *timing*, not correctness.

**Dump-analysis toolchain notes (reusable)**:
- dbghelp-from-PowerShell works, BUT plain System32 dbghelp has NO working symsrv (SRV* paths silently resolve exports only). Workaround that WORKS: extract CV record (GUID+age) from dump module list (module stream entry +76/+80), download PDB via `https://msdl.microsoft.com/download/symbols/<pdb>/<GUID-N><ageHex>/<pdb>`, then co-locate **copy of matching DLL + PDB in one flat dir** (`%TEMP%\symtest\`) and `SymInitialize(hp, thatDir, false)` + `SymLoadModuleEx` pointing at the copied DLL.
- **MUST match the exact runtime build**: machine has many runtimes; dump used **10.0.1026.32716 = installed folder `10.0.10`** (`C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.10\coreclr.dll`, GUID `{53bf947e-e5ca-4d92-bd36-51f1e7e1dda8}` age 1 — verified equal to local file). Wrong-version DLL gives err=487 or nonsense names.
- Line numbers work: `SymGetLineFromAddrW64` with `IMAGEHLP_LINEW64.SizeOfStruct=40`; FileName is WIDE → `PtrToStringUni` (Ansi truncates to "D").
- Scripts kept in `%TEMP%\opencode\`: `get_pdb.ps1` (CV extract+download), `resolve_v3.ps1` (co-location trick), `resolve_v5.ps1` (GUID verify + line info), `scan_threads.ps1` (per-thread graphics-module raw-stack tally), `find_av_record.ps1` (stack scan for EXCEPTION_RECORDs), `find_module.ps1`, `diag_syms.ps1`.
- dotnet-dump quirks: pipe commands + `quit` via stdin; NO `u`/disassembly command; `setthread` wants decimal index or `-t <osid>`; `ip2md` maps JIT heap addresses (P/Invoke IL-stub return addrs like 0x7FF869F730EC report "not in JIT code range" — normal).
- Raw-stack scan of other threads: UI thread idle in `WaitMessage`; five native-only threads show identical faint `d2d1:6 dxgi:2` residue (stale data, DSP/audio threads — not suspects).

**NEXT STEPS** *(superseded — see 2026-08-22 entry)*: ~~identify racing op / pick fix architecture A-B-C~~. Still valid: after fix verified, REMOVE WER LocalDumps key `HKLM\...\LocalDumps\Thetis.exe` and optionally delete dumps\*.dmp (2 GB).

### 2026-08-22 (RACE THEORY RETRACTED — real lead: `m_bDX2_3d_fill_brush` stale after RT recreation)
Session goal was implementing "Option B" (RWLS gate). Pre-edit verification **destroyed the premise** and produced a better lead.

**What was proven this session:**
1. `RenderDX2D` holds `lock (_objDX2Lock)` around its entire frame body — display.cs:**3998** (lock opens) through 4406. The whole Draw* call tree therefore runs under that lock already.
2. Complete lock-site map built (`%TEMP%\opencode\map_locks.ps1`, 53 sites): every lifecycle method IS gated — `initDisplayArrays`:3000, `ResetWaterfallBmp/2`:3203/3258, `ShutdownDX2D`:3352, `getGPUNameInUse`:3495, `initDX2D`:3517, `DXVersion`:3729, `ResetDX2DModeDescription`:3758, `resizeDX2D`:3784+3852, `setupAliasing`:3895+3922, `RenderDX2D`:3998, `SetDX2BackgoundImage`:8744, `buildDX2Resources`:9255, `buildFontsDX2D`:9389, `PurgeBuffers`:12610, `SetSantaGif`:12832, plus ~30 property setters (lines 2072–2765; script mislabels them "OnCentreFrequencyChanged" because property setters don't match its method regex).
3. `releaseDX2Resources`/`releaseFonts` ELIMINATED as unlocked suspects: only called from display.cs 3364/3365 (inside locked ShutdownDX2D), 9259 (buildDX2Resources), 9393 (buildFontsDX2D).
4. Shutdown flow confirmed serialized: console.cs:28234–28237 sets pause flag → `Join(500)` → locked `ShutdownDX2D()`.
5. Lock-free methods touching D2D objects are all render-path helpers invoked under the frame lock (drawLine/drawString wrappers, grids, drawSpots, getSpotFlagBitmap@12978 via drawSpots@12213, etc.) — not cross-thread.

**THE LEAD — repro path fully traced to a branch only Classic+Sync-Off can reach:**
- Checkbox handler does NOTHING but set the bool: frm3DPanadapter.cs:370–373 → `Display.Pan3DWaterfallSync = value` (display.cs:586, plain setter, no side effects). So the crash happens **inside the next rendered frame**, in code that branches differently when sync is off.
- Fill-brush selection in `DrawPanadapterDX2D` (display.cs:5558–5608):
  - `liveUseColormap = _pan3DEnabled && _pan3DColorMap > 0 && rx==1 && !local_mox` (:5455) → Turbo/Viridis/Inferno take this regardless of sync ⇒ never crash ✓
  - `liveUseWaterfallSync = !liveUseColormap && _pan3DEnabled && _pan3DWaterfallSync && rx==1 && !local_mox` (:5460) → Classic + sync-ON takes this ⇒ never crash ✓
  - else `if (draw3DHistory && !local_mox) { if (!m_bUseLinearGradient) { if (m_bDX2_3d_fill_brush == null) { ...create... } activeFillBrush = m_bDX2_3d_fill_brush; } }` → **Classic + sync-OFF is the ONLY combination reaching `m_bDX2_3d_fill_brush`** ⇒ exactly the crashing config ✓✓
- Prime suspect: `m_bDX2_3d_fill_brush` is a static `ID2D1LinearGradientBrush` added by OUR Tier-1 work ("vertical gradient: bright near trace ~55% alpha → ~16%", created lazily at :5590–5605 via `CreateGradientStopCollection` + `CreateLinearGradientBrush`). D2D device-dependent resources are **render-target-bound**: if `_d2dRenderTarget` gets recreated (`resizeDX2D` on window resize / `RecreateTarget` retry :4361–4369 / DeviceRemoved retry :4384–4390) while this brush isn't disposed+nulled alongside the other `m_bDX2_*` brushes in `releaseDX2Resources()`, the stale brush survives and the next sync-OFF classic frame calls `DrawLine(staleBrush)` (:5610) → native AV inside d2d1 → CLR fail-fast. Crash IP frozen at exactly that DrawLine fits.
- Fits "often crashes on SECOND toggle cycle": brush must exist BEFORE an RT recreation to become stale (created during an earlier sync-off period; recreation meanwhile; uncheck again → draw with stale brush).
- Secondary hygiene note: the `stopsColl` from `CreateGradientStopCollection` (:5598) is never released — leak, harmless to the crash but fix alongside.

**NEXT STEPS (implement immediately):**
1. Grep all references to `m_bDX2_3d_fill_brush`; read `releaseDX2Resources()` (9092–9252) to confirm it's missing from the dispose list.
2. Fix: dispose + null `m_bDX2_3d_fill_brush` (and release `stopsColl`) everywhere the other `m_bDX2_*` brushes are released; if `resizeDX2D` recreates the RT without calling `releaseDX2Resources`, null the brush there too.
3. Rebuild, user repros (classic colormap, uncheck Waterfall Sync, resize between toggles), confirm no crash.
4. Then cleanup: REMOVE WER LocalDumps key `HKLM\...\LocalDumps\Thetis.exe`; optionally delete dumps\*.dmp (2 GB).

### 2026-08-22 (BLOCKER FIXED — real root cause: dispose-without-null in `Pan3DLineColor` setter)
Followed the NEXT STEPS above; findings overturned the RT-recreation theory and identified the true bug.

**What was found:**
1. `releaseDX2Resources()` was NEVER missing the brush — display.cs:9117 disposes it, :9183 nulls it. That theory is dead.
2. **THE BUG — display.cs `Pan3DLineColor` setter (~line 600)**: `_pan3DLineColor = value; if (m_bDX2_3d_fill_brush != null) m_bDX2_3d_fill_brush?.Dispose();` — disposes WITHOUT nulling. The lazy-create guard at :5595 (`if (m_bDX2_3d_fill_brush == null)`) then skips recreation, and the next Classic+Sync-OFF frame assigns a DISPOSED COM brush as `activeFillBrush` → `DrawLine(...)` at :5610 → native AV inside d2d1 → CLR fail-fast (c0000005). Exactly the frozen-RIP signature from the dump.
3. **Repro chain now fully explains every observed symptom**:
   - Only Classic crashes: LUT colormaps take the `liveUseColormap` branch and never touch this brush; waterfall-sync takes its own branch. Classic + sync-OFF is the only path that uses it.
   - Often the SECOND reset-defaults→uncheck cycle: `PushAllSettings()` (frm3DPanadapter.cs:355, called from ctor AND btn3DResetDefaults_Click) fires the setter every time. First cycle: brush still null → harmless no-op; uncheck creates fresh brush. Second cycle: setter disposes-but-leaves-non-null → uncheck → next frame draws through dead pointer.
   - "Uncheck" itself is not causal — any setter call after the brush exists arms the bomb; unchecking sync just routes rendering into the poisoned branch.
4. `stopsColl` leak confirmed (handoff hygiene note): `CreateGradientStopCollection` result never released after `CreateLinearGradientBrush`.
5. Sweep for same bug class (`if (x != null) x?.Dispose();` without null) across display.cs + MeterManager.cs: all other sites live inside bulk-release methods that unconditionally null every field afterwards, or are locals. No other instances.
6. resizeDX2D recreates `_d2dRenderTarget` without releasing static brushes — upstream-by-design (same D3D device domain; all other m_bDX2_* brushes survive resizes in practice), left untouched.

**Fix applied (display.cs):**
- Setter: dispose + set `m_bDX2_3d_fill_brush = null`
- Creation site: `stopsColl.Dispose()` after brush creation

**Brush-cache audit (user asked to confirm the anti-churn fixes survived the Vortice port — they did):**
| Path | Mechanism | Status |
|---|---|---|
| 3D surface fills/outlines/walls/grid floor | per-frame `brushCache` Dictionary<int,ID2D1SolidColorBrush>, disposed in `finally` (~:6548) | ✓ intact |
| Live trace colormap/waterfall-sync | `liveWfBrushCache`, cleared every frame (:6037–6041) | ✓ intact |
| Classic fill gradient | lazy singleton `m_bDX2_3d_fill_brush` (now correctly invalidated) | ✓ fixed |
| General colour brushes | `_DX2Brushes` / `_DXBrushes` keyed caches via getDXBrushForColour, cleared by clearAllDynamicBrushes() in both files' release paths | ✓ intact |
| RX/TX data gradients | built-once with `_bRebuild*LinearGradBrush` flags | ✓ intact |

No uncached per-column/per-row `CreateSolidColorBrush` remains in any hot loop. Note: Classic surface mode still generates many distinct colours per frame (continuous brightness × dim × haze quantised to int RGB keys) so its cache hit-rate is inherently lower than LUT colormaps — known/accepted since Tier 1; LUT colormaps (Turbo/Viridis/Inferno) remain the fast path.

Build clean x64 Release (0 errors, pre-existing CA1416/MSB8012 warnings only).

**NEXT:** user repro test (Classic colormap → open/close 3D settings popup + Reset Defaults between cycles → uncheck Waterfall Sync, plus window resizes between toggles). WER LocalDumps stays armed until verified, then remove `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\Thetis.exe` + delete dumps\*.dmp (2 GB).

### 2026-08-22 (fix VERIFIED by user; cleanup deferred)
- User tested the fixed build: "seems its fixed so far" — no crash on uncheck-Waterfall-Sync. Blocker considered resolved (watch for regressions during normal use).
- **Deferred cleanup (user choice — keep for now):** WER LocalDumps key `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\Thetis.exe` remains armed; `dumps\Thetis.exe.21132.dmp` + `dumps\Thetis.exe.3532.dmp` (~4.3 GB) retained for future analysis if anything resurfaces. Remove both once confident.
- Phase 2 runtime verification status: display modes working in daily use (2D pan, waterfall, meters, 3D pan with Tier-1 upgrades). Remaining Phase 2/3 checklist items are enhancements, not defects.
- NOTE: large uncommitted working tree on `net10-migration` (Phase 2 Vortice port of display.cs/MeterManager.cs, DXVorticeCompat.cs + frm3DPanadapter.cs new files, settings/branding fixes, Tier-1 3D work, this fix) — commit when ready.
- **2026-08-22 follow-up — "transparent panadapter + solid pink waterfall at exactly 28.000 MHz"**: NOT a code bug (user-confirmed). Per-band display scaling exists (spectrum grid min/max + waterfall hi/lo per band, applied on every band change via console.cs `UpdateWaterfallLevelValues` :8864+ and the grid-min/max switches :9247+/:9384+); 28.000 is the exact GEN(B11M)/B10M(HF) table boundary so the swap is instantaneous. 10m Spectrum Grid Max was found at -135 post-upgrade (user readjusted).
  - **New installs SAFE**: modern per-band defaults are in-code field initializers (console.cs ~13950–14600; e.g. grid max/min 10m = -40/-140, waterfall -80/-140); Options loader overrides only when a DB key exists; no `-135` default anywhere in source.
  - **Likely origin**: `_default_settings` blob (database.cs:11387) is ONLY a key-name whitelist used by ImportAndMergeDatabase (Database Manager manual imports, clsDBMan.cs:492/:1411) — values come from the old DB being imported. Ancient-era DBs carry old-era defaults (e.g. -135), which fill keys the user never customized. In-place upgrades without importing an ancient DB are unaffected.
- **2026-08-22 — 3D panadapter live-trace/geometry parity with wdsp2-conversion (f4cca3d)**: user reported Depth slider raised surface "vertically" instead of a flat plane; then live trace looked "much taller than the 3D rear rows, hard visual break, seeing double / blurred". Full comparison vs old branch: geometry math IDENTICAL (smoothstep depth, uniform ridge, dim/haze/alpha formulas verbatim); live trace structure identical (same 55%/16% fill gradient, same draw order: 3D → grid → live on top). Real differences found+fixed:
  1. **Live trace vertical mapping** — root cause of seam/double image: live trace used absolute full-plot-height dBm mapping while every 3D row uses the compressed ridge envelope (strength × frontMaxRidge), so the front overlay towered over its own stack as a second copy. Fix (Aether-style, display.cs ~5483): when `draw3DHistory && !local_mox && _pan3DEnabled && _3dHistoryCount >= 2` (`live3DMapping`), live trace Y = `bottomY - strength * H * Pan3DRidgeHeight` — identical to the 3D front row, making it the front crest of ONE continuous surface; spectral peak-hold Y remapped identically (~5640). Non-3D modes unchanged.
  2. **Depth default mismatch**: new frm3DPanadapter designer + Reset Defaults used 0.40; validated old setup.cs used **0.58**. Both now 0.58 (Display field default was already 0.58).
  3. Reverted an interim ridge-foreshortening experiment (`rowRidge[i] = frontMaxRidge * rowWidthFrac`) — diverged from validated uniform-height reference and worsened the live-vs-rear disparity.
  Note: user's saved values in DB "3DPanadapter" table persist across builds — after testing use "Reset Defaults" for the reference look. Build clean; awaiting visual verification.
- **2026-08-22 — Waterfall Sync priority fix + Aether extras (zCurve floor-lift, slope shading)**: user verified the A+B+C geometry port ("looks much much better"), approved optional extras; also reported that Waterfall Sync checkbox only overrode Classic colour mode, not colormaps. Changes:
  - **Colour priority flipped everywhere** (display.cs ~5457 live trace + ~6160 surface): now Waterfall Sync > colormap > gradient/line-colour; colormap LUT gating moved after the wf-range validity check so an invalid waterfall range falls back to colormap cleanly. Popup tooltips updated to match.
  - **Floor Lift knob (D)**: new `_pan3DZCurve`/`Pan3DZCurve` (default 0.70 = Aether `m_dssZCurve`, clamp 0.05–1.0); height uses `pow(strength, zCurve)` (local fn `Lift`) applied to fills, ridge outline, edge walls AND the live-trace/peak-hold remap (`live3DZCurve`) so the seam stays closed; strength anchor remains per-band grid_min (≈ user's noise-floor reference). Colour still keyed to raw dBm/strength (Aether parity). New "Floor Lift" row added to frm3DPanadapter (ud3DZCurve, y=247, Reset button moved to y=285, form height 298→323; persists via Common.RestoreForm by control name like its siblings).
  - **Slope shading (E)**: fill pass brightness follows ridge steepness exactly as DssRenderer.cpp:851-858 — `shade = clamp(1 + 0.55×(lift[c-1]−lift[c+1]), 0.68, 1.32)`, quantized to 0.05 steps to keep the per-frame brush cache bounded; applied after dim, before haze; outline rim deliberately unshaded (Aether parity).
  -   Perf note: one pow per column per row via shared scratch `_3dRowLift[]` (fill+outline reuse); live trace pays nDecimatedWidth pows/frame. Build clean; awaiting user look. Remaining unported Aether bits (low priority): temporal IIR kTemporalAlpha=0.60 + median-of-3 impulse rejection; row density 96×768 vs our default 35; kColorSpanDb=45 palette span.
- **2026-08-22 — committed `290be6b`** (Aether geometry port + seam fix + priority fix + extras) after user verified ("wow looks so much better"). Handoff updated same commit.
- **2026-08-22 — "3D Pan" button now skin-image styled; USER VERIFIED WORKING (`4dc72d7`+`0216c12`+`2deba89`, caption `9bf6dbb`)**: v2 button first rendered as a plain white checkbox-looking box because the Thetis skin system paints button-appearance checkboxes from per-control PNG sets in the active skin folder (8 `ImageState`s: NormalUp/NormalDown/Disabled*/Focused*/MouseOver*; SDRVST3 skin ships only `-0`/`-1`). chkTXVST/chkRXVST look right because `SetupCheckBoxImages()` aliases their names to the existing `chkNoiseGate` image set. Fix: alias added in Skin.cs `SetupCheckBoxImages()` (~1003). GOTCHA THAT COST A ROUND-TRIP: the control's runtime `.Name` is **chkDisplay3DPan** (set in console ctor ~759, deliberately matching setup-checkbox naming) while its FIELD is btnDisplay3DPan — first alias attempt used the field name and never matched (`0216c12`), corrected in `2deba89`. Alias by CONTROL NAME. NO inversion needed in `CheckBox_StateChanged`/`CheckBox_MouseEnter` (TXVST/RXVST invert there because they're bypass toggles; 3D lights on checked normally). Appearance=Button + FlatStyle=Flat + UseVisualStyleBackColor=false match siblings. Skin.Restore walks all controls incl. runtime-added ones, so no extra wiring needed. Caption shortened to just "3D" per user (`9bf6dbb`).
- **2026-08-22 — "3D Pan" toolbar button added (v2, correct placement)**: first attempt used the MW0LGE other-buttons framework (`OtherButtonId.PAN3D`) — reverted (`8622934`); user couldn't see it because framework buttons are gated by per-user saved bitfields (`buttonbox_other_buttons_bitfield_N`), and the "Peak" they meant was the FIXED display toolbar anyway. Final: runtime-created `CheckBoxTS btnDisplay3DPan` ("3D Pan", 50×23) added to `panelDisplay2` at (52,51) — directly below Peak (52,28), right of CTUN (chkFWCATU at 1,51; its caption is 'CTUN' despite the name). Created in console ctor after InitializeComponent; handler sets `Display.Pan3DEnabled` + selected-colour backcolor like AVG/Peak; `console.SyncDisplay3DPanButton()` called from setup's chkDisplay3DPanadapter handler and post-init push (setup.cs ~660) so boot-restore + setup checkbox stay in sync. State persists via the existing setup checkbox persistence.
- **2026-08-22 — startup settings bug fixed (`180f0ea`)**: 3D panadapter came up on compiled-in defaults after restart (user-visible as Waterfall Sync ON until the popup was opened) because frm3DPanadapter is lazy-created and its persisted values only reached the engine via constructor's PushAllSettings on first open. Fix: `Display.RestorePan3DPersisted()` reads DB.GetVars("3DPanadapter") directly (control-name mapping mirroring PushAllSettings; colormap stored as combo text mapped back to index; ColorButton r.g.b.a parsed) and is called in console init next to RestoreForm(EQForm). User verified.
- **2026-08-22 — Floor Lift (Pan3DZCurve) default raised 0.70 → 0.90** (user preference): engine field `display.cs` `_pan3DZCurve`, designer initial value and Reset button in `frm3DPanadapter.cs`. Saved user settings still override via RestoreForm — hit Reset once to adopt the new default.
- **2026-08-22 — cached-surface rendering REVERTED by user preference (`55d865c` reverts `9ac365a`)**: push-rate stepping of rows was visibly worse than smooth display-rate phase sliding, even though the offscreen surface made framerate flat at any Depth Lines count. User verdict: motion quality > framerate headroom — direct drawing with phase interpolation is the keeper; the persistent brush pool stays. If perf ever matters again, `9ac365a` remains in history as a reference implementation (offscreen compatible RT, `_rt3D` indirection, dirty-on-push + 300ms forced refresh, 0-based coordinate rebase via zeroed `nVerticalShift`, premultiplied-alpha Clear for identical translucency, Vortice gotchas: `SDXPixelFormat` alias not bare `PixelFormat`, `Vortice.Mathematics.SizeI`, `.Bitmap` on `ID2D1BitmapRenderTarget`, `EndDraw(out ulong,out ulong)`).
- **2026-08-22 — 3D render perf: silhouette occlusion culling**: user reported FPS dropping fast with Depth Lines >25 (expected — every row painted every column to absolute bottom ≈ linesToDraw×nDecimatedWidth×2 draw calls/frame). Fix: per-column silhouette array (`_3dSilhouette`, reset to bottomY each frame, static scratch): rows draw back-to-front, so a fill whose crest yPx >= silhouette[c] is fully covered by a nearer row → skip colour math AND DrawLine entirely; otherwise draw and lower the silhouette. Outline pass skips crests strictly below silhouette (own-row crest passes since PASS1 set sil == crest). Zero visual change by construction (only removes fully-hidden primitives); biggest savings in flat/noise regions where back rows are almost entirely occluded. Build clean; awaiting user FPS check at 35+ lines. Next perf levers if still needed: persistent cross-frame brush cache (per-frame create/dispose of solid-colour COM brushes is the next-biggest cost), partial-fill drawing (fill only [crest..silhouette] instead of to bottom), per-frame scratch-array hoisting.
- **2026-08-22 — culling REVERTED (wrong for translucent stacking) + persistent brush pool**: user verified FPS improved but "lost all its 3D appearance". Root cause understood: our fills are TRANSLUCENT painter's-algorithm layers — nearer rows' hazed fills + bright crests paint OVER farther rows everywhere below them, and that translucent stacking IS the depth look. Aether's GPU mesh can cull because it renders OPAQUE depth-tested pixels; our style cannot treat "geometrically covered" as "invisible". Reverted all three culling checks + `_3dSilhouette`. Replacement win: **`_3dBrushCache`** — static cross-frame Dictionary<int,ID2D1SolidColorBrush> keyed by packed ARGB (was per-frame create/dispose of thousands of COM brushes); hard cap 16384 entries self-clears; disposed+cleared in `clearAllDynamicBrushes()` alongside `_DX2Brushes`; per-frame `finally` no longer disposes (clip pop retained). Build clean; awaiting user check: 3D look restored + FPS at 35+ lines. If more headroom needed, agreed next lever = offscreen surface rendered at push rate (~25fps) composited per display frame (motion steps instead of sliding).
- **2026-08-22 — Aether DssRenderer geometry port (user-approved A+B+C)**: located actual AetherSDR source (github.com/aethersdr/AetherSDR, src/gui/DssRenderer.{h,cpp}, sparse-cloned to %TEMP%\opencode\aether for reference). Our 4 perspective constants were already exact ports of their `kBackWidthFrac=0.60/kDepthSpanFrac=0.58/kFrontMaxRidgeFrac=0.46/kHaze=0.16`. Ported the per-row math differences: (A) amplitude foreshortening — ridge height × rowWidthFrac per row (`rowRidge[]`, matches DssRenderer.h:185 `strengthHeight * kFrontMaxRidgeFrac * width`) applied to fills/outline/edge walls; safe now that live trace shares front-row mapping; (B) linear depth parametrization `tS = line/(N-1)` replacing smoothstep easing (matches `v = age/rows`); (C) haze now linear full-strength `tS*hazeStrength` replacing halved exponential fog. NOT ported yet (options): zCurve noise-floor-anchored pow(s,zCurve) amplitude lift + kColorSpanDb=45 palette span (would be a new "floor lift" knob); slope shading (kSlopeGain=0.55, shade 0.68–1.32); temporal IIR kTemporalAlpha=0.60 + median-of-3 impulse rejection; row density 96×768 vs our default 35. Build clean; awaiting user look.

### 2026-08-22 (Tier 2 first slice — spectrum glow via D2D effects graph, HW-only)
Build clean x64 Debug+Release (EXIT=0). **STARTUP CRASH FOUND + FIXED same day**: first Release run failed all render-path attempts with `No usable DirectX render path : 0x80070057 (InvalidParameter)` from `CreateBitmapFromDxgiSurface` (display.cs, createDX2DDevice) — dismissing the MessageBox left the app running with no panadapter/waterfall (DX never initialised). Root cause reproduced in a standalone probe: on **FlipDiscard swapchain backbuffers** `CreateBitmapFromDxgiSurface` returns E_INVALIDARG unless the bitmap is created with `D2D1_BITMAP_OPTIONS_CANNOT_DRAW` (surface is owned by the presentation path; dpi/alpha/format were fine — probe matrix showed Target-only FAILS, CannotDraw OK, Target|CannotDraw OK incl. full BeginDraw/EndDraw/Present cycle, null-properties OK which is why the old CreateDxgiSurfaceRenderTarget path worked). Fix applied to BOTH backbuffer creation sites (createDX2DDevice + resizeDX2D): `new BitmapProperties1(fmt Premultiplied, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw)`. Probe project kept at %TEMP%\opencode\d2dprobe for future interop questions.
RUNTIME VERIFIED 2026-08-22 (user: "it is glowing now", colour follows the data-line setting, intensity tracks the alpha slider — "perfect just the way its working now"). Two fixes + one relocation landed after first runtime test:

1. **Glow layer draws silently dropped — missing BeginDraw/EndDraw**: first run showed layer "active" + composite running but NO visible glow. Root cause: trace segments were stroked into `_glowRT` OUTSIDE a Begin/EndDraw pair — D2D drops all commands recorded outside that pair, so the blurred bitmap was always empty. Fix: defer segments during the column loop into `List<(Vector2 a, Vector2 b, ID2D1Brush brush, float width)> glowSegs`, then after the loop `_glowRT.BeginDraw(); Clear(default); replay; EndDraw();` before compositing. (D2D lesson for ALL future offscreen-layer work.)
2. **Final tuning**: GaussianBlur StandardDeviation 12 with 4 blur DrawImage passes (debug overdrive while invisible) reverted to **σ=6 × 2 passes** + sharp pass — user-approved look. Composite log line reports segment count.
3. **UI relocated by user request**: checkbox moved from grpDisplayDriverEngine to **Setup → Appearance → RX Display tab** (`grpAppPanadapter`), text "**Line Glow**", at (9,240) directly under the Data Line colour/alpha + Line Width cluster it visually belongs to (colour = glow hue, alpha slider = intensity). To make room WITHOUT shifting lower rows down, the Active Spectral Peak / Data Line / Line Width rows were slid UP 28px into pre-existing vacancy (y=158/186/214 rhythm), everything below untouched, group size unchanged (264×390). TabIndex 97.

3D mode decision: live crest already glows in 3D mode for free (same column-loop segments); glowing the stacked history surface was prototyped on paper and DECLINED by user ("leave as is i like it") — solid fills would wash out under plain gaussian; true selective bloom needs luminance-threshold custom shaders (not worth it).

**display.cs**:
- Pipeline upgraded from `ID2D1RenderTarget` to the factory1/device/devicecontext chain: `_d2dFactory` (ID2D1Factory) QI→`ID2D1Factory1` → `CreateDevice(IDXGIDevice)` → `_d2dDevice` → `CreateDeviceContext()` → `_d2dDeviceContext`; backbuffer now a real `ID2D1Bitmap` (`_backBufferBitmap`) created via `CreateBitmapFromDxgiSurface`, bound as context `.Target`. `_d2dRenderTarget` field kept and pointed at the SAME object so every existing draw site compiles unchanged. `resizeDX2D` keeps the context alive across ResizeBuffers (only detaches Target + recreates surface/backbuffer); ShutdownDX2D/releasePartialDX2DDevice release device+context+bitmap in dependency order.
- Glow layer (new fields `_glowRT`, `_glowTraceBitmap`, `_glowBlurEffect`, `_glowBlurImage`, `m_bGlowLayerActive`): per-frame in DrawPanadapterDX2D, when enabled the two live-trace stroke sites (`DrawLine previousPoint→point` + ignored-point flush) RECORD segments into a list (no direct draws); after the column loop the segments are replayed into an offscreen `ID2D1BitmapRenderTarget` (`CreateCompatibleRenderTarget`, full target size+1, premultiplied, PerPrimitive AA) inside a mandatory `BeginDraw/EndDraw` pair; fill/peaks/grid/text still draw straight to the main target. Composite (inside the pushed spectrum clip): GaussianBlur effect at σ=6 drawn TWICE via cached `QueryInterface<ID2D1Image>` (Vortice's ID2D1Effect does not implicitly convert to ID2D1Image), then the sharp trace bitmap — all three `CompositeMode.SourceOver`.
- Auto-skip rule 2: `bGlowTrace = Display.SpectrumGlow && RenderPath==Hardware` — zero effect-graph cost on WARP. New public statics `SpectrumGlow` (default true). `releaseGlowLayer()` called from resizeDX2D / ShutdownDX2D / releasePartialDX2DDevice; lazily recreated on size change.
- Vortice 3.8.3 API notes for future work (cost several compile cycles): pixel format type is `Vortice.DCommon.PixelFormat` (aliased here as SDXPixelFormat); `CreateBitmapFromDxgiSurface(surface, BitmapProperties1)`; `CreateCompatibleRenderTarget(SizeF, SizeI, PixelFormat, CompatibleRenderTargetOptions)`; `CreateEffect(Guid)` returns nint → cast `(ID2D1Effect)` with `EffectGuids.GaussianBlur`; `SetInput(0, bmp, true)`; effect properties via raw `SetValueByName("StandardDeviation", PropertyType.Float, BitConverter.GetBytes(f), 4)`; single `DrawImage(image, Vector2?, RawRectF?, InterpolationMode, CompositeMode)` overload; qualify `InterpolationMode` (ambiguous Direct2D/Direct3D).
- NOT covered yet (deliberate scope): DrawSpectrumDX2D (plain Spectrum display mode) has no glow; 3D history surface untouched; blur radius/strength not user-adjustable.

**setup UI**: `chkSpectrumGlow` ("Line Glow") lives in grpAppPanadapter on the **Appearance → RX Display tab** at (9,240) under the data-line cluster; default checked, tooltip notes HW-only/auto-ignore on WARP + follows data-line colour/width; session-only like its siblings (no Options persistence in this group). Handler pushes `Display.SpectrumGlow` live (no restart needed); registered in ForceAllEvents next to chkForceCPURendering.

**MIGRATION FIX (unrelated but blocking)**: Thetis.Tests referenced `..\..\bin\x64\Debug\Thetis.exe`. Post-migration that exe is a native apphost stub (managed assembly is Thetis.dll) — tests silently compiled against a PRE-migration managed exe until today's rebuild replaced it, then failed CS0103/CS0246 ('VstHost', 'VstPluginCatalogFile', 'VstChainState'). HintPath switched to Thetis.dll; full solution incl. tests builds clean again.

### 2026-08-22 (Tier 3 first slice — GPU mesh 3D panadapter, RUNTIME VERIFIED)
Build clean x64 Release (EXIT=0). **User verified: "yes its working looks very fluid and solid", then after the BGRA palette fix "palletes are correct color now".** New file `Console/Display.Pan3DMesh.cs` (~730 lines, partial class Display) + `display.cs` made `partial`; Vortice.D3DCompiler 3.8.3 package added to Thetis.csproj (+ explicit `<Compile Include>` — csproj uses explicit Compile items despite SDK-style). Session-only toggle `chkGpuMesh3D` ("GPU 3D mesh (exp.)") in grpDisplayDriverEngine slot (8,47) TabIndex 52; handler pushes `Display.GpuMeshEnabled` live; registered in ForceAllEvents. **COMMITTED as `89f05af` 2026-08-23.**

Architecture:
- Heightmap R32Float dynamic texture (strengths [0..1], pow zCurve applied in shaders) + static UV-grid VB + index buffer rebuilt when (rows,cols) change; per-frame palette 256×1 BGRA8 replicating SelectSurfaceColour priority (waterfall sync > colormap > gradient > line colour); params snapshot captured each frame by DrawPanadapterDX2D into `_meshParams`, consumed NEXT frame pre-BeginDraw (one-frame latency invisible).
- Dispatch: RenderDX2D calls `_b3DMeshDrewFrame = RenderGpuMesh3D()` BEFORE `_d2dRenderTarget.BeginDraw()`; when true, the frame skips the D2D global Clear+background-image+FillRectangle block (mesh pass owns the backdrop), D2D still draws grid/live-trace/labels/waterfall on top. `if (!_b3DMeshDrewFrame) DrawPanadapter3DHistoryDX2D(...)` keeps the line renderer intact (rule 1). try/catch inside RenderGpuMesh3D releases objects + returns false → automatic D2D fallback. Teardown hooks: `ReleaseGpuMeshDeviceObjects` (shutdown/releasePartial), `ReleaseGpuMeshFrameState` (resize; RTV only).
- HLSL VS mirrors Aether math exactly (uv→pos incl. pow zCurve lift, foreshortened ridge ×row-width-fraction, y-flip to NDC); PS = palette lookup + horizontal slope shade 0.68–1.32 (kSlopeGain 0.55) + depth dim + linear haze toward bg + alpha fade 1.0→0.85. MeshCB is 48 bytes (float3 Background @16-byte offset 32).
- Skin background preservation: when `_bitmapBackground != null`, a D2D **prepass** draws the skin image first (`DrawSkinBackgroundPrepass`, same aspect-ratio logic as the normal frame), then instead of ClearRenderTargetView the mesh pass repaints ONLY the plot strip with a scissored opaque quad (`vs_clear`/`ps_clear` returning CB_Background, `_meshBlendOpaque` = default no-blend, `_meshRSScissor` ScissorEnable=true, rect [0, Shift, W, Shift+PlotH]); without a skin image it full-clears to bg colour as before. User confirmed image visibility preserved.

Debugging saga — lessons that cost hours (record for ALL future D3D11 work):
1. **Missing `OMSetRenderTargets` = draws invisible, clears fine**: `ClearRenderTargetView` writes through the view directly, but draw fragments rasterize into whatever is bound at the OUTPUT MERGER — never bound here → every fragment discarded while magenta debug clears showed perfectly. Fixed with `dc.OMSetRenderTargets(new[]{_meshRTV}, null)`. THE root cause of "no mesh, just black".
2. Isolation technique that found it: magenta clear + solid-green PS (proves target reachability vs geometry), then SV_VertexID fullscreen triangle `dc.Draw(3,0)` with NO buffers (splits IA-buffer issues from everything else), then reflection-dumping Vortice overloads via the %TEMP%\opencode\d2dprobe project.
3. **Palette BGRA packing**: B8G8R8A8_UNorm memory order is [B,G,R,A]; little-endian uint = `A<<24 | R<<16 | G<<8 | B`. Originally packed R/B swapped → colormaps rendered with red/blue exchanged ("palettes don't match non-mesh versions"). 
4. Samplers must be created AND bound per stage (`VSSetSamplers`/`PSSetSamplers`) or every SampleLevel returns 0; height SRV needed on BOTH VS and PS stages. Cull mode off (`RasterizerDescription(CullMode.None, ...)`) because the VS y-flip makes winding CCW.
5. Vortice API traps hit this session: `Filter.MinMagMipPoint` needs full qualification (`Vortice.Direct3D11.Filter` — ambiguous); `BufferDescription` ctor is (uint byteWidth, BindFlags, ResourceUsage, CpuAccessFlags=..., ResourceOptionFlags=..., uint stride=...) with optional tail; generic `CreateBuffer(float[], BufferDescription)` (Span overload) works for VB/IB init; `BlendDescription.AlphaBlend` preset = SrcBlend One / DestBlend InvSrcAlpha (premultiplied convention — fine for straight-alpha out with a=1); `RasterizerDescription(CullMode,FillMode)` partial ctor leaves DepthClipEnable=true, MultisampleEnable=true (both harmless here); `RSSetScissorRects(RawRect[])` wants an ARRAY of `Vortice.RawRect` (Left/Top/Right/Bottom, lives in Vortice.DirectX assembly — fully qualify); D2D types: `Matrix3x2` = System.Numerics, `RectangleF` = System.Drawing, `RawRectF` = Vortice.DirectX; background DrawBitmap overload used: `(ID2D1Bitmap, RawRectF?, float opacity, BitmapInterpolationMode, RawRectF? sourceRect=null)` (5-arg — the 4-arg float-second-arg forms are different signatures entirely).
6. 3D history ring resets each app restart — right after launch the grid is a thin strip (rows = histCount-based); wait ~10 s before judging visuals.

Known gaps (intentional first-slice scope, queued next): side walls/end caps, grid floor/rails, crest hairline, depth-direction slope shading (D2D path has these via Tier 1); GPU% not yet measured post-debug-removal (was pegged 99% during fullscreen-triangle debug builds only); RDP/WARP behaviour of mesh path untested (mesh auto-falls back to D2D lines on WARP by design — GpuMeshEnabled requires Hardware path).

### 2026-08-22 (GPU fallback first slice — render-path enum + HW→WARP init chain + Force CPU setting)
Implements the "NEXT SESSION START HERE" scope. Build clean x64 Release (EXIT=0, pre-existing warnings only). Runtime verification pending.

**display.cs**:
- New public enum `DXRenderPath { Unknown, Hardware, WarpSoftware }` + statics `m_eRenderPath` / `m_bForceCPURendering` / `m_bWarpDowngradeAttempted`; public `RenderPath`, `ForceCPURendering`, `RenderPathString()`.
- `initDX2D` refactored: device/swapchain/RT creation extracted to `createDX2DDevice(driverType, adaptorInfo, featureLevels, debug)`; attempt chain = forced-CPU ? [WARP] : [preferred adaptor (-adaptor:N, DriverType.Unknown) → default Hardware → WARP]. Each failed attempt: partial COM teardown via new `releasePartialDX2DDevice()` + ErrorLog line (`describeDXAttempt` names the tier); all fail → throw → existing catch MessageBox. Semantics preserved: preferred-adaptor lookup miss falls back to null-adapter Hardware inside that attempt.
- Startup log at end of successful initDX2D: `"DirectX initialised : render path=<Hardware|WARP software>, adapter='<name>', feature level=<n.n>[ (forced CPU)]"` via Common.LogString; `featureLevelString()` formats DXVersion().
- Mid-session auto-downgrade (rule 2): new `tryWarpDowngrade(reason)` — one-shot per session, only from Hardware path, not when forced; ShutdownDX2D + initDX2D(Warp); resets `_dx_fail_retry`. Wired into RenderDX2D: RecreateTarget-retry-budget-exhausted branch, Present DeviceRemoved/DeviceReset after retry budget, and outer catch (MessageBox only if downgrade fails/unavailable). Safe because Monitor lock is reentrant (called while frame holds _objDX2Lock) and from the outer catch it re-acquires.
- Note: WARP device runs the SAME D2D line-based renderer; `_renderPath` records which device backs it. GpuMesh tier arrives with Tier 3 behind a single dispatch point.

**setup UI/persistence**:
- setup.designer.cs: `chkForceCPURendering` ("Force CPU rendering") added to grpDisplayDriverEngine ("DirectX Display Settings") at slot (8,119), TabIndex 51, tooltip explains WARP/RDP/fallback; hidden `chkAccurateFrameTiming` moved to (8,143), group size unchanged, nothing else shifted.
- setup.cs: handler pushes `Display.ForceCPURendering` then `console.RestartDisplayDX()`; registered in ForceAllEvents BEFORE `udDisplayDecimation_ValueChanged` (which triggers Display.Target→initDX2D at startup) so the restored value is in force before first init; persistence free via Options table (CheckBoxTS name-keyed).
- console.cs: `RestartDisplayDX()` — no-op if DX not up (startup case), else pause display thread → ShutdownDX2D → `Display.Target = pnlDisplay` (full re-init) → unpause; same lifecycle pattern as SetupDisplayEngine/pnlDisplay_Resize.

**Test matrix for verification (rule 7)**: normal HW boot (log shows Hardware + real adapter), toggle checkbox (display must survive shutdown+reinit live, ErrorLog shows both paths), RDP session / forced mode (WARP path renders all modes incl. 3D panadapter + meters OK).
- VERIFIED 2026-08-22 on RX 580 machine: 5 ErrorLog entries across two on/off toggle cycles — startup Hardware/RX580/FL12.0 ↔ WARP/'Microsoft Basic Render Driver'/FL12.1 (forced CPU), clean live re-inits both directions, no errors. FL difference HW-vs-WARP is expected. DEFERRED (user has no second PC available yet): RDP/device-removal test of the tryWarpDowngrade auto path — start on HW, RDP in or update GPU driver, expect "switching to WARP" log line + successful WARP re-init with display still painting; also confirm meter init log line appears at launch with multimeter open.

**Same-day follow-ups**:
- MeterManager DXRenderer got the same fallback (was flagged as gap): `dxInit` now builds the identical attempt chain — respects `Display.ForceCPURendering` (WARP-only when set), else preferred adaptor → default HW → WARP; device/swapchain/RT creation extracted to `createMeterDXDevice`, new `releasePartialMeterDX()` disposes partial COM objects between attempts (also fixes pre-existing leak where the catch's ShutdownDX early-returned on !_bDXSetup without disposing); success/failure logged via Common.LogString ("Meter DirectX initialised : render path=... [tier], feature level=..."); total failure keeps original MessageBox. Display helpers `describeDXAttempt`/`featureLevelString` made public static and reused (no duplication). NOTE: meters still have NO mid-session device-removed auto-downgrade — only init-time fallback; display's tryWarpDowngrade does not restart meters.
- Setup → Display → General layout fix: grpSpectralWarningLeds moved from (566,300) back to left column at (394,200), directly under btn3DSettings (394,172, 146x23); grpDisplayDriverEngine height grown 130→145 so chkForceCPURendering (local y=119) sits fully inside its frame. Hidden chkAccurateFrameTiming (Visible=false, y=143) unaffected by clipping.
- WARP 3D depth-lines cap (user perf feedback: RX580 box on forced CPU, 35 depth lines = ~7fps, 15 lines = ~20fps): new `Display.Max3DLinesSoftwareRender = 15` (public const next to Max3DHistoryLines, now public); render loop clamps `linesToDraw` to it whenever RenderPath==WarpSoftware — authoritative for forced AND auto-downgraded sessions, saved preference untouched. frm3DPanadapter.ApplyRenderPathLimits() caps ud3DLineCount.Maximum to 15 while on WARP and restores 60 + remembered value when back on HW (`_lineCountBeforeCap`, preserved across cap unless user manually edits while capped; _initializing-guarded so no spurious Pan3DLineCount pushes); tooltip updated dynamically. Called from form ctor and setup's chkForceCPURendering_CheckedChanged (only path that can reach the lazily-created form; auto-downgrade mid-session with the 3D window already open leaves stale UI until next open/toggle — cosmetic only).

### 2026-08-23 (Fill Color toggle + opacity, data-line/glow color normalization, RUNTIME VERIFIED)
User-verified end-to-end ("they are all proper now including the 2d"):

1. **Fill Color feature** (`frm3DPanadapter.cs`): new row under the existing Color field — enable checkbox `chk3DFillColorEnable`, swatch `clrbtn3DFillColor`, opacity `tb3DFillOpacity` (TrackBarTS 0–100, default 55 = the 0.55 alpha every fill path historically used). Colormap/Floor Lift/Reset shifted down 30 px, ClientSize 232×353, TabIndex renumbered. Engine additions in `display.cs`: `Pan3DFillColorEnabled` / `Pan3DFillColor` / `Pan3DFillAlpha` (clamped 0–1), persisted via Common.SaveForm auto-mapping AND explicit cases in `RestorePan3DPersisted`; Reset Defaults restores off / Aquamarine / 55.
   - Behaviour contract: **unchecked = status quo on every colormap** (Turbo/Viridis/Inferno LUT fills, WF-sync fills, Classic strength-keyed mesh fill, D2D gradient). **checked = front live fill on ALL colormaps/WF-sync/mesh+D2D becomes a strength-shaded solid brush** (`0.25+0.75·strength` brightness × chosen colour, alpha from slider, cache key `(alpha<<24)|rgb`) inserted as TOP priority in the pan_fill chain. Never touches the data line stroke or the 3D surface palette.
2. **Data line color normalized**: removed the Tier-3-era per-column recolouring of the live trace stroke (colormap-LUT/waterfall brushes). The top curve now uses the configured DataLineColor on EVERY colormap/sync path, 2D and 3D mesh alike — palettes colour the surface and fills only, never the line (user rule: "data line ... works fine as is").
3. **Glow halo color fixed**: glow replay used the recorded per-segment brushes; blurring per-column palette colours produces colourless mush on non-Classic palettes (Classic only looked right because every shade was the set hue). Now the sharp trace draws directly with per-column colours (identical to the proven glow-off code path) and the blurred halo strokes use the base `lineBrush` → constant user colour on all colormaps/WF sync. Halo still composites under overlays inside the pushed spectrum clip.
4. **Abandoned experiments (fully reverted before commit, kept here as lessons)**: (a) suppressing the curtain by skipping `RenderGpuMesh3D()`/`DrawPanadapter3DHistoryDX2D` — user rejected: kills the entire 3D scene instead of clearing just the front; also one stale-binary cycle caused confusion about what was active. (b) hairline suppression + near-row height flattening with row-0 exemption — rejected: even touching row-adjacent crest hairlines changes the perceived top curve. LESSON: the mesh's row-0 crest hairline IS part of the visual data line ("where you put the glow"); any change to rows adjacent to the live crest reads as breaking the line itself. If "solid front" is revisited, design it as an overlay that cannot alter the crest geometry, and describe the expected look to the user BEFORE coding.

Build clean x64 Release (0 errors). Fresh-binary discipline note: two test cycles were invalidated by testing a stale exe after source-only reverts — always rebuild + restart Thetis between iterations.

### 2026-08-23 (Solid front wall "2D panel" mode for Fill Color, RUNTIME VERIFIED)
Follow-up to the Fill Color feature (`b84a70e`); user-verified ("perfect they both match"). Design converged over several iterations — full history kept because the wrong turns are instructive:

1. **Final behaviour (Fill Color checked)**: per-column, in the shared pan_fill block: (a) OPAQUE backdrop stroke crest→baseline in `m_cDX2_display_background_clear_colour` erases every receding row behind the wall; (b) skin image slice restored inside the wall strip via DrawBitmap with src/dest rects replicating the frame-start aspect-fit math (works on BOTH paths — mesh normally never draws the skin, restoring it there is what makes low sliders reveal skin instead of black; this was an explicit user correction); (c) fill = 2D-panafill-style vertical gradient from `_pan3DFillColor` scaled by slider alpha. Works with the checkbox alone (independent of pan_fill). Unchecked = status quo.
2. **Slider semantics**: bottom gradient stop converges toward the top stop as slider rises (`tailRatio = 0.16/0.55 + (1−0.16/0.55)·alpha`) so 100% is a perfectly uniform opaque wall — fixes "never goes fully opaque" on the standard path where skin texture showed through the gradient tail (mesh hid it behind flat black).
3. **Live-fill density match**: with fill unchecked, both paths' live fills now run at `liveFillAlpha = 0.95` (was 0.55). Reason: standard's curtain stack compounds translucency so its front read denser than the mesh's single sheet; user tuned mesh 0.75→0.85→0.9→0.95→0.98→0.8 and settled 0.95, then matched standard up to 0.95.
4. **Rejected variants** (user feedback): full-height panel (baseline→plot-top occlusion) — hides the ENTIRE 3D scene, no rear visible at all; strength-shaded per-column fill — moving brightness bands travel across the wall, "not working like the 2D panadapter"; black-only backdrop on standard — user runs a skin background image and wants fades to reveal it.

Build clean x64 Release (0 errors).

### 2026-08-23 (Filter/zero-line markers stay visible over the solid front wall, RUNTIME VERIFIED)
Follow-up to the solid-wall mode (`b661f77`); user-verified ("perfect we made alot of progress"). Problem: the TX/RX filter lines and locked 0Hz line were drawn during the GRID pass (before the live trace), so once the 3D front wall/fill painted over them they vanished — visible over the rear rows only (history draws before the grid).

1. **Fix**: extracted the whole marker set — sub-RX filter + its 0Hz line, RX filter + highlighted edge, TX filter/edges, band-stack overlays, CW zero-beat + TX CW lines, locked 0Hz line (all incl. their waterfall variants) — into self-contained `drawFilterZeroOverlaysDX2D(nVerticalShift, W, H, rx, bottom, bIsWaterfall)` (display.cs ~:10109). The helper re-derives Low/High/f_diff, filter_low/high (+DRM override), top, localSubDiff and cwSideToneShift from globals exactly as the grid method does, and runs inside the CALLER's clip (no own push/pop).
2. **Call sites**: (a) grid method calls it once at the original position — non-3D/waterfall rendering unchanged; (b) `DrawPanadapterDX2D` calls it a SECOND time after the per-column live trace loop (just before the WF-sync brush cleanup, still inside the spectrum clip), gated `draw3DHistory && !local_mox` — mirroring the 3D-history gate — so markers render on top of the front wall across the whole depth.
3. **Deliberately left in the grid method**: notches and 60m-channel bars (not part of the request). Side effect: band-stack/CW-zero now draw slightly earlier relative to those in the under-pass; no conflict observed.
4. **Bookkeeping note**: the toolbar-button UX fixes shipped as `5364b44` never got an entry — recorded here for completeness: two-way sync `btnDisplay3DPan` ↔ setup `chkDisplay3DPanadapter` (via `SetupForm.Display3DPanadapter` setter guarded by `IsSetupFormNull`; SetupForm always exists at boot) and right-click on the toolbar 3D button opens the settings popup through public `setup.Show3DPanadapterSettings()`. WinForms gotcha: MouseClick/Click NEVER fire for the right button — wire MouseUp.

Build clean x64 Release (0 errors).

### 2026-08-23 (Tier 3 parity + mesh opacity + brush-race crash fix, RUNTIME VERIFIED)
Three items landed and user-verified ("no crash now", parity look "does look right", opaque fix "perfect"):

1. **Tier 3 Tier-1-parity port** (`Display.Pan3DMesh.cs`, +~360 lines): crest hairlines, side walls/end caps, perspective grid floor + rails ported from the D2D path so the GPU mesh matches the established look. Slope shading was already at parity (horizontal-only, kSlopeGain 0.55, clamp 0.68–1.32 — same as D2D; depth-direction shading deliberately NOT added to preserve the approved look).
   - **Crest hairlines (PASS 2 parity)**: new `ps_line` + static LineList index buffer over the SAME UV grid (row-contiguous indices → one DrawIndexed per row). Formula mirrors D2D exactly: palette colour × `outlineBright(0.35+0.65s) × dim(0.72+0.28·(1−v)) × 1.5` → min 1.0, hazed toward bg, alpha `(1−v·0.4)·0.85`. **Occlusion**: hairline(r) drawn THEN sheet block(r−1..r) in a back-to-front painter loop (2·rowCount−1 draw calls, index ranges are row-contiguous in both IBs) so nearer rows cover farther outlines exactly like the D2D fill/outline interleave.
   - **Grid floor + rails**: flat vertex-colour pipeline (`vs_flat`/`ps_flat`, pixel-space positions through the same plot transform). 6 smoothstep-baseline gridlines alpha 0.10→0.03 + 2 straight rails alpha 0.12, geometry computed CPU-side mirroring the D2D math, drawn before walls/surface.
   - **Side walls**: edge-trace triangle strips down to absolute bottomY honouring `_pan3DSideWalls`; flat wall colour = palette[front-row left-edge strength] ×0.32 blended toward bg by FogFor(0.5) — identical to D2D's wall brush. Wall edge crest Y recomputed CPU-side with vs_main's exact parametrization (v = r/(rows−1)) so walls hug the surface edge. Guard rows≥2 (rowCount is always ≥2 by construction anyway).
   - **Premultiplied-alpha convention**: ps_line outputs `rgb*alpha`; aux vertex colours premultiplied CPU-side — the shared `BlendDescription.AlphaBlend` state (SrcBlend One / InvSrcAlpha) then composites as true SourceOver for all NEW passes. The main sheet PS was left unchanged (straight rgb output) since its look was already approved.
   - Palette build refactored into `ComputePaletteArray(yRange)` (uint[256] BGRA) + `UploadPalette(dc)` so the wall colour can read the same data without a COM dependency; dynamic `_meshAuxVB` (stride 32B = float2 pos + float4 col) sized via `AuxVertexBudget(rows)` = 16 line verts + 12·(rows−1) wall verts, rebuilt in EnsureMeshGrid alongside the grid.
2. **Brush-race crash (user hit while dragging colour picker, Classic palette)**: SEHException 0x80004005 at display.cs:5946 (`DrawLine(bottomPoint, point, activeFillBrush, ...)`) dispatched from :4550 (RX1 panafall) / :4571 (RX2 pane). Root cause: the Aug-22 dispose+null fix made the setter SAFE against stale brushes but it still ran **unlocked** on the UI thread — during a colour-picker drag (or ANY popup open / Reset Defaults, both fire PushAllSettings → setter) Dispose could land between the display thread's null-check/`activeFillBrush = m_bDX2_3d_fill_brush` and the DrawLine → use-after-dispose AV inside d2d1. Fix: setter's dispose+null now under `lock (_objDX2Lock)` — the frame body holds that lock for its entire duration, so disposal waits for frame end and the next frame recreates lazily with the new colour; matches the ~30 existing gated setters convention. LESSON: the Aug-22 audit checked "dispose-without-null" everywhere but nobody re-checked WHICH THREAD disposed — a null-safe dispose on the wrong thread is still a race.
3. **Mesh sheet made fully opaque**: user noticed skin/background bleed-through on quiet bands (flat noise floor) toward back rows. Explanation: D2D stacks dozens of translucent curtains per column (accumulates ≈opaque); the mesh is a SINGLE sheet whose alpha faded 1.0→0.85 with depth, letting the backdrop show at ≤15%. Since dim+haze already carry depth, ps_main now returns alpha 1.0 unconditionally.

ErrorLog forensics note: after the SEH, tryWarpDowngrade logged "switching to WARP" yet the next init log showed Hardware/RX580 — post-AV recovery chain is murky (device was poisoned; app needed restart). Not chased further: preventing the AV is the cure.

Remaining Tier 3: GPU% measurement post-debug-removal; RDP/device-removal auto-downgrade test (deferred until second PC/RDP available).

### 2026-08-23 (ZTB toolbar button white-block fix via skin-image fallback chain, RUNTIME VERIFIED)
User reported the console "ZTB" button (below waterfall, right of zoom slider) rendering as a plain white block. Root cause: `btnDisplayZTB` has NO image set in most skins (incl. SDRVST3/W1AEX sets), and an unskinned flat ButtonTS = light-grey rectangle with near-white text on dark chrome. A naive name→name alias was rejected because (a) it would override genuine per-skin ZTB art that many third-party skins ship, and (b) a hard dependency on one donor could still miss on minimal skins.

Fix (`Skin.cs`) — buttons resolve art through a fallback chain instead of exact-name-only:
- New `ButtonImageSetExists(ctrl, name)` probes `<skin>\<form>\<name>-<0..7>.png` (+ shared `Console\` dir).
- `SetupButtonImages`: prefer the control's OWN set first (N2MDX/XMAS/AI/VA2CST/K1GMM… skins ship real `btnDisplayZTB` art — must not be overridden); if absent, fall back through per-control donor lists. For `btnDisplayZTB`: `btnDisplayZoom05` (same row, same 40×24 size, user-chosen) → PanCenter → Zoom1x → Zoom2x → Zoom4x. Every installed skin ships PanCenter + zoom sets, so no skin can regress to the white block.
- BOTH lookup passes (cache-key scan + ImageList fill loop) now use the RESOLVED name — previously the fill loop used raw `ctrl.Name`, which silently starved any aliased control.
- Incidental: same latent fill-loop bug fixed in `SetupCheckBoxImages` (its ImageList fill now honours the chkTXVST/chkRXVST/chkDisplay3DPan → chkNoiseGate alias).

Build clean; user verified the button shows skinned Zoom05 art.

### 2026-08-23 (GPU% indicator on the console status bar)
Delivers the at-a-glance measurement tool for the last open Tier-3 item (mesh GPU% cost). New `Invoke`-style helper `GpuUsageMonitor.cs` (explicitly added to Thetis.csproj):
- Samples the Windows **"GPU Engine" / Utilization Percentage** performance counter category — the same source Task Manager's per-process GPU column uses. Instances are filtered by `pid_<ourpid>_` prefix so ALL engines (3D/Copy/VideoDecode/…) of THIS process are summed across all adapters.
- Engine instances appear/disappear dynamically → full instance re-enumeration every 5 s and value sampling every 1 s, all on the threadpool (`PerformanceCounterCategory.Exists` is slow, so even the availability probe runs off-thread).
- Values posted to the UI thread via the captured `SynchronizationContext`; clamped 0–100 (multi-GPU theoretical overflow noted in comments); if the category is missing (pre-WDDM2.0 driver etc.) it reports unavailable exactly once and goes dormant.
- UI: `toolStripStatusLabel_GPU` inserted programmatically into `statusStripMain` immediately AFTER `toolStripStatusLabelTXAnt` ("last in line" per user), 68 px, ControlLightLight text, shows `GPU nn%`, stays hidden when counters are unavailable. Started from `Console_Shown` → `initGpuUsageMeter()` (region `GPU usage indicator`); disposed first thing in `Console_Closing`.
- Placement history: v1 was a form-level label under panelPower using designer coords (landed overtop the buttons because saved layouts move the strip); v2 anchored live to `panelPower.Bottom` (right spot, user liked size/bold) then user relocated it into the status bar instead — floating label code removed.
- LESSON: never hard-code console control coordinates from resx — saved layouts/skins reposition controls at runtime; anchor relative to live bounds or parent containers.

Build clean; user verified placement/readout ("perfect"). Actual mesh-vs-D2D GPU% reading session still pending.

### 2026-08-23 (GPU mesh reworked to Aether-style curtain topology — spike "ribbon" bug fixed, RUNTIME VERIFIED)
User tuned to an AM carrier: on the D2D 3D path the carrier renders as a thin red needle per history row (correct), but on the GPU mesh it showed a tall coloured **ribbon** hanging under the tip and running front-to-back.

Root cause: our mesh was a continuous **heightfield sheet** — quads interpolate heights across neighbouring columns, so a single-column spike (carrier ≈ 1 FFT bin) creates near-vertical cliff faces sweeping floor→peak along full depth. The face pixels take low/mid palette colours (blue/green in Classic) and read as a ribbon. The D2D path has no such geometry: it paints each column independently, fill stroke from its own crest down, background visible either side of a needle.

Two pixel-shader band-aids tried and rejected before the real fix: (1) colour faces with neighbourhood-max crest strength — ribbon became uniform pink but geometry remained; (2) bilinear-sample + `clip()` discard of below-crest face pixels — introduced GLOBAL choppiness (bilinear exposed a latent half-texel offset between grid UVs `c/(cols-1)` and texel centres `(c+0.5)/cols`) plus a residual upper-face band. LESSON: don't patch away geometry that shouldn't exist with per-pixel heuristics.

Reference answer (`%TEMP%\opencode\aether\resources\shaders\dss_mesh.frag`): Aether's GPU mesh is NOT a heightfield either — *"Fill vertices (edge >= 0) draw the original full-height coloured curtains"* — per-column curtains from crest to floor, i.e. the GPU equivalent of exactly what our D2D path does.

Rework (`Display.Pan3DMesh.cs`, user-approved "the aether way", verified "working perfect"):
- **Vertex grid** → per (row,col) PAIR of vertices, float4(u, v, corner, 0), stride 16: corner 0 = crest above column, corner 1 = directly below at absolute `CB_BottomY`. `vs_main` branches on corner; everything else (perspective inset/rwf/baseline/zCurve lift) unchanged.
- **Index buffers**: sheet IB now `rows*(cols-1)*6` per-row curtain quads (row-contiguous); line IB retargeted to crest vertices (`idx*2`, stride 2 between neighbours).
- **Draw loop**: per row back-to-front, hairline(r) then curtain(r) — every row draws (old code skipped the r=0 sheet block as the live trace covers it; front curtain now included for full parity).
- **ps_main** reverted to plain PointSamp taps + palette(s) (no smax/clip experiments); still opaque — curtains tile edge-to-edge below crests so coverage stays fully solid (explicit user concern beforehand: "it won't look solid like now?" — it does).
- **Input layout** POSITION → R32G32B32A32_Float; **MeshConstants** gained `TexelY` (1/rows), buffer 48→64 bytes.
- **Latent sampling fix**: new `TexelAt(u,v)` remaps grid uv → texel-centre uv for ALL height taps (VS/PS/line PS). The old code sampled raw grid uv, straddling texel boundaries by half a texel everywhere.

Build clean; user verified needle rendering + overall look.

### 2026-08-23 (waterfall colour quantization fixed: gradient LUT 101 -> 1024 steps, RUNTIME VERIFIED)
Investigation prompted by user question "is the waterfall 8-bit / limited to 256 colours?". Findings: pixel storage was ALREADY 32-bit BGRA (`pixel_size=4` rows into a D2D Bgra32 bitmap) — not an indexed surface. The real limit was the **Custom-scheme colour LUT**: `perc = (int)(overall_percent * 100f)` indexed `_rx1/_rx2/_tx_waterfall_grad`, each only **101 entries** (producers `WaterfallRXGradient()/WaterfallTXGradient()` in setup.cs sampled the gradient picker at whole-percent steps). Result: at most 101 discrete colours on the RX1/RX2/TX waterfalls regardless of gradient smoothness — visible posterized bands following signal contours (contour-map effect), worst on slow fades and smooth dark gradients. Enhanced scheme already computed colours continuously; the GPU mesh palette was already 256 entries — which is why 3D looked smoother than 2D.

Fix (display.cs + setup.cs):
- New `public const int WaterfallGradSteps = 1024;` on Display (single source of truth both files use).
- Producers build 1024-entry arrays, sampling `GetColourAtPercent(i/(N-1))` continuously; rebuild still event-driven on gradient change, zero added per-pixel cost (~0.05 dB/step at a typical 50 dB span).
- `OnWaterfallRX/TXGradientChanged` guards compare against `WaterfallGradSteps`; copy loops bound by array length.
- All consumers index **dynamically** by `array.Length - 1` with explicit clamps instead of hard-coded 100: `GetWaterfallColor(...)` (also feeds spectrum fills and the D2D 3D-history SelectSurfaceColour path) and the inline waterfall Custom block (`cols[100]` top-colour reference replaced by `cols[^1]` equivalent).

Verification tip that came up: distinguish artifact from real RF — quantization banding follows equal-strength contours of signals/noise floor; genuine static crashes are full-width bright streaks at one instant (always keep those).

Build clean; user verified "seems to look smoother".

### 2026-08-23 (Tier 3 mesh waterfall: GPU ring replaces D2D bitmap scroll — BUILT, awaiting runtime verification)
Extends the GPU-mesh pipeline (chkGpuMesh3D toggle) to the waterfalls. Design decisions locked with user: pixel-identical visuals (row colours still baked on CPU by the existing colour switch; no shader palette), fill-only 2D mesh (data line/glow/peak-hold stay D2D, later slice), same master toggle gated on Hardware render path. Rule-1 respected: full D2D fallback retained on every failure path (max one lost line).

New `Display.WaterfallMesh.cs` (partial class Display, registered in Thetis.csproj):
- Per-rx ring: default `B8G8R8A8_UNorm` W×rows texture + W×1 staging; companion `R32Float` 1×rows anchor texture + 1×1 staging. Rows uploaded via Map+MemoryCopy then `CopySubresourceRegion(dst=ring @ (0,Head), src=staging)` (Vortice signature: `(dst,dstSub,dstX,dstY,dstZ,src,srcSub,Box? srcBox)` — offsets are DESTINATION args, Box is source-only; verified via package XML docs).
- Ring model replicates prepareWaterfallBitmapShift semantics: cumulative AnchorNow advances by shiftPixels per line unless clearExisting (→ reset 0, ValidRows=0); row stored with post-shift anchor; shader computes source column `cw = x - (AnchorNow - rowAnchor)`; out-of-range → transparent black. Smear mode falls out naturally.
- `WaterfallMeshCommitLine(rx,row,paneRows,addRow,shiftPixels,clearExisting)` returns MeshOwnsPane; ownership set true only after first successful addRow upload, false on ANY failure (releases ring → seamless D2D takeover). Hold frames (addRow=false) handled internally once owned.
- HLSL vs_wf/ps_wf compiled at runtime via Vortice.D3DCompiler.Compiler.Compile (Pan3DMesh pattern); per-pane 48B cbuffer (PaneX/Y/W/H, TargetW/H/TexW/TexH, Head/ValidRows/AnchorNow/Opacity from m_fRX{1,2}WaterfallOpacity).
- Pane geometry captured one frame ahead (`CaptureWaterfallPaneParams` after transform reset in DrawWaterfallDX2D; consumed + invalidated by `ClearWaterfallPaneCaptures()`).

Integration (display.cs):
- RenderDX2D pre-BeginDraw pass now resets `_bGpuBackdropDone`, runs `RenderGpuMesh3D()` then `RenderGpuWaterfall()`; skip-clear condition extended to `_b3DMeshDrewFrame || _bWfMeshDrewFrame`. Shared backdrop ownership added to Pan3DMesh (`EnsureGpuBackdrop(dc)` draws skin prepass or bg clear exactly once).
- DrawWaterfallDX2D scroll block restructured: temp-bitmap CreateBitmap/CopyFromBitmap work moved INTO the D2D branch (was unconditionally above colour switch — per-line COM churn now skipped when mesh owns); commit tried first when armed (`!stopWaterfallOnTx || clearExistingBitmap`, honouring width-change clears during TX-stop like D2D order); legacy scroll runs only if commit declined. recordWaterfallAdvance hoisted out of the D2D gate (bookkeeping must run on both paths).
- Present gate: DrawBitmap skipped when `WfMeshArmed && WfMeshOwnsPane(rx)` (ownership fully reflects commit outcome — no extra flag needed at that scope).
- Teardown: ReleaseWaterfallMeshObjects() after ReleaseGpuMeshDeviceObjects() at both device-loss sites (NOT at swapchain-resize site — device objects survive; ring rebuild handles dim changes).
- setup.designer.cs: chkGpuMesh3D caption "GPU 3D mesh (exp.)" → "GPU mesh (exp.)" + tooltip now mentions waterfall.

Known acceptable divergences: history cleared (not stretched) on height-changing mode switches; blank pane during first-fill window after enable/ring rebuild. Runtime verification pending: enable GPU mesh, watch waterfall scroll identically to before; check RX1/RX2 opacity sliders, smear mode, TX-stop, resize/mode-switch recovery, and log strings ("GPU waterfall mesh active" / failure lines).

### 2026-08-23 (Tier 3 mesh 2D panafill: GPU curtain sheet replaces per-column fill strokes — BUILT, awaiting runtime verification)
Third mesh slice: the plain 2D panadapter FILL (the per-column crest-to-baseline D2D strokes in DrawPanadapterDX2D) moves to a GPU-rendered curtain sheet. Data line / glow / spectral peak-hold stay D2D by design decision. Rule 1 intact: any failure returns false and the legacy column loop runs unchanged.

New `Display.Pan2DMesh.cs` (partial Display, registered in csproj):
- Per-rx offscreen W x H BGRA sheet (RT-bound texture wrapped for D2D via QueryInterface<IDXGISurface> + CreateSharedBitmap, premultiplied), cols x 1 dynamic R32F strength texture, shared 256 x 1 dynamic BGRA gradient LUT.
- HLSL vs_spec/ps_spec: SV_VertexID quad soup (6 verts/column, zero vertex buffers); crest mapping replicates Y = shift + H*(1-s) - 0.5 with s = clamp((data[i]+fOffset-grid_min)/yRange) — same normalisation as the column loop incl. floor mimic. PS samples the premultiplied LUT bottom->top exactly like the D2D brush axis.
- Colour parity: solid mode = uniform data_fill_color(_tx); LG mode = same ucLGPicker stop lists as buildLinearGradientBrush(RX/TX) (both rx use RX1GradPicker; TXGradPicker under mox; alpha = data_fill_color.A / data_fill_color_tx.A). Stop lists cached per-slot keyed by (version, tx, lg, gridMin, gridMax, alpha); _lgBrushVersion bumped from both builders via SpectrumFillBrushesChanged(). LUT expanded+uploaded every call (256 texels, free).
- Scissor replicates the PushAxisAlignedClip(0,shift,W,H) region; blend disabled during sheet render (columns disjoint, colours pre-premultiplied).
- Interop protocol: called INSIDE DrawPanadapterDX2D (after modifyDataNotches, before the column loop): _d2dRenderTarget.Flush(out tags) -> D3D pass -> dc.Flush() -> restore OM to backbuffer RTV + full scissor -> back in D2D ONE DrawBitmap composites the sheet. Zero frame latency (unlike the waterfall capture scheme) because all inputs are already computed at call time. NOTE the OM restore is mandatory: changing OM inside an open D2D BeginDraw does NOT get undone by D2D until the next BeginDraw, so skipping the restore sends every later D2D draw of that frame (trace/peaks/overlays/fps text) into the sheet texture — first build showed exactly this as "panadapter and fps text vanish when GPU mesh enabled"; fixed by restoring _meshRTV + full scissor + Flush after the pass.

Integration (display.cs):
- DrawPanadapterDX2D: gated `if (!draw3DHistory && pan_fill)` -> TryRenderSpectrumFillMesh + BlitSpectrumFillMesh right after visual-notch modification (inside the clip region, before anything else draws there => stacking identical: grid < fill < peaks < line < glow < overlays). Legacy block now `(pan_fill || liveCustomFill) && !bSpecFillMesh`.
- Scope note: when the 3D history overlay is active (draw3DHistory) nothing changes — its colormap/waterfall-sync/custom fills + mesh surface own that path already.
- Hooks: ReleaseSpectrumFillFrameState() at resize site (D2D wrappers die with target recreation; textures survive), ReleaseSpectrumFillObjects() at both device-teardown sites, SpectrumFillBrushesChanged() at end of buildLinearGradientBrush + buildLinearGradientBrushTX.
- Known minor divergence: GPU quads rasterise hard-edged crest boundaries where D2D strokes were antialiased (sub-pixel, same character as accepted 3D mesh).
- BUG FIXED during bring-up #2: the sheet pass ran its viewport/scissor in FULL-TARGET coordinates while the RT is the W x H sheet itself. RX1 (shift=0) masked it - wrong coords coincided with sheet-local; RX2 (shift=298, H=149) scissored every quad away => "RX2 panadapter missing". Fixed by making the pass fully SHEET-LOCAL: VS subtracts PaneY before normalising by sheet W/H, cbuffer carries SheetW/SheetH instead of target dims, viewport+scissor = (0,0,W,H). Lesson: an offscreen-RT pass must never inherit target-space rects from the pane it mirrors.
- BUG FIXED during bring-up #3: after a render-target recreation (resize), ReleaseSpectrumFillFrameState nulls SheetBitmap but EnsureSpecSheet early-returned on matching dims and never rebuilt the D2D wrapper => blit no-opped forever (silent). EnsureSpecSheet now recreates the shared bitmap when non-null-dims but wrapper==null.
- BUG FIXED during bring-up #4 (3D mesh + RX2): CaptureMeshFrameParams had a hard `if (rx != 1) return;` so RX2-only layouts never captured params; _meshParams kept stale RX1-era values, RenderGpuMesh3D drew the single surface at the WRONG position while the GLOBAL _b3DMeshDrewFrame flag made RX2 skip its D2D 3D fallback => "RX2 3D reverts to flat pan". Fix: params now capture for any rx (+RX field), new GpuMesh3DOwnerRX tracks which pane the GPU surface actually served this frame (reset to 0 pre-pass, set on success), and all per-pane gates key off it: D2D 3D fallback runs `if (!_b3DMeshDrewFrame || GpuMesh3DOwnerRX != rx)`, liveWfBrushCache + mesh-fill-colour branch use GpuMesh3DOwnerRX == rx instead of (_b3DMeshDrewFrame && rx == 1). NOTE behaviour in duplex: the surface is owned by whichever rx captured last (rx2 - drawn after rx1), so rx2 gets the GPU surface and rx1 renders its 3D via D2D fallback; both look identical by design. History ring remains fed by RX1 pushes only (pre-existing, shared with D2D path).
- BUG FIXED during bring-up #5 (RX2 3D waterfall-sync colours): the D2D 3D path gated waterfall-sync colouring to rx1 (`useWaterfallSync = _pan3DWaterfallSync && rx == 1`, thresholds always RX1's), so RX2's CPU-rendered surface fell back to generic colormap/gradient instead of matching its waterfall; the GPU path applied sync universally but with RX1 thresholds. Fix: shared helper Get3DWfSyncThresholds(rx, out lo, out hi) returning per-rx thresholds (rx2_waterfall_low/high_threshold, rx2 AGC via _RX2waterfallPreviousMinValue - m_fWaterfallAGCOffsetRX2 when !m_bRX2_spectrum_thresholds) + false on degenerate range; used by both DrawPanadapter3DHistoryDX2D (keyed off the rx param) and ComputePaletteArray (keyed off _meshParams.RX). Runtime-confirmed.
- Runtime verification: CONFIRMED WORKING by user 2026-08-23 ("all working proper", "working correct now") - plain 2D panafill RX1+RX2/duplex, fill parity vs CPU toggle, 3D surface on RX2 with mesh enabled, RX2 waterfall-sync colouring in both mesh and CPU modes. Remaining checks if issues resurface later: LG-gradient vs solid modes under MOX, gradient-picker edit refresh, resize recovery mid-run, "GPU 2D panafill mesh active" log string.
- BUG FIXED during bring-up: sheet texture was created with BindFlags.RenderTarget ONLY. D2D accepts CreateSharedBitmap on such a surface but marks the wrapper CANNOT_DRAW (resource not samplable), so the composite DrawBitmap fails at flush time and EndDraw returns D2DERR_BITMAP_CANNOT_DRAW (0x88990021) - which DISCARDS THE ENTIRE BATCH DRAWN SINCE THE LAST Flush. Symptom: with GPU mesh enabled, everything D2D-drawn after the panafill call site (trace, peaks, separator bar + its text, fps text) vanished; only pre-flush output (grid) + the two GPU mesh passes survived. Diagnosis path worth remembering: EndDraw's ignored HRESULT is the ground truth for "half my frame disappeared"; instrument it, decode the code, and check BindFlags of any texture wrapped via QueryInterface<IDXGISurface>+CreateSharedBitmap - ShaderResource bind flag is mandatory for D2D-readable surfaces. EndDraw failure logging kept permanently behind SpecMeshWasUsedThisFrame (one-shot).

### 2026-08-27 (GPU spectrum peak-hold overlay - fresh reimplementation, roadmap #5, BUILT, awaiting runtime verification)
The auto-generated GPU overlay work from 2026-08-27 00:00-02:10 was lost when the user restored the repo to a clean backup (evidence survives only in `%TEMP%\opencode\fix_overlay.ps1` + ErrorLog.txt `"GPU overlay mesh active (4864/4888 verts, fill mode, rx1)"`). Reimplemented from scratch with a fresh design; none of the prior debug patches were ported.

New `Display.OverlayMesh.cs` (partial Display, registered in csproj):
- GPU spectral peak-hold overlay: the Active Peak Fill columns AND the peak trace line render into a per-rx offscreen W x H BGRA sheet, composited by ONE D2D DrawBitmap at the exact position of the per-column peak strokes (after the pana-fill sheet, before the data line).
- HLSL vs_ovl/ps_ovl: SV_VertexID quad soup (6 verts/column), corners uploaded per frame as a cols x 2 R32G32B32A32 texture (4 corner points/column = 8 floats), PS emits premultiplied solid colour; Y shifted sheet-local in the VS (Pan2DMesh pattern).
- Geometry replicates the D2D column loop exactly: fill mode = one quad per column centred on i*dec with width local_Decimation from crest Y to peak Y; trace mode = per-segment extruded quads (width line_width) including the D2D oldSpectralPeakPoint init quirk (plain formula + H-clamp for the first point) and the live3DMapping pow(s,zCurve)*ridge Y branch.
- Colour source: reads the D2D peak solid-brush colour per frame (GDI+ pen-brush fallback) so GPU and CPU peaks stay in lockstep.
- Engagement gate for stacking correctness: only when the GPU panafill sheet drew (bSpecFillMesh) OR there is no panafill at all, plain 2D panadapter only (!draw3DHistory), peak-hold on. Toggle `GpuOverlayEnabled` (session-only, default off; chkGpuOverlay). Rule 1 intact - every failure logs + returns false, the guarded D2D peak DrawLine calls in the column loop still run.

Integration (display.cs): dispatch + composite block before the column loop; the two peak DrawLine sites guarded by !bOverlayMesh (oldSpectralPeakPoint tracking runs on both paths); teardown hooks `ReleaseSpectrumOverlayObjects()` at both device sites + `ReleaseSpectrumOverlayFrameState()` at the resize site.

Setup UI: `chkGpuOverlay` ("GPU overlay (exp.)") in grpDisplayDriverEngine at (8,100), TabIndex 54; handler `chkGpuOverlay_CheckedChanged` -> `Display.GpuOverlayEnabled`; registered in ForceAllEvents. Session-only like siblings.

Build: console csproj + full solution x64 Release EXIT=0 (pre-existing CA1416 noise only). Startup smoke: launches and runs without crashing.

Runtime verification pending: enable GPU mesh + GPU overlay + spectral peak-hold; watch the fill bars / trace draw via the sheet and match the CPU path pixel-identically; toggle Active Peak Fill; WARP / force-CPU must auto-fall back to the D2D strokes; log strings "GPU overlay mesh active (N verts, fill/trace mode, rxN)" / guard lines under the mesh diag toggle.

### 2026-08-27 (overlay runtime bug: pink "fill bar" across top + twitchy diagonal; v1/v2 diag runs both captured ONLY idle frames — signal capture still pending)
USER-REPORTED BUG: chkGpuOverlay ON (peak-hold on) draws a solid pink (Active Peak Fill colour) band/bar across the top of the pan; signals show UNDER the bar; peak markers wrong; a pink diagonal ray twitches from the left on occasion. CPU path is clean. User hint: "AMD RX580 bug - something about not using buffers".

- RX580 constraint verified in code: `Display.SpectrumCompute.cs:19` - `CopyResource` on Buffer resources is BROKEN on AMD RX 580; ALL GPU<->CPU transfer MUST use Texture2D + `CopySubresourceRegion` staging. The overlay sheet path already complies (dynamic Texture2D corners via dc.Map, Texture2D sheet, constant buffer only). Any fix must keep this rule.
- First ErrorLog run (8:35:41, adapter 'Radeon RX 580 Series', FL 12.0), v1 one-shot diag: every column `p=-3.4e38` (= float.MinValue reset sentinel from ResetSpectrumPeaks, display.cs:5026/5046/6424), flat `d=-196.3`, `y=620` => all quads degenerate/off-pane, overlay drew NOTHING that frame. v1 fired during warm-up, BEFORE peaks populated, so it missed the symptom frame. Also observed (every enable): first GPU panafill frame fails `D2DERR_WRONG_STATE/WrongState 0x88990001` then self-recovers next frame.
- v2 probe installed in `Display.OverlayMesh.cs` (after the colour else-block; field `_ovlDiagCount[OverlaySlotCount]`): triggers on FIRST populated-peak frame (nSent < decimated width) OR suspect state (nTopBar>=4, nNan>0, nOut>width/3), streams up to 200` lines/slot, dumps summary + corner colour (unpremultiplied cr/cg/cb, ca).
- v2 run (8:48:20): `GPU overlay diag[2] rx1 fill cols=2444 grid=[-152..-50] data[-196.3..-196.3] peak[min/max/mean=-196.3] nSent=0 nNan=0 nUp=0 nTopBar=0 nOut=2444 col=1.32/0.00/1.32 a=0.76 |c0(d=-196.3,p=-196.3,y=666)...`
  - Analysis: NO LIVE SIGNAL in the test (floor -196.3 is 44 dB BELOW grid_min=-152). Every column maps to y=666 > H (~465) => scissored off-pane; degenerate quads => sheet stayed black, composite no-op. Peak-state machinery WORKS (peaks track the data floor, no sentinel, nSent=0) => the pink bar does NOT exist on idle frames; it must appear once real peaks reach the grid top.
  - `col=1.32/0.00/1.32 a=0.76` decodes to straight magenta (1.0,0,1.0) @ 76% alpha = user's Active Peak Fill brush (premultiplied 0.76/0/0.76) - colour source is correct.
- Probe flaws to fix BEFORE the next run (self-caught):
  1. `nOut` fires on EVERY idle frame (floor below grid), so the 200-line cap floods on idle and, once the fade-down drops nOut below threshold, the stream STOPS right at the signal frame - the exact frame we need is missed. Add signal-presence to the suspect predicate (`dMax > grid_min + 10` OR `nUp > 0`) and REMOVE `nOut` as a sole trigger (keep it as a reported field). Re-arm counters per enable so the next session re-streams.
  2. No sheet READBACK yet - we cannot distinguish "pink authored INTO the sheet" vs "D2D composite scaling/positioning creates the pink". Under diag, add an RX580-safe copy-back (staging Texture2D W x 8, `CopySubresourceRegion`, then `dc.Map(Read)`) of THREE horizontal strips (top rows 0-8, middle H/2, bottom H-8) sampled on the signal frame; scan sample columns for magenta-ish pixels (B>200, G<80, R>200) and log locations/counts. API shape verified from Display.WaterfallMesh.cs:425/433 + Display.SpectrumCompute.cs:664/681/748 - Vortice signature `(dst, dstSub, dstX,dstY,dstZ, src, srcSub, Box? srcBox)`, offsets are DESTINATION args, Box is source-only.
- Build note: C# compile succeeded (Thetis.dll emitted), but post-build copy FAILED `MSB3027/MSB3021 Could not copy libfftw3-3.dll ... locked by "Thetis(8824)"` => full build requires Thetis CLOSED first.
- RESOLVED same session. User saw the pink columns start rendering once peaks populated, but they were "large, going left to right instead of vertical, and they grow when tuned to a large signal" - the fill/quads slanted sideways. ROOT CAUSE: the corners texture was `cols x 2` R32G32B32A32 but uploaded with ONE contiguous `Buffer.MemoryCopy` that ignores `RowPitch`. On a padded pitch (RX 580 / driver alignment; cols=2444 => 39104B row is not 256-aligned so row 1 starts at a padded offset), the GPU reads row 1 (corner2,corner3 texels) shifted N texels, so every quad's right edge connected to a DIFFERENT column's corners => near-horizontal slivers on flat noise growing into diagonal slashes at strong signals. FIX (Display.OverlayMesh.cs): corners texture is now a SINGLE row of `2*cols` texels (`Width=cols*2, Height=1`); vs_ovl loads `cA = Load(int3(col*2,0,0))`, `cB = Load(int3(col*2+1,0,0))`. Single-row flat copy is exactly the row contents, so RowPitch padding can never shift texel pairs. Added one-shot diag `GPU overlay: corners map rowpitch=... rowbytes=...` under mesh diag. Verified WORKING by user 2026-08-27 ("its working correct now"). Lesson (matches Pan3DMesh.cs:650 which DOES honour `mapped.RowPitch` row-by-row): any multi-row dynamic texture upload MUST respect RowPitch - either copy per-row with `row * mapped.RowPitch` offsets or collapse to a single row.
- NEXT SESSION: (a) probe still streams/floods on flat-idle frames (nOut>width/3 always true below-grid); not harmful, just noisy - improve suspect predicate to signal-presence (`dMax > grid_min + 10` OR `nUp > 0`) if the 200-line cap gets in the way; (b) strip readback idea (sheet top/mid/bottom rows via RX580-safe CopySubresourceRegion) is now OPTIONAL - geometry verified correct; only worth adding if a future visual regression needs sheet-vs-composite attribution. Do NOT port `%TEMP%\opencode\fix_overlay.ps1` (evidence only).
- EXTENDED TO 3D PAN same session (user: "can we implement on 3d pan"): the dispatch gate was `!draw3DHistory && ...` so the overlay silently stayed CPU on the 3D pan (user's "no visual difference, it's probably in CPU mode" was correct). But the D2D peak strokes it replaces DO run in 3D (shared column loop, display.cs:6214-6257, `spectralPeakPoint.Y = live3DBottomY - pow(sPeak,zCurve)*ridge`), and the overlay geometry + dispatcher already had the `live3DMapping/live3DBottomY/live3DRidge/live3DZCurve` plumbing - dead code. FIX (`display.cs` dispatch): gate widened to `bSpectralPeakHold && spectralPeaks != null && (draw3DHistory || bSpecFillMesh || !pan_fill)`. 2D behaviour unchanged; on 3D the peaks stack straight over the surface (no fill sheet there), and the overlay draws ONLY the LIVE front-edge peaks from current spectralPeaks - historical slab lines are never touched (user requirement). RUNTIME-VERIFIED 9:09 on RX 580: log sequence "GPU overlay: blocked by guard (armed=False, overlay still off)" -> "GPU overlay: corners map rowpitch=78848 rowbytes=78208 (single-row, pitch-independent)" -> "GPU overlay diag[1] rx1 fill cols=2444 grid=[-140..-60] data[-138.1..-78.8] peak[min=-133.9,max=-76.1] nUp=1585 nOut=0 |cmax@1221(y=289)" -> "GPU overlay mesh active (14664 verts, fill mode, rx1)". The rowpitch line is conclusive: 78848-78208 = 640B pad = 40 texel shift on the ORIGINAL cols x 2 layout - the sideways-bar bug, fully proven. User confirmed visual parity on the 3D pan.
- LOG SPAM CLEANUP (same session): removed the v2 streaming probe (200-line `diag[N]` flood) and the redundant `rowpitch=` line + their fields (`_ovlDiagCount`, `_ovlRPLogged`). Remaining overlay logging is one-shot per slot/session and self-gated by `MeshDiagLogEnabled` (common.cs:624): `GPU overlay mesh active (...)`, `GPU overlay: blocked by guard / blocked - peakHold=...`, `GPU overlay: render failed ...`, and a single `GPU overlay diag rxN ...` summary line on the first populated frame (data/peak ranges, nNan/nUp/nTopBar/nOut, brush colour, sample columns). Root-cause forensics preserved in this file only.

### 2026-08-19 (shutdown debugging session)
- Root cause of shutdown hang identified: `BinaryFormatter` removed in .NET 10 caused `SaveOptions()` to throw exceptions showing MessageBox dialogs blocking the UI thread for 8-28 seconds per attempt
- ErrorLog.txt showed identical stack trace repeated 10+ times: `BinaryFormatter.Serialize` → `SerializeToBase64` → `MultiMeterIO.GetSaveData` → `SaveOptions` → `Console_Closing`
- Shutdown log revealed Console_Closing was re-entering 3 times due to lack of re-entry guard
- Fixed BinaryFormatter → System.Text.Json in all serialization paths (common.cs, DiversityForm.cs, ucOtherButtonsOptionsGrid.cs)
- Committed as `f82ce68`

### 2026-08-27 (Rebrand sweep — Bucket A only)
User approved the Bucket A rebrand ("do 1"). All safe, user-visible "Thetis" UI text changed to "SDR-VST3", compiled clean x64 Release (EXIT=0, pre-existing CA1416 only). Files touched: console.cs, display.cs, MeterManager.cs, setup.cs, setup.designer.cs, setup.resx, console.resx, frmAbout.Designer.cs, frmMeterDisplay.cs, frmSeqLog.Designer.cs, ShutdownForm.Designer.cs, clsProgressLog.cs, clsSingleInstance.cs, Firewall.cs, NetworkThrottle.cs, radio.cs, Midi2Cat/Midi2CatSetupForm.cs.
- Captions/titles → "SDR-VST3": Startup Log, About (form title + logo label), Meter window title, shutdown splash, CPU-meter context menu ("Thetis Only" resx text → "SDR-VST3 Only"; control name `thetisOnlyToolStripMenuItem` kept), Midi2Cat startup progress messages, "Thetis DirectX" error captions (display.cs + MeterManager.cs).
- Dialog/MessageBox body text → "SDR-VST3": single-instance, DB model/hardware-version mismatches, ADC overload, fft-wisdom rebuild, FPS profile test, firewall/reset-admin, network-throttle-admin, "Auto Save TX Profile on close", "Clear DumpCap folder on startup", settings tooltips (VFOa duplication, TX inhibit, auto-launch-close, RNnoise model), setup.resx labelTS295 ("after SDR-VST3 is restarted").
- Deliberately left (Bucket B): AssemblyName/RootNamespace/AssemblyTitle/Product = Thetis (exe identity), TCI/CAT protocol strings (`sProtocol`, `sendPingFrame/PingFrame`), `#Thetis TCP/IP Cat`, N1MM `Thetis_1/2` IDs, `Vst3EditorWindowClass`, root-namespace resource names (`Thetis.Properties.Resources`, `Thetis.Resources.*`, `thetis-logo1/_logo2`), `"ThetisVersion"` data key, `thetisOnlyToolStripMenuItem`/`lblSkinThetisVersion` control names, "Thetis.exe" firewall-help filename refs, all `, Thetis, Version=` type-qualified resx strings (control deserialization), `Before ThetisBotDiscord.Disconnect()` log id.
- Deliberately left (Bucket C): About-box link list (upstream GitHub/Discord/manual pages), frmAbout.resx GPL attribution, both `User-Agent = "Thetis v..."` sites (skin/image servers), skin "Min Thetis Version" metadata label, test bundle dir names.

---

## Shutdown Hang — Root Cause Analysis

### Fixed Issues (Phase 1.5)

| Issue | Location | Fix |
|---|---|---|
| MultiMeterIO `Thread.Join()` no timeout | `MeterManager.cs:42263,42523,42762,42959` | Added `Join(2000)` to all 4 Stop() methods |
| PSForm `_ampViewDone.Wait()` no timeout | `PSForm.cs:431` | Added `Wait(2000)`, removed `Thread.Abort()` (.NET 10 unsupported) |
| MeterManager.Shutdown nWait overflow | `MeterManager.cs:3558-3569` | Capped nWait at 500ms |
| Display thread blocks process exit | `console.cs:1090` | Set `IsBackground = true` |

### Remaining Issues (lower priority)

| Issue | Location | Risk |
|---|---|---|
| `_objDX2Lock` contention on shutdown | `display.cs:3329` → `console.cs:28241` | After Join(500) times out, ShutdownDX2D blocks on lock held by display thread in GPU Present |
| PS thread cross-thread UI access | `PSForm.cs:562-587` | `CheckForIllegalCrossThreadCalls=false` suppresses but could crash during disposal |
| TCIServer `InvokeOnConsole` potential deadlock | `TCIServer.cs:8342` | If using synchronous Invoke during shutdown |

---

## 3D Panadapter Modernization Plan (Phase 3)

Documented 2026-08-20 after reviewing the current implementation.

### Current Implementation

`DrawPanadapter3DHistoryDX2D` (display.cs:6018–6286) — *simulated* 3D, no real geometry:

- History of spectrum frames in `_3dHistoryBuffer` (ring buffer, pushed at `_3dPushIntervalTicks` ≈ 25 FPS, decimated to `nDecimatedWidth` columns)
- Rows drawn back-to-front with perspective: narrower width toward back (`_pan3DPerspective`), rising baseline (`_pan3DDepth`), smoothstep depth curve, uniform ridge height (`_pan3DRidgeHeight`)
- **PASS 1**: per-column vertical `DrawLine` fill (trace → absolute bottom, thickness = `local_Decimation`)
- **PASS 2**: ridge outline hairline pass, brighter crest
- Coloring: waterfall-sync palette / user gradient (pre-sampled 64-entry palette) / line color × brightness; depth dimming + linear haze blend toward background
- Per-frame brush cache dictionary avoids COM brush churn
- Existing user knobs: `_pan3DPerspective`, `_pan3DDepth`, `_pan3DRidgeHeight`, `_pan3DDepthFade`, `_pan3DLineCount`, `_pan3DLineColor`, `_pan3DWaterfallSync`
- Cost: ~rows × columns × 2 DrawLine calls per frame (e.g. 100 × 800 × 2 ≈ 160K calls) — all CPU-side D2D command submission
- Known artifact: "edge stepping" — jagged left/right silhouette because each row's inset differs (`inset = W*(1-rowWidthFrac)*0.5`) and outline runs under an `AntialiasMode.Aliased` clip

### Tier 1 — Quick wins inside existing D2D renderer (days each)

1. **Temporal interpolation between frames** — push rate is ~25 FPS, display is 60; interpolate 1–2 sub-rows between adjacent history frames so the surface morphs continuously. Biggest perceived-smoothness win per LOC.
2. **Edge smoothing** — draw row outlines as filled path geometries and/or enable per-primitive anti-aliasing for the outline pass; turns the staircase silhouette into clean converging edges.
3. **Side walls / end caps** — fill left/right edge quads (front trace → back trace → baseline) with darkened surface color; reads as a solid object (SDR Console / Aether style).
4. **Perceptual colormaps** — add Turbo/Viridis/Inferno lookup tables alongside existing gradient/waterfall-sync plumbing; Turbo makes weak signals pop against noise floor.
5. **Perspective grid floor** — faint receding gridlines under the surface matching row baselines; cheap depth cue.
6. **Exponential fog** — replace linear haze (`haze = tSmooth * strength * 0.35`) with `1 - exp(-k*t)`; saturating fog looks more natural.

### Tier 2 — Bloom/glow (medium)

Upgrade render target to `ID2D1DeviceContext` (Vortice supports; D2D factory/device already exist). Effects graph: extract bright crests → Gaussian blur → additive composite = modern "neon ridge" look. Care needed with existing present path and render-target type used elsewhere.

### Tier 3 — True GPU mesh renderer (flagship deliverable)

Replace line emulation with a Direct3D11 triangle mesh; the port already owns an `ID3D11Device` + swap chain:

- Vertex buffer: X=frequency, Y=amplitude, Z=depth; a few thousand vertices updated per frame — trivial GPU load
- Pixel shader: palette lookup by height, exponential fog by depth, normal-based lighting (finite-difference normals; slopes facing virtual light brighten — what makes terrain renders look solid)
- MSAA gives free anti-aliasing; edge stepping becomes geometrically impossible
- 200+ rows at 60 FPS with near-zero CPU cost vs today's ~160K DrawLine calls/frame
- Coexists with D2D on the shared swap chain: D3D draws surface, D2D overlays grid/text/controls
- Foundation for compute-shader spectrum processing (final Phase 3 item)

**STATUS 2026-08-22 — FIRST SLICE IMPLEMENTED + RUNTIME VERIFIED** (implementation notes + D3D11 lessons in session history; committed `89f05af` 2026-08-23):
- Done: heightmap texture + UV-grid mesh pipeline (Display.Pan3DMesh.cs), Aether-math VS (zCurve/ridge/perspective), palette PS (slope shade + depth dim + haze), per-frame palette upload mirroring SelectSurfaceColour, pre-BeginDraw dispatch with D2D-line fallback (rules 1–3 honoured), skin-background prepass + scissored plot-strip clear, session-only chkGpuMesh3D toggle.
- Remaining for full parity/polish: side walls/end caps, grid floor/rails, crest hairline, depth-direction slope shading (Tier 1 has these on the D2D path); measure GPU% post-debug; RDP/WARP fallback sanity test. (Commit done `89f05af`.)

**Recommended order**: Tier 1 items compound and ship immediate visible results → then commit to Tier 3 as flagship; Tier 2 optional polish either side of it.

---

## SharpDX → Vortice Migration Reference

### NuGet Packages
| Package | Version |
|---|---|
| `Vortice.Direct2D1` | 3.8.3 |
| `Vortice.Direct3D11` | 3.8.3 |

### Using Statement Changes
| SharpDX | Vortice |
|---|---|
| `using SharpDX;` | `using SharpGen.Runtime;` |
| `using SharpDX.Direct2D1;` | `using Vortice.Direct2D1;` |
| `using SharpDX.Direct3D;` | `using Vortice.Direct3D;` |
| `using SharpDX.Direct3D11;` | `using Vortice.Direct3D11;` |
| `using SharpDX.DXGI;` | `using Vortice.DXGI;` |
| `using SharpDX.Mathematics.Interop;` | `using Vortice.Mathematics;` |
| `using SharpDX.DirectWrite;` | `using Vortice.DirectWrite;` |
| `using SharpDX.WIC;` | `using Vortice.WIC;` |

### Type Mapping Table (DXRenderer scope)
| SharpDX | Vortice | Notes |
|---|---|---|
| `SharpDX.Direct3D11.Device` | `ID3D11Device` | Created via `D3D11.D3D11CreateDevice(...)` static method |
| `SharpDX.DXGI.Factory1` | `IDXGIFactory2` | Created via `DXGI.CreateDXGIFactory1<IDXGIFactory2>()` |
| `Surface` | `IDXGISurface` | |
| `SwapChain` | `IDXGISwapChain` | |
| `SwapChain1` | `IDXGISwapChain1` | |
| `RenderTarget` | `ID2D1RenderTarget` | Created via `factory.CreateDxgiSurfaceRenderTarget(surface, props)` |
| `SharpDX.Direct2D1.Factory` | `ID2D1Factory` | Created via `D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded)` |
| `SharpDX.Direct2D1.Brush` | `ID2D1Brush` | |
| `SharpDX.Direct2D1.SolidColorBrush` | `ID2D1SolidColorBrush` | Created via `renderTarget.CreateSolidColorBrush(color)` |
| `SharpDX.Direct2D1.Bitmap` | `ID2D1Bitmap` | Created via `renderTarget.CreateBitmap(...)` |
| `StrokeStyle` | `ID2D1StrokeStyle` | Created via `factory.CreateStrokeStyle(props)` |
| `TextFormat` (DirectWrite) | `IDWriteTextFormat` | Created via `factory.CreateTextFormat(name, weight, style, FontStretch.Normal, size)` |
| `SharpDX.DirectWrite.Factory` | `IDWriteFactory` | Created via `DWrite.DWriteCreateFactory<IDWriteFactory>()` |
| `SharpDX.WIC.ImagingFactory` | `IWICImagingFactory` | Created via `new IWICImagingFactory()` |
| `Vector2` | `System.Numerics.Vector2` | |
| `Matrix3x2` | `System.Numerics.Matrix3x2` | `.TranslationVector` → `.Translation` (Vector3, not Vector2) |
| `Color4` | `Vortice.Mathematics.Color4` | |
| `RawVector2` | `Vortice.Mathematics.RawVector2` | |
| `RawColor4` | `Vortice.Mathematics.RawColor4` | |
| `RectangleF` | `System.Drawing.RectangleF` (display.cs usage) / `Vortice.RawRectF` for raw D2D calls | `Matrix3x2` = `System.Numerics.Matrix3x2`; `RawRect`/`RawRectF` live in the Vortice.DirectX assembly | |
| `Size2` | `Vortice.Mathematics.SizeI` | |
| `Utilities.Dispose(ref x)` | `x?.Dispose(); x = null;` | |
| `SharpDXException` | `SharpGenException` | |
| `TryPresent(vblanks, flags)` | `Present(vblanks, flags)` | |
| `Bitmap.FromWicBitmap(rt, converter)` | `rt.CreateBitmapFromWicBitmap(converter)` | |
| `new SolidColorBrush(rt, color)` | `rt.CreateSolidColorBrush(color)` | |
| `new Bitmap(rt, size, props)` | `rt.CreateBitmap(size, props)` | |
| `new Ellipse(center, rx, ry)` | `new Ellipse(center, rx, ry)` | Verify constructor signature |

### Key API Pattern Changes
1. **Device creation**: `new Device(...)` → `D3D11.D3D11CreateDevice(adapter, driverType, flags, featureLevels, out device, out featureLevel, out context)`
2. **SwapChain**: `new SwapChain(factory, device, desc)` → `factory2.CreateSwapChainForHwnd(device, hwnd, desc1)` using `SwapChainDescription1` (no nested ModeDescription)
3. **RenderTarget**: `new RenderTarget(factory, surface, props)` → `factory.CreateDxgiSurfaceRenderTarget(surface, props)`
4. **Exception handling**: `SharpDXException` → `SharpGenException`, `ex.ResultCode` → `ex.Result`
5. **FontStyle ambiguity**: Use `using FontStyle = System.Drawing.FontStyle;` alias since both `Vortice.DirectWrite.FontStyle` and `System.Drawing.FontStyle` exist
6. **WIC**: `IWICStream` wrapper needed for managed `MemoryStream` → `factory.CreateStream(stream, FileAccess.Read)`

### Migration Strategy for DXRenderer
The DXRenderer inner class (MeterManager.cs:31642-41708) contains **all** SharpDX code in a self-contained ~10K line class with ~481 references. Strategy:

1. **Replace using statements** with Vortice equivalents + `using FontStyle = System.Drawing.FontStyle;`
2. **Bulk type renames** via script (most are simple namespace swaps)
3. **Fix field declarations** (types change: `Surface`→`IDXGISurface`, etc.)
4. **Rewrite `dxInit()`** — device creation, swapchain, D2D factory, render target (most complex)
5. **Rewrite `ShutdownDX()`** — remove `Utilities.Dispose(ref x)`, use `x?.Dispose(); x = null;`
6. **Rewrite helper methods** — `convertColour`, `getDXBrushForColour`, `bitmapFromSystemBitmap`, `buildDXFonts`
7. **Fix render methods** — ~25 methods, mostly mechanical type renames + brush/bitmap creation pattern changes

## 2026-08-23 - Installer slice: WiX MSI updated for .NET 10 + version 4.6 (DONE, MSI BUILT)

### Version
- Console/AssemblyInfo.cs: AssemblyVersion 4.5 -> 4.6, FileVersion 4.5.0.0 -> 4.6.0.0.
- MSI ProductVersion is BOUND to the exe's VERSIONINFO via !(bind.FileVersion.ThetisEXE) in Product.wxs - no separate version to bump for releases.

### Installer changes (Project Files/Source/Thetis-Installer/)
- **Product.wxs**: replaced .NET Framework 4.8 launch condition with a .NET 10 Desktop Runtime (x64) check. FIRST ATTEMPT (RegistrySearch on HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App, value names 10.0/10.1/10.2) FAILED IN THE FIELD: those keys are only written by some installers; VS/winget/zip installs never create them, so a machine WITH the runtime got blocked. FINAL: filesystem AppSearch - DrLocator [ProgramFiles64Folder]dotnet\shared\Microsoft.WindowsDesktop.App Depth=1 + FileSearch for PresentationCore.dll MinVersion 10.0.0 (WPF marker present in every WindowsDesktop band; its FileVersion tracks the runtime, e.g. 10.0.10 -> 10.0.1026.x). SECOND FIELD FAILURE + FIX: giving the nested FileSearch an @Id makes WiX emit a CHAINED DrLocator pair (parent dir row + child signature row); msiexec silently fails the chain (log notes 1322-empty x2 then 1325 file-not-found) even though tables look correct - verified by diffing against a standalone probe MSI where an Id-less nested FileSearch flattens into ONE Signature+DrLocator row and resolves fine. ALWAYS omit FileSearch/@Id here. WiX v3 preprocessor has NO .Replace() string function - do not use foreach+Replace tricks (CNDL0235). THIRD FIELD FAILURE + FIX: installed app crashed at startup (0xE0434352, FileNotFoundException System.IO.Ports 9.0.0.0) while identical bytes ran fine from bin - root cause: Thetis.deps.json runtimeTargets PINS System.IO.Ports (net9.0), System.Management (net10.0), System.ServiceProcess.ServiceController (netstandard2.0) and libSkiaSharp.dll (win-x64 native) to the runtimes\win\... / runtimes\win-x64\native\ SUBTREE; hostfxr never falls back to the identical root-level copies, so the MSI must ship that subtree. Added RuntimesDir Directory tree + RuntimeAssets ComponentGroup (per-component Directory overrides are ILLEGAL under a ComponentGroup/@Directory - CNDL0062 - hence separate group). SkiaSharp .pdb deliberately NOT shipped (83 MB debug symbols). Diagnosis technique that nailed it: COREHOST_TRACE=1 + COREHOST_TRACEFILE=<path> env vars, run exe, grep trace for 'Probed deps dir and matched'.
- **Product.wxs**: Thetis.exe.config component no longer exists post-migration; replaced with Thetis.dll.config (checksum'd). Config now sits next to managed assembly.
- **Product.wxs**: added missing framework-dependent payload components: Thetis.dll, Thetis.deps.json, Thetis.runtimeconfig.json (all checksum'd), Vortice.{Direct2D1,Direct3D11,D3DCompiler,DXGI,DirectX,Mathematics}.dll, SharpGen.{Runtime,Runtime.COM}.dll, WinForms.DataVisualization{,.Utilities}.dll, System.{ComponentModel.Composition,IO.Ports,Management,ServiceProcess.ServiceController}.dll, VstPluginScanner.{dll,deps.json,runtimeconfig.json}.
- **Thetis-Installer.wixproj**: AfterBuild rename used GetAssemblyIdentity on Thetis.exe - FAILS under .NET 10 because Thetis.exe is a native apphost stub (no assembly manifest; MSB3441). Now reads version from managed Thetis.dll instead.

### Build commands (verified working)
- App: MSBuild.exe Console/Thetis.csproj -p:Configuration=Release -p:Platform=x64 -p:BuildProjectReferences=false
- Installer: MSBuild.exe Thetis-Installer/Thetis-Installer.wixproj -p:Configuration=Release -p:Platform=x64 "-p:WixTargetsPath=C:\Program Files (x86)\MSBuild\Microsoft\WiX\v3.x\Wix.targets"
  (WiX 3.14 toolset at C:\Program Files (x86)\WiX Toolset v3.14; InstallRoot registry resolves WixTasks/extensions automatically)
- Output: Project Files\bin\Installers\SDR-VST3-v4.6.0.0.x64.msi (ProductVersion 4.6.0.0, 636 files, ~83 MB)

### Verification done vs still open
- VERIFIED: light resolves all sources; MSI Property table shows ProductVersion 4.6.0.0; LaunchCondition table contains the .NET 10 Desktop Runtime message; File table contains all 19 new payload files (query File table with LIKE-free SQL or filter locally - MSI SQL has no LIKE).
- NOT YET RUN: actual install/uninstall test on this machine (upgrade-over-4.5 path), and runtime smoke test of installed copy (Thetis was running from bin during build; user to install when convenient).

## 2026-08-23 - Field validation + third-party license attribution (DONE)

### Field results
- .NET 10 runtime detection now passes in the field after the FileSearch/@Id fix (see installer slice above). Install succeeded.
- Startup crash FIXED by shipping the runtimes\ subtree; app "started and working" from Program Files.

### License attribution ("proper" disclosure)
- **lib\licenses\** is the canonical payload folder (18 files): NOTICES.txt index mapping every shipped third-party dll to its license file, verbatim upstream texts for Vortice.Windows, SharpGenTools, SkiaSharp, NAudio, Discord.Net 3.18.0, HtmlAgilityPack (upstream text has no copyright line; header credits ZZZ Projects), Markdig BSD-2, Newtonsoft.Json, WindowsFirewallHelper, ExCSS, RNNoise BSD-3, SVG.NET MS-PL (full text), FFTW GPLv2+ notice, .NET Foundation MIT umbrella for System./Microsoft./WinForms.DataVisualization assemblies, FTDI D2XX distribution notice, plus copies of local PortAudio MIT and libspecbleach LGPL-2.1 texts.
- **Product.wxs**: `<?define LicensesPath = "..\..\lib\licenses\" ?>` + `LicensesDir` Directory under INSTALLFOLDER + `LicensesComponents` ComponentGroup (18 comps, Guid="*", KeyPath=yes) + `<ComponentGroupRef Id="LicensesComponents"/>` next to RuntimeAssets. Installs to [INSTALLFOLDER]\Licenses\.
- **GNU_GENERAL_PUBLIC_LICENSE.rtf** (the installer license page): appended a bold THIRD-PARTY SOFTWARE NOTICES section listing each component + copyright + license shorthand; braces balanced (67/67).
- **Source-code attribution**: all six files that consume Vortice.Windows/SharpGenTools carry a short MIT attribution comment above their Vortice using-block (display.cs ~line 63, MeterManager.cs, DXVorticeCompat.cs, Display.Pan3DMesh.cs / Pan2DMesh.cs / WaterfallMesh.cs). Only Vortice/SharpGen are NEW to this migration (replaced SharpDX); every other third-party package predates it and is disclosed via installer + Licenses folder instead. App rebuilds clean.
- MSI rebuilt: SDR-VST3-v4.6.0.0.x64.msi now 658 files (+18 licenses); verified via Component table join Directory_=LicensesDir -> all 18 present. AUDIT GOTCHA: SELECT * FROM File column order is File, Component_, FileName... - GetString(2) is COMPONENT not filename; filter on GetString(3) or join via Component.Directory_.

## Future upgrade roadmap (parked 2026-08-23 - revisit once v4.6 field stability is confirmed)
Deliberately NOT started; candidate list agreed with user, priority order within groups:

1. Runtime log toggles - DONE 2026-08-24: Setup -> Options-3 -> Diagnostics -> "Log GPU mesh events" checkbox drives Common.MeshDiagLogEnabled at runtime (common.cs); sink writes into ErrorLog.txt, each toggle records an ENABLED/disabled marker line; state persists via SaveForm/RestoreForm and is re-applied on startup (setup.cs chkMeshDiagLog_CheckedChanged).
2. Crash safety net - DONE 2026-08-25: Common.SaveCrashReport(Exception) in common.cs writes a standalone .crash file to crashes/ subdirectory under the log path (falls back to %APPDATA%\OpenHPSDR\SDR-VST3-x64\crashes\ if log path not yet set). Report includes timestamp, version, render path, OS, CLR version, thread info, and full InnerException chain. Global handlers updated: Application.ThreadException (console.cs:1396) and AppDomain.CurrentDomain.UnhandledException (console.cs:1401) now call SaveCrashReport + ShowCrashDialog. Dialog shows the crash file path with Yes/No to open the folder in Explorer (via /select). Existing Common.LogException calls preserved for ErrorLog.txt continuity.
3. Check-for-updates - DONE 2026-08-24: console.cs "update check" region; ~5s after Console_Shown an async GET hits api.github.com/repos/nubbyless/SDR-VST3/releases/latest (10s timeout, UA header required by GitHub, silent on any failure); tag vs Application.ProductVersion compared via loose 4-part version parser (handles v-prefix and suffixes). Status-bar label seats right of the GPU meter, bold 11pt, LimeGreen "Up to date : vX" / Red "Update available : vX"; click opens the release html_url in the browser.
4. GPU paths to default - once 3D/2D mesh + waterfall ring proven stable in the field, flip hardware path to default; keep D2D fallback as automatic recovery only (GPU fallback rule 1 stays).
5. GPU spectrum overlays - peak-hold/annotations/markers drawn through the existing D3D11 pipeline (cheap now that mesh passes exist).
6. Code health splits - MeterManager.cs ~45k lines and display.cs ~14k lines into focused partials/modules; retire DXVorticeCompat shim when confident nothing needs the SharpDX surface.
7. CI releases - DONE 2026-08-25: .github/workflows/release.yml triggers on v* tag push; runs on windows-latest; sets up MSBuild + .NET 10 SDK; restores NuGet packages; builds full solution (Thetis_VS2026.sln, Release/x64); builds WiX installer (Thetis-Installer.wixproj); finds MSI in Project Files/bin/Installers/; creates GitHub Release with MSI attached + auto-generated release notes via softprops/action-gh-release@v2. Usage: `git tag v4.7 && git push origin v4.7`.
8. Error-dialog capture logging - DONE 2026-08-25: Common.ReportError(caption, message[, exception]) in common.cs shows the error MessageBox AND writes a structured entry to ErrorLog.txt (timestamp, app version via GetVerNum, render path via Display.RenderPathString with try/catch for early startup, caption, message, exception+stacktrace when available). Always-on, no toggle. 24 high-value MessageBox.Show sites converted: display.cs (5 — DX init, resize, shutdown, render failures), console.cs startup (10 — version check, DB load x3, PortAudio x2, Main catch x2, TCPIP/TCI servers), console.cs CAT/PTT (9 — CAT/Andromeda/Aries/Ganymede/CAT2/CAT3/CAT4 setters + PTT init). Pairs with #2: #2 catches UNHANDLED exceptions globally, this captures HANDLED errors that previously only flashed a dialog and vanished.

Top picks if only two get done first: #1 (runtime log toggle) and #7 (CI releases) - low effort, recurring value. **Update 2026-08-25**: #1, #2, #3, #7, #8 done. Remaining: #4 (GPU default), #5 (GPU overlays), #6 (code splits).


