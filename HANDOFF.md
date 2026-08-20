# SESSION HANDOFF - SDR-VST3 (Thetis VST3 Plugin)

Created: 2026-08-16
Updated: 2026-08-18
Branch: `wdsp2-conversion` (clean, last commit `ce60724`)
Version: v4.4

## Project Overview

SDR-VST3 is a VST3 plugin adaptation of the Thetis SDR software (HPSDR/ANAN
radios). It wraps the Thetis console as a VST3 plugin so it can run inside DAWs
and other VST3 hosts. The project is a fork of Thetis 2.10.x with WDSP 2.00
integration, VST3 hosting capabilities, and SDR-VST3 branding.

## Current State

**WDSP 2.00 conversion: COMPLETE** (Phases 1-3 of `WDSP2_CONVERSION_PLAN.md`).

**TX Profile Reordering: COMPLETE**.

**3D Panadapter: COMPLETE** — rendering, UI, performance, visual polish, waterfall sync all done.

## 3D Panadapter - Full Implementation

AetherSDR-inspired stacked-trace 3D panadapter display. Draws historical
spectrum traces as a perspective stacked-trace surface using painter's algorithm
(back-to-front). Implemented using SharpDX Direct2D1 CPU rendering with
perspective geometry matched to AetherSDR's `DssRenderer` constants.

### Rendering Geometry (matched to AetherSDR DssRenderer.h)

- Trace Y is RELATIVE to rising baseline: `yPx = baselineY - strength * maxRidge`
- Fill goes to absolute bottom (`bottomY`), not rising baseline — painter's algorithm occlusion
- Front rows wider and sit lower, occluding back rows
- Signal strength (0=noise floor, 1=peak) drives ridge height above floor
- **Smoothstep curve** (`t² * (3-2t)`) applied to ALL depth parameters for gradual front-to-back fade instead of linear transition
- **Uniform ridge height**: `maxRidge = frontMaxRidge` (not scaled by rowWidthFrac) — same peak height front to back, only width narrows

### Waterfall Sync

When enabled, the 3D panadapter uses the waterfall's palette and thresholds for
coloring instead of the static line color/gradient. Also syncs 3D push speed to
the waterfall's actual scroll rate.

- `Pan3DWaterfallSync` property (bool, default true)
- `GetWaterfallColor()` helper replicates waterfall color logic for all schemes
  (enhanced, Custom, SPECTRAN, grayscale fallback) — eliminates dependency on
  the `_rx1_waterfall_grad` array which is only populated for Custom scheme
- Live front trace fill + line also use waterfall colors when sync is on
- 3D push interval auto-reads `getWaterfallLineIntervalMs(1)` when sync is on,
  bypassing the manual Speed spinner
- Live trace waterfall sync requires `_pan3DEnabled && _pan3DWaterfallSync &&
  rx == 1 && !local_mox` — does NOT affect 2D panadapter

### Performance Optimization

**Brush cache**: `Dictionary<int, SolidColorBrush>` keyed by packed RGBA avoids
creating/disposing 36,000+ COM brushes per frame (60 lines × 600+ columns).
Cache is cleared at frame end. Reduces to ~few hundred unique brushes per frame.
Also used for live front trace (`liveWfBrushCache`).

**3D row push throttle**: Configurable via `Pan3DSpeed` property (10–60 FPS).
When waterfall sync is on, this is overridden to match the waterfall's actual
line interval. `_3dPushIntervalTicks` is a `static long` (not const).

### Visual Polish (tuned iteratively)

**Smoothstep depth curve** (replaces linear):
```
tSmooth = depthFrac² × (3 - 2 × depthFrac)
```
Applied to: rowWidthFrac, baselineY, maxRidge, dim, haze, alpha, outlineAlpha.
Keeps front rows close to live trace, then gradually transitions to back.

**Depth dimming** (gentle):
```
dim = 0.72 + 0.28 × (1 - tSmooth)    // floor 72%, front 100%
```

**Atmospheric haze** (subtle):
```
haze = tSmooth × hazeStrength × 0.35  // 35% of user setting
```

