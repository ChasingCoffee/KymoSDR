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

**Goal**: Modern GPU-accelerated rendering leveraging Vortice.

- [ ] GPU compute shaders for spectrum/waterfall
- [ ] GPU mesh-based 3D panadapter (replacing per-column DrawLine)
- [ ] Fix edge stepping in 3D panadapter (open issue from HANDOFF.md)

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
- [ ] NU1701 warnings (4) from SharpDX — expected, resolved in Phase 2
- [ ] Output verification: native DLLs (fftw, rnnoise) need manual copy

### Phase 2
- [ ] SharpDX packages removed
- [ ] Vortice packages added
- [ ] display.cs ported to Vortice
- [ ] MeterManager.cs DXRenderer ported to Vortice
- [ ] All display modes functional

### Phase 3
- [ ] GPU compute shaders for spectrum
- [ ] GPU mesh 3D panadapter
- [ ] Edge stepping fix

---

## Session History

### 2026-08-19
- Created `net10-migration` branch from `wdsp2-conversion` (at `f4cca3d`)
- Completed full investigation of .NET 10 migration feasibility
- Mapped all 69 NuGet packages for compatibility
- Analyzed AmpView chart replacement scope
- Documented all build concerns (PreBuild, PostBuild, AssemblyInfo)
- Created this handoff document
