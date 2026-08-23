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

- [ ] Tier 1: Quick visual wins inside existing D2D renderer (temporal interpolation, edge smoothing, side walls, perceptual colormaps, grid floor, exponential fog)
- [ ] **MANDATORY: GPU→CPU fallback architecture (applies to ALL GPU features below)** — see "GPU Fallback Architecture Requirement" section
- [x] Tier 2: Bloom/glow via ID2D1DeviceContext effects graph — **DONE + RUNTIME VERIFIED 2026-08-22 (panadapter trace glow "Line Glow", HW-only)**
- [ ] Tier 3: GPU mesh-based 3D panadapter (replacing per-column DrawLine; fixes edge stepping geometrically) — **FIRST SLICE DONE + RUNTIME VERIFIED 2026-08-22** (surface renders on HW, fluid/solid, colormaps match non-mesh displays after BGRA fix; skin background preserved via prepass sandwich). Remaining: side walls/grid floor/crest hairline/depth-slope-shading parity + GPU% check. See session history.
- [ ] GPU compute shaders for spectrum/waterfall

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
- [ ] Broader rebrand sweep — OPEN DECISION (see 2026-08-20 session history: Bucket A safe UI text / Bucket B functional identifiers / Bucket C upstream refs)
- [ ] DXCC/country prefix lookup runtime verification (validates cty.txt regen)

### Phase 3
- [x] Tier 1: temporal interpolation, edge smoothing, side walls, Turbo/Viridis colormaps, grid floor, exponential fog — **CODE COMPLETE 2026-08-20 (below), RUNTIME VERIFIED 2026-08-22 (user sign-off)**
- [x] **BLOCKER RESOLVED 2026-08-22: uncheck-Waterfall-Sync crash — root cause was `Pan3DLineColor` setter disposing `m_bDX2_3d_fill_brush` WITHOUT nulling it → Classic+Sync-OFF frame drew through a disposed COM brush. Fixed (dispose+null + stopsColl leak); user verified "seems fixed so far". Dumps/WER key intentionally left in place for now. See 2026-08-22 session entries**
- [x] Tier 2: bloom/glow (ID2D1DeviceContext effects graph) — **DONE + RUNTIME VERIFIED 2026-08-22 (panadapter trace glow "Line Glow", HW-only)**
- [ ] Tier 3: GPU mesh 3D panadapter (replaces per-column DrawLine; fixes edge stepping) — **FIRST SLICE RUNTIME VERIFIED 2026-08-22** (see Phase-3 roadmap line + session history; polish items remain, NOT yet committed)
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
Build clean x64 Release (EXIT=0). **User verified: "yes its working looks very fluid and solid", then after the BGRA palette fix "palletes are correct color now".** New file `Console/Display.Pan3DMesh.cs` (~730 lines, partial class Display) + `display.cs` made `partial`; Vortice.D3DCompiler 3.8.3 package added to Thetis.csproj (+ explicit `<Compile Include>` — csproj uses explicit Compile items despite SDK-style). Session-only toggle `chkGpuMesh3D` ("GPU 3D mesh (exp.)") in grpDisplayDriverEngine slot (8,47) TabIndex 52; handler pushes `Display.GpuMeshEnabled` live; registered in ForceAllEvents. **NOT YET COMMITTED** (all Tier 3 work; commit next session on user go-ahead).

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

### 2026-08-19 (shutdown debugging session)
- Root cause of shutdown hang identified: `BinaryFormatter` removed in .NET 10 caused `SaveOptions()` to throw exceptions showing MessageBox dialogs blocking the UI thread for 8-28 seconds per attempt
- ErrorLog.txt showed identical stack trace repeated 10+ times: `BinaryFormatter.Serialize` → `SerializeToBase64` → `MultiMeterIO.GetSaveData` → `SaveOptions` → `Console_Closing`
- Shutdown log revealed Console_Closing was re-entering 3 times due to lack of re-entry guard
- Fixed BinaryFormatter → System.Text.Json in all serialization paths (common.cs, DiversityForm.cs, ucOtherButtonsOptionsGrid.cs)
- Committed as `f82ce68`

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

**STATUS 2026-08-22 — FIRST SLICE IMPLEMENTED + RUNTIME VERIFIED** (implementation notes + D3D11 lessons in session history; NOT yet committed):
- Done: heightmap texture + UV-grid mesh pipeline (Display.Pan3DMesh.cs), Aether-math VS (zCurve/ridge/perspective), palette PS (slope shade + depth dim + haze), per-frame palette upload mirroring SelectSurfaceColour, pre-BeginDraw dispatch with D2D-line fallback (rules 1–3 honoured), skin-background prepass + scissored plot-strip clear, session-only chkGpuMesh3D toggle.
- Remaining for full parity/polish: side walls/end caps, grid floor/rails, crest hairline, depth-direction slope shading (Tier 1 has these on the D2D path); measure GPU% post-debug; RDP/WARP fallback sanity test; commit.

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