**Alpha fade** (minimal):
```
alpha = 1.0 - tSmooth × 0.15          // front 100%, back 85%
```

**Live pan fill gradient** (matches AetherSDR):
- Vertical `LinearGradientBrush` from trace to plot bottom
- 55% alpha at top (near trace) → 16% alpha at bottom
- Replaces flat `SolidColorBrush` — one brush for all columns

**Render order fix**:
- 3D history now draws BEFORE the grid (was after)
- Filters and cursor lines render ON TOP of 3D history
- Requires pre-computing `grid_max`/`grid_min` before grid call (4 lines, not duplication)

### Properties

| Property | Range | Default | Description |
|---|---|---|---|
| `Pan3DEnabled` | bool | false | Enable/disable 3D mode |
| `Pan3DPerspective` | 0.1–1.0 | 0.60 | Back row width fraction (kBackWidthFrac) |
| `Pan3DDepth` | 0.0–1.0 | 0.58 | Baseline rise fraction (kDepthSpanFrac) |
| `Pan3DRidgeHeight` | 0.1–1.0 | 0.46 | Front ridge height fraction (kFrontMaxRidgeFrac) |
| `Pan3DDepthFade` | 0.0–1.0 | 0.16 | Atmospheric haze strength (kHaze) |
| `Pan3DLineColor` | Color | Aquamarine | Ridge fill/outline color (when sync off) |
| `Pan3DLineCount` | int | 35 | Number of history rows (max 60) |
| `Pan3DWaterfallSync` | bool | true | Use waterfall palette for 3D colors |
| `Pan3DSpeed` | int | 25 | 3D push FPS (10–60), overridden by waterfall sync |

### UI Controls

In Setup > Display > General tab, `grp3DPanadapter` group box (166×244):

| Control | Y pos | Type | Range | Default |
|---|---|---|---|---|
| Enabled | 18 | CheckBox | on/off | off |
| Waterfall Sync | 38 | CheckBox | on/off | on |
| Perspective | 60 | Spinner | 0.10–1.00, step 0.05 | 0.60 |
| Depth | 80 | Spinner | 0.00–1.00, step 0.05 | 0.58 |
| Ridge Ht | 100 | Spinner | 0.10–1.00, step 0.05 | 0.46 |
| Haze | 120 | Spinner | 0.00–1.00, step 0.05 | 0.16 |
| Depth Lines | 140 | Spinner | 2–60, integer | 35 |
| Speed | 160 | Spinner | 10–60, integer | 25 |
| Color | 180 | ColorButton | color picker | Aquamarine |
| Reset Defaults | 200 | Button | — | — |

Waterfall Sync auto-checks when 3D Enabled is turned on.

### AetherSDR Reference Values Used

From `DssRenderer.h` and `SpectrumWidget.cpp`:
- `kBackWidthFrac = 0.60` → Pan3DPerspective default ✓
- `kDepthSpanFrac = 0.58` → Pan3DDepth default ✓ (was 0.38, updated to match)
- `kFrontMaxRidgeFrac = 0.46` → Pan3DRidgeHeight default ✓
- `kHaze = 0.16` → Pan3DDepthFade default ✓
- Fill gradient: `alphaTop = 200 × 0.70 = 140`, `alphaBot = 60 × 0.70 = 42` (at default fill alpha 0.70)
- 3D active band fill: `105/255 (~41%)`
- `m_fftFillAlpha` default: 0.70
- AetherSDR uses GPU mesh (triangle list) for rendering, not per-column DrawLine

### Files Modified

| File | Changes |
|---|---|
| `display.cs` | Ring buffer + properties, `DrawPanadapter3DHistoryDX2D()`, brush cache, smoothstep depth curve, gradient fill brush, 3D push throttle, render order (3D before grid), haze/dim tuning, `GetWaterfallColor()` helper, waterfall sync for 3D + live trace, `liveWfBrushCache`, `Pan3DSpeed` property |
| `setup.cs` | Event handlers (all controls), init push block, `handleOutdatedOptions()` DB migration, `btn3DResetDefaults_Click`, `chk3DWaterfallSync_CheckedChanged`, `ud3DSpeed_ValueChanged`, 3D enables waterfall sync |
| `setup.designer.cs` | `grp3DPanadapter` (166×244) with all controls + Reset button, field declarations, `grpDisplayDriverEngine` shrunk, `grpSpectralWarningLeds` repositioned |

### Bug Fixes Applied (cumulative)

1. Inverted offset multiplier — oldest lines now furthest back
2. Y=0 smearing — early return guard
3. Color4 parameter order — `(R, G, B, alpha)` not `(alpha, R, G, B)`
4. Pan fill suppression — re-enabled with 3D color support
5. 3D properties not restoring — explicit push after `initializing = false`
6. Trace positions absolute vs relative — now baseline-relative matching AetherSDR
7. Fill target — always goes to absolute bottom for painter's algorithm
8. DB migration — `handleOutdatedOptions()` removes old entries
9. Thread safety lock — `ucLGPicker.GetColourForDBM()` uses `lock(m_objListLocker)`
10. `lineBrush` selection — 3D mode uses gradient without requiring both checkboxes
11. Brush creation crash — added try/catch around gradient palette sampling
12. SEHException crash fix
13. Waterfall sync default colors — created `GetWaterfallColor()` helper for all schemes
14. `liveWfBrushCache` cleanup placement fix (wrong `PopAxisAlignedClip`)
15. Spectral Warning LEDs pushed off setup window — shrunk `grpDisplayDriverEngine`, repositioned
16. Waterfall sync leaking into 2D panadapter — added `_pan3DEnabled` guard

### Known Issues / Not Yet Done

- **Edge stepping**: Back rows at low perspective (< 90) show visible "steps" at
  left/right edges due to thick `DrawLine` column calls overhanging the row
  boundary. AetherSDR avoids this with a GPU mesh (triangle list) where edges
  are inherently smooth. We've tried: per-row clip rects (D2D clip stack depth
  overflow → DX crash), edge alpha fades (reverted), line width scaling
  (`scaledDec`, better but still there), column clamping. Currently reverted to
  original `local_Decimation` line width. This is a fundamental limitation of the
  per-column `DrawLine` approach vs a mesh-based renderer.
- Waterfall sync should potentially also sync the dBm range/thresholds (not just
  colors)
- Consider caching vertical gradient brushes per-row for smoother per-column alpha

## Key File Locations

| File | Path | Purpose |
|---|---|---|
| Display engine | `Project Files/Source/Console/display.cs` | DX2D rendering, 3D panadapter ring buffer + drawing (~line 5974 for DrawPanadapter3DHistoryDX2D), `GetWaterfallColor()` (~line 6980), `getWaterfallLineIntervalMs()` (~line 6570) |
| Setup form | `Project Files/Source/Console/setup.cs` | 3D panadapter controls (~line 12927 for handlers, ~line 660 for init push, ~line 1086 for recovery) |
| Setup designer | `Project Files/Source/Console/setup.designer.cs` | grp3DPanadapter layout (~line 33887), grpDisplayDriverEngine (~line 566), grpSpectralWarningLeds (~line 566) |
| ucLGPicker | `Project Files/Source/Console/ucLGPicker.cs` | `GetColourForDBM()` with lock (~line 764) |

## Build & Run

- **Solution**: `Project Files/Source/Thetis_VS2026.sln` (Release|x64)
- **Output**: `Project Files/bin/x64/Release/` - Thetis.exe, wdsp.dll, etc.
- **MSBuild**: `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" "Project Files\Source\Thetis_VS2026.sln" -p:Configuration=Release -p:Platform=x64 -m -nologo`
- **App data**: `%APPDATA%\OpenHPSDR\SDR-VST3-x64\`

## Git State

- Branch: `wdsp2-conversion` (uncommitted changes)
- Remote: `origin/wdsp2-conversion`
- Last commit: `ce60724`

## Potential Next Steps

- Solve edge stepping (may require switching to filled trapezoid polygons per row instead of per-column DrawLine)
- Waterfall sync dBm range/threshold coupling
- Gradient palette optimization (currently 64-entry pre-sampled)
- Consider caching vertical gradient brushes per-row for smoother per-column alpha
