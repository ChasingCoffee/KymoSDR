# Thetis WDSP 2.00 Conversion Plan

Status: DRAFT - investigation complete, no code changes made yet. REVISED 2026-08-13:
a proven reference implementation of this exact conversion was found and analyzed
(see Section 2.2) - the strategy is now to port from that reference rather than
hand-derive every mod.

## 1. Summary & Strategy

Thetis currently bundles **WDSP 1.29 + Thetis-local modifications** in
`Project Files\Source\wdsp`, built to `wdsp.dll` by `wdsp.vcxproj`
(v145, x64, DynamicLibrary). WDSP 2.00 is an incremental release over 1.29 that
changes ~30 source files and adds several new modules (WBFM, PureSignal V3,
NURBS EQ, phase rotator, RX input decimator, snoop).

**Strategy: replace the entire wdsp Source tree with WDSP 2.00, then re-apply the
known Thetis-local modifications on top.** Wholesale replace is cleaner than
selective merging because (a) upstream changed a wide surface area, (b) the Thetis
modifications are confined to a small, well-identified set (Sections 4-5), and
(c) it guarantees every 2.00 feature/regression arrives intact.

**Skip WDSP 1.30** - its Source tree is byte-identical to 1.29 (verified; only
release docs differ).

## 2. Verified baseline facts (as of investigation)

| Item | Location | Note |
|---|---|---|
| wdsp source | `Project Files\Source\wdsp` | 1.29 base + local mods; initial commit `104eb76` |
| Build | `wdsp.vcxproj` (v145, x64, DLL) | 132 unique `ClCompile` refs, all files present on disk |
| Consumers | C# Console (`dsp.cs`, `specHPSDR.cs`) + ChannelMaster | cmASIO/VstHostBridge/VstAudioHost do NOT link wdsp |
| Version gate | `Versions.cs:23` `_WDSP_VERSION = 1290`; `console.cs:44705-44748` | `GetWDSPVersion()*10` must equal `_WDSP_VERSION`, else refused at startup |
| Wisdom file | `wdspWisdom00` (thetis) vs `wdspWisdom01` (2.00) | C# checks old name at `radio.cs:106` - must be updated |
| Impulse cache | `impulse_cache.c` present in both | invalidated on wisdom rebuild (`radio.cs:139`) |

Upstream reference trees used: `C:\Users\W4YNY\AppData\Local\Temp\opencode\wdsp\`
(`wdsp 1.29`, `wdsp 1.30`, `wdsp 2.00`).

### 2.1 2.00 release-notes vs actual 2.00 source (gotchas)

The 2.00 docs (`readme.md`, `wdsp2.htm`) describe some things that are NOT in
this 2.00 source tree:
- `WDSP_SetCalAndMasks()` does NOT exist anywhere in 2.00 source. The old
  calibration file save/restore thread is still in `calcc.c` (fopen at
  `calcc.c:1857+`), and `PSSaveCorr`/`PSRestoreCorr` still work. So the
  console-side calibration-file workflow is unchanged in practice.
- Treat the HTML release notes as aspirational; verify against source when
  implementing.

### 2.2 Proven reference implementation (REVISED STRATEGY)

The fork **OpenHPSDR-Thetis-Enhanced** (github.com/eu2av/OpenHPSDR-Thetis-Enhanced)
is a Thetis 2.10.x build with WDSP 2.00 already integrated and build-proven
(wdsp.vcxproj 0 errors, full solution 0 errors, exports verified, Thetis boots).
Its base lineage matches ours exactly (identical Versions.cs constants). Cloned to
`C:\Users\W4YNY\AppData\Local\Temp\opencode\thetis-enhanced`. Changelog:
`CHANGELOG_Yurij-eu2av_EN.md` (only the 2026-07-07/08 entries are wdsp-related).

**C-side facts (verified by hash diff against stock 2.00):**
- Their `wdsp` folder == stock 2.00 except **24 modified files** + 7 fork-only
  (rnnr/sbnr/wdsp.rc/wdsp.vcxproj/filters). Our thetis `wdsp` folder is a strict
  subset of theirs; the only files they add are the 8 new 2.00 modules
  (extrapolate, nurbs, nurbs_fit, nurbs_spline, phrot, reshb, snoop, wbfm).
- The 24 modified = Thetis mods re-applied onto 2.00: `amd/anf/anr/emnr/ssql.c`
  (RXAbp1Check 8-arg call sites), `fmsq.c` (eq_impulse Q signature), `TXA.c/.h`,
  `calcc.c/.h` (PS3 wrappers), `eq.c/.h` + `cfcomp.c/.h` (dual-path Q engine),
  `cblock.c/.h` (2-arg xcbl + SetRXACBLPosition), `analyzer.c/.h` (SetPixelRef),
  `RXA.c/.h` (rnnr/sbnr/cbl wiring), `snb.c`, `comm.h`, `iqc.c/.h` (their iqc).
- Our thetis `rnnr.c/h`, `sbnr.c/h` are byte-identical to theirs (2-line comment
  marker only) -> OUR rnnr/sbnr are compatible with their RXA.c as-is. Our thetis
  RXA.c already has the 8-arg RXAbp1Check (lines 903-935).
- **GOTCHA:** the fork ships STOCK 2.00 `FDnoiseIQ.c/h` + `zetaHat.c/h`. Ours are
  Thetis-trained NR data and DIFFER - restore OUR versions when porting.
- Their `version.c` returns 200; their `wisdom.c` writes `wdspWisdom01`.

**C#-side facts (wdsp-related changes only):**
- `Versions.cs` `_WDSP_VERSION = 2000`; console.cs version gate unchanged in shape.
- `radio.cs`: check `wdspWisdom01`; rename legacy `wdspWisdom00` -> 01 (or delete).
- `dsp.cs`: `SetTXAEQProfile`/`SetRXAEQProfile` 5-arg with trailing `double* Q`;
  `SetTXACFCOMPprofile` 7-arg with `Qg`/`Qe`; `GetTXACFCOMPDisplayCompression`
  3-arg `(ch, comp_values, int* ready)` (NOT the 5-arg firstFlag form);
  `GetTXACFCOMPGainAndMask` removed; added `SetTXAPHROTAutoMode`,
  `SetTXAPHROTAutoReset`, `GetTXAPHROTAsymmetry`. WBFM not wired (no imports).
- `PSForm.cs`: `puresignal` class holds PS imports incl. the advanced 2.00 set
  (`SetPSStabilize`, `SetPSEMAAlpha`, `SetPSPinAlpha`, `SetPSPinMode`,
  `SetPSIntsAndSpi`, `SetPSDCBEnable`, `SetPSDCBCap`, `SetPSEQEnable`,
  `SetPSOutlierSigma`, `ResetPSAdvancedParams`); `GetPSDisp` 12-arg.
  `SetPSPtol`/`SetPSMapMode` REMOVED (UI controls deleted, superseded by
  `SetPSOutlierSigma`/`SetPSEQEnable`). Red highlight on `info[6]==2`.
- `AmpView.cs`: 12-arg GetPSDisp call; bucket config from PSForm Ints/Spi.
- `eqform.cs`/`frmCFCConfig.cs`/`setup.cs`: pass Q array ptr when parametric mode
  on, `(double*)0` otherwise; CFC timers poll the `ready` out-param.
- Phase Rotator extras built programmatically (`InitPhaseRotatorControls`), with
  `CFCPhaseRotatorAuto` TXProfile column via `VerifyTXProfileColumns`.

**Porting strategy:** take the fork's `wdsp` folder as the proven base, then
overwrite the Thetis-only data/mods we must keep (rnnr, sbnr, iqc, FDnoiseIQ,
zetaHat, wdsp.rc), add the 8 modules to OUR vcxproj, and apply the fork's C#
edits (located via their `Yurij_eu2av` marker comments) to OUR Console files.
Do NOT copy their whole Console files (our base differs due to VST work).

## 3. Build project changes (wdsp.vcxproj)

1. **Add new 2.00 modules** (in 2.00 Source, absent from Thetis):
   `extrapolate.c/.h`, `nurbs.c/.h`, `nurbs_fit.c/.h`, `nurbs_spline.c/.h`,
   `phrot.c/.h` (extracted from 1.29 `iir.c`), `reshb.c/.h`, `snoop.c`,
   `wbfm.c/.h`. Add matching `<ClCompile>` / `<ClInclude>` entries.
2. **Keep Thetis-only files** (no upstream counterpart): `rnnr.c/.h`, `sbnr.c/.h`,
   `wdsp.rc` (Thetis resource; 2.00 ships none), `FDnoiseIQ.c`, `zetaHat.c`
   (Thetis-trained NR data - MUST NOT be overwritten; upstream 1.29 and 2.00 are
   identical to each other but both differ from Thetis).
3. **`calculus`** data file exists in both trees; compare hashes - either is fine
   if identical (likely), otherwise keep Thetis's.
4. Do NOT remove `iqc.c` (see Section 5.4).
5. No new external dependencies: FFTW remains the bundled `fftw3.h`/dll.
6. Optional cleanup: the vcxproj has duplicated `ClCompile`/`ClInclude` entries and
   broken filter structure; while touching it, dedupe (no functional change).

## 4. Thetis-only modules to carry forward (no upstream counterpart)

### 4.1 Carrier blanker (`cblock.c`, `cblk.h`)
Thetis modified `xcbl` from 1-arg `xcbl(CBL a)` to **2-arg `xcbl(CBL a, int position)`**
(`cblock.c:76`) to allow blanking before or after AGC. 2.00 still has the 1-arg form.
Re-apply the 2-arg form and keep the `SetRXACBLPosition` export (C# calls it from
`radio.cs:1675`; declared `dsp.cs:227`).

### 4.2 RNNoise NR3 (`rnnr.c/.h`)
Thetis-only noise reduction module. Keep the Thetis files verbatim. Exports used by
C#: `RNNRloadModel` (`dsp.cs:253`, called from `radio.cs:182`, `setup.cs:34970`),
`SetRXARNNRRun`, `SetRXARNNRPosition`, `SetRXARNNRUseDefaultGain`.

### 4.3 SpecBleach NR4 (`sbnr.c/.h`)
Thetis-only noise reduction module. Keep the Thetis files verbatim. Exports used by
C#: `SetRXASBNRRun`, `SetRXASBNRPosition`, `SetRXASBNRnoiseScalingType`,
`SetRXASBNRnoiseRescale`, `SetRXASBNRpostFilterThreshold`,
`SetRXASBNRreductionAmount`, `SetRXASBNRsmoothingFactor`,
`SetRXASBNRwhiteningFactor`.

### 4.4 IQ correction (`iqc.c/.h`)
Thetis ships a modified IQC implementation (`SetTXAiqcValues`, `GetTXAiqcValues`,
`SetTXAiqcStart/End/Swap`). **No C# code calls these** - the module is compiled but
dormant. Keep the Thetis version. (Upstream 2.00 rewrote `iqc.c`; not worth
adopting since it is unwired on both sides.)

## 5. Re-apply Thetis mods onto 2.00 files

### 5.1 `RXA.c` (highest risk file)
Take 2.00 `RXA.c`, then re-apply the Thetis wiring (current Thetis mods, from the
1.29 diff, all confirmed still relevant):
- create/destroy/`xrxa`/`setDSPSamplerate_rxa`/`setDSPBuffsize_rxa` wiring for
  `rnnr` and `sbnr` (both pre- and post-AGC positions).
- `xrxa`: `xcbl(...)` called at position 0 (before AGC) and position 1 (after AGC).
- `RXAbp1Check` / `RXAbp1Set`: add `rnnr_run` / `sbnr_run` to the bandpass gain
  decision (gain 2.0 when any NR is active).
- 2.00 also changed RXA.c itself (+94/-19 vs 1.29) - review that delta during the
  merge, especially around the AGC/bp1 ordering.

### 5.2 `cblock.c`
Re-apply 2-arg `xcbl` (Section 4.1) onto 2.00 `cblock.c`.

### 5.3 `eq.c/.h` - DECISION (Section 9.1)
Thetis carries a **parametric-Q equalizer engine** (Richard Samphire, MW0LGE,
`eq.c:176-282`): Q-factor gaussian bells + 4th-order rolloff beyond active range.
This required adding `double* Q` to both `eqp` and `eq` structs and threading Q
through `eq_impulse`/`eq_mults`/`create_eqp`/`create_eq`, and keeping the 5-arg
`SetRXAEQProfile`/`SetTXAEQProfile` signatures.
2.00 **rewrote** EQ as NURBS control-point splines and dropped Q entirely
(`SetRXAEQProfile(channel, nfreqs, F, G)`).
If keeping Thetis EQ: re-apply the Q engine onto 2.00 `eq.c/.h` and keep the 5-arg
exports. If adopting 2.00 EQ: C# changes only (Section 6.3).

### 5.4 `cfcomp.c/.h`
Thetis applies the same parametric-Q technique to the compressor / frequency
compressor / peq stack, plus two exports not in upstream:
- `GetTXACFCOMPGainAndMask(channel, gainAndMask_values, ready)` (`cfcomp.c:1452`) -
  used by C#; **removed in 2.00** (hard break if not re-added).
- 7-arg `SetTXACFCOMPprofile(channel, nfreqs, F, G, E, Gq, Eq)` - 2.00 uses
  `(channel, nfreqs, F, G, E)`.
- 2.00 also added `SetTXACFCOMPGprofile`, `SetTXACFCOMPEprofile`,
  `SetTXACFCOMPCompCurve/Weights`, `SetTXACFCOMPPeqCurve/Weights`,
  `GetTXACFCOMPCompDraw`, `GetTXACFCOMPPeqDraw` (free-curve API).
Decision path mirrors Section 9.1.

### 5.5 `analyzer.c`
Re-apply `SetPixelRef(disp, pixel_ref)` export (C#: `display.cs:957,978` via
`specHPSDR.cs:846`) and the Thetis `analyze_bandpass_filter` helper. Also review
the Thetis `stitch()` / `ResetPixelBuffers()` / `GetPixels()` changes vs 2.00
(2.00 moved analyzer to 64-bit pixel/`n_pixout` math - the Thetis C# already
passes `double fscLin/fscHin` and `int n_pixout`, so the P/Invoke shape is
compatible; verify no int-truncation regressions).

### 5.6 `snb.c`
Thetis `+4/-1` delta (SNBA output-bandwidth handling). Diff Thetis vs 2.00 and
re-apply; 2.00 also reworked the NB.

### 5.7 `calcc.c` - DECISION (Section 8)
PureSignal. See Section 8 for options.

### 5.8 Files verified as Thetis-identical to 1.29 (take 2.00 as-is)
`TXA.c`, `wcpagc.c`, `amn`, `fmd`, `emph`, `nobII`, `amsq`, `anf`, `anr`, `emnr`,
`ssql`, `fmsq`, `fmmod`, `ammod`, `amd`, `gaussian`, `dexp`, `tnf`, `rnf`,
`bandpass`, `cblk` data structures (in headers), `vox`, `wsd`, `vfo`, `osctrl`,
`nbp`, `channel.c` (thetis `channel.c` is 1.29-identical; 2.00 changed it).
For any of these, simply take the 2.00 file.

## 6. C# / P/Invoke changes (thetisvst\Project Files\Source\Console)

### 6.1 Version gate (required to boot)
- `Versions.cs:23`: `_WDSP_VERSION = 1290` -> `2000`.
- `console.cs:44702-44748`: comment says "version number in version.c where it is
  121 * 10"; verify the combined gate at `:44748` still passes (2.00
  `version.c` returns 200 -> `200*10 = 2000`).

### 6.2 Hard breaks (EntryPointNotFoundException - crashes if not fixed)
| Symbol | File(s) | Status in 2.00 |
|---|---|---|
| `GetTXACFCOMPGainAndMask` | `dsp.cs:794-796` | **NOT a real break - corrected 2026-08-13.** Not in any C source, not in 1.29 baseline exports, and the only C# references are the dangling DllImport declaration (no call sites). Just delete the declaration. |
| `SetPSPtol` | `PSForm.cs:808,810,1037` | removed - drop entirely (decision 2026-08-13) |
| `SetPSPinMode` | `PSForm.cs:836,838,1046` | removed - restore as 2.00 wrapper (fork reference) |
| `SetPSMapMode` | `PSForm.cs:844,846,1049` | removed - drop entirely (decision 2026-08-13) |
| `SetPSStabilize` | `PSForm.cs:852,854,1052` | removed - restore as 2.00 wrapper (fork reference) |
| `SetPSIntsAndSpi` | `PSForm.cs:862-880,1055` | removed - restore as 2.00 wrapper (fork reference) |

`psccF` (`PSForm.cs:1019`) is declared but never called - safe to leave as a
dangling extern or delete.

`SetFMDCalVals` - **non-issue, corrected 2026-08-13.** Does not exist in any C#
file or the DLL; the plan's earlier reference was wrong. No action.

### 6.3 Signature changes (compile/runtime-mismatch fixes)
| Symbol | Current C# | 2.00 | Action |
|---|---|---|---|
| `SetTXACFCOMPprofile` | `(ch, nfreqs, F, G, E, Gq, Eq)` (`dsp.cs:758`, `frmCFCConfig.cs:384,388`, `setup.cs:18566`) | `(ch, nfreqs, F, G, E)` | depends on Section 9.1 |
| `SetTXAEQProfile` | `(ch, nfreqs, F, G, Q)` (`dsp.cs:787`, `eqform.cs:2810,3070`) | `(ch, nfreqs, F, G)` | depends on Section 9.1 |
| `SetRXAEQProfile` | `(ch, nfreqs, F, G, Q)` (`dsp.cs:791`, `eqform.cs:3027-3028`) | `(ch, nfreqs, F, G)` | depends on Section 9.1 |
| `GetTXACFCOMPDisplayCompression` | `(ch, comp_values, ready)` (`dsp.cs:800`, `frmCFCConfig.cs:404`, `setup.cs:22293`) | fork-proven form is the SAME 3-arg `(ch, comp_values, int* ready)` - no change needed (docs' 5-arg firstFlag form is wrong; cfcomp port keeps 3-arg) | no change |
| `SetFMDCalVals` | none (declared nowhere, corrected 2026-08-13) | renamed `SetWBFMCalVals` | no action needed |

### 6.4 Wisdom / impulse cache
- 2.00 writes `wdspWisdom01`; Thetis C# checks/removes `wdspWisdom00`
  (`radio.cs:105-156`). Update the filenames so the rebuild prompt and the
  "missing wisdom" path trigger correctly on first run post-upgrade.
- Existing impulse-cache invalidation logic (`radio.cs:139`) already handles the
  wisdom-rebuild case; verify it still covers the new filename.

### 6.5 Optional new-feature wiring (only if the product wants them)
- WBFM stereo demod (new form/controls; `SetWBFMCalVals`, `GetRXAWBFMStereoIndicator`).
- Phase rotator extras: `SetTXAPHROTAutoMode`, `SetTXAPHROTAutoReset`,
  `GetTXAPHROTAsymmetry` (existing `SetTXAPHROTRun/Corner/Nstages/Reverse`
  signatures are unchanged in 2.00).
- RX input decimator: mode 1029 / 1029a (4-fold half-band `reshb`).
- EQ / CFC free-curve UI: `SetRXAEQCurve/Weights`, `GetRXAEQDraw`,
  `SetTXACFCOMPGprofile/Eprofile`, `GetTXACFCOMPCompDraw/PeqDraw`, etc.

## 7. Runtime / data artifacts

- FFTW wisdom rebuilt on first run (new `wdspWisdom01`); C# already prompts.
- PS calibration file: format/content unchanged in this 2.00 (still
  console-side via `PSSaveCorr`/`PSRestoreCorr`); re-verify once PSV3 is active
  (Section 8) because sample-domain fit data is new.
- `FDnoiseIQ.c` / `zetaHat.c`: keep Thetis data files; do not overwrite.

## 8. PureSignal decision (largest decision)

Removed in 2.00 (PureSignal V3 rewrite of `calcc.c`): `SetPSIntsAndSpi`,
`SetPSMapMode`, `SetPSPinMode`, `SetPSPtol`, `SetPSStabilize`. Everything else
the Thetis PSForm touches survives: `SetPSControl`, `SetPSRunCal`, `SetPSMox`,
`SetPSMoxDelay`, `SetPSLoopDelay`, `SetPSTXDelay`, `SetPSHWPeak`, `GetPSHWPeak`,
`GetPSMaxTX`, `PSSaveCorr`, `PSRestoreCorr` (all verified present in 2.00).

**RESOLVED 2026-08-13 by the reference implementation.** The fork ported the
PSForm onto 2.00 with a hybrid of the original options A and C - no no-op stub
stage needed (option C is moot):
- Re-added `SetPSStabilize`, `SetPSPinMode`, `SetPSIntsAndSpi` as working
  wrappers mapped to 2.00 NURBS tunables (see Section 2.2).
- Removed `SetPSPtol`/`SetPSMapMode` entirely and deleted their UI controls,
  superseded by direct 2.00 exports `SetPSOutlierSigma` (outlier culling) and
  `SetPSEQEnable` (density equalization) plus `SetPSEMAAlpha`/`SetPSPinAlpha`/
  `SetPSDCBEnable`/`SetPSDCBCap`/`ResetPSAdvancedParams`.
- `GetPSInfo` index semantics still used by PSForm (info[0..15]); `info[6]==2`
  now means severe over-drive and is red-highlighted.
- `GetPSDisp` is 12-arg (channel + 11 pointers, incl. nsamps_out/cpts_out/
  phs_ref_deg_out); AmpView updated.

One sub-decision remains (ask user): follow the proven final state (drop
ptol/map, recommended) or keep all 5 as wrappers per the fork's earlier 07-07
changelog state (ptol -> `outlier_sigma = 1.5 + ptol*1.875`; map -> `eq_enable`),
preserving the current PSForm UI exactly.

**SUB-DECISION RESOLVED 2026-08-13: DROP `SetPSPtol`/`SetPSMapMode`** (delete
`chkPSRelaxPtol`/`chkPSMap` controls; expose `SetPSOutlierSigma`/`SetPSEQEnable`).
Expected export union excludes these two symbols.

## 9. Feature decisions (must be made before implementation)

### 9.1 Equalizer / Compressor engine
Keep the Thetis parametric-Q engine (re-port to 2.00 tree, keep 7-arg CFC and 5-arg
EQ profiles) **or** adopt 2.00's NURBS/spline engine (update C# to the new 4-arg
profiles and free-curve UI). This choice determines work in Sections 5.3, 5.4, 6.3.

**RESOLVED 2026-08-13 by the reference implementation: keep BOTH.** The fork
ported the Q engine into 2.00 as a dual path - `eq.c/.h` and `cfcomp.c/.h` take
optional Q arrays (`Q`, `Qg`/`Qe`); the Gaussian-blend Q path is active when
`Q != NULL`, the 2.00 NURBS/linear path when `Q == NULL`. C# keeps the 5-arg EQ
and 7-arg CFC signatures with a trailing `double* Q`, passing a Q array in
parametric mode and `(double*)0` otherwise. This preserves the shipped Q UI
(eqform.cs `chkUseQFactors`, frmCFCConfig.cs `chkCFC_UseQFactors`) while keeping
2.00's engine available. Recommendation: **adopt this dual-path port**.

### 9.2 WBFM
Either ship it (new RX mode + form) or leave the 2.00 module compiled-but-unwired.
No C# currently calls FM-cal functions.

**RESOLVED 2026-08-13:** the reference fork ships it compiled-but-unwired - zero
WBFM imports, UI, or calibration calls in its C#. Recommendation: **match that**
(compile-but-unwired), wire later if wanted.

### 9.3 New NB / ANF / ANR / wcpAGC / NOB improvements
These are upstream behavioral changes inside 2.00; they arrive automatically with
the source swap. Regression-test only.

## 10. Implementation order (phases)

**Phase 0 - Baseline.**
Create a working branch. Build the current `wdsp.vcxproj` from a clean checkout
and dumpbin `/exports` to record the current 539-export baseline. Confirm the
toolchain builds before touching anything.

**Phase 1 - Pure C swap (no C# changes).**
1. Copy the fork's 2.00-based `wdsp` folder into `Project Files\Source\wdsp`
   (Section 2.2 - this is stock 2.00 + all Thetis mods already applied and
   build-proven).
2. Restore our Thetis-only files (Section 4) from OUR current tree: `rnnr`,
   `sbnr`, `iqc.c/.h`, `FDnoiseIQ.c/.h`, `zetaHat.c/.h`, `wdsp.rc` (ours==fork
   for the last one; calculus/fastmath.h identical either way).
3. Update OUR `wdsp.vcxproj` (Section 3): add the 8 new 2.00 modules
   (`extrapolate`, `nurbs`, `nurbs_fit`, `nurbs_spline`, `phrot`, `reshb`,
   `snoop`, `wbfm`) to ClCompile/ClInclude/filters. Our lib config (fftw,
   rnnoise, specbleach) already matches.
4. Build. Script-compare the DLL's export list against
   `(2.00 exports) + (Thetis-only exports)` (minus `SetPSPtol`, `SetPSMapMode`
   if the Section 8 sub-decision drops them) and fix gaps until green.
5. Run a smoke test with the existing C# but only where signatures are unchanged.

**Phase 2 - C# compile & boot fix.**
Version gate (6.1), hard breaks (6.2), signature updates (6.3), wisdom filenames
(6.4). Use the fork's C# as the reference for every edit (Section 2.2; edits are
marked with `Yurij_eu2av` comment markers in the fork). Goal: Thetis boots
against the 2.00 DLL without crashes.

**Phase 3 - Functional regression pass.**
RX/TX audio paths, ANF/ANR/EMNR, NR3/NR4, CBL, NB, AGC, EQ, CFC, spectrum/
analyzer/panadapter, meters, TXAFMD. Compare behavior to the pre-upgrade build.

**Phase 4 - PureSignal PSV3.**
Per Section 8 option A (or C then A).

**Phase 5 - Optional features.**
WBFM, phrot extras, RX input decimator, free-curve EQ/CFC UI.

**Phase 6 - Release validation.**
Wisdom rebuild UX on first run, performance (64-bit analyzer path), memory, 100%
export-list check, installer includes new DLL only.

## 11. Verification tooling

- Export-list diff script (PowerShell): parse `dumpbin /exports wdsp.dll`, compare
  to the union of 2.00 `PORT`/`__declspec(dllexport)` symbols + Thetis-only exports
  (Section 4/5; drop `SetPSPtol`/`SetPSMapMode` from the expected union if the
  Section 8 sub-decision removes them). This catches accidental symbol loss at build
  time - the single most important automated check for this conversion.
- After C# edits: build the Console solution and run to confirm the version gate
  and startup paths.
- Spot-check diffs of `RXA.c`, `TXA.c`, `calcc.c`, `eq.c`, `cfcomp.c` between
  thetis-pre and thetis-post to confirm intended Thetis mods are present.

## 12. Risks

1. **PureSignal**: PSV3 changes calibration/info semantics; PSForm rework is the
   biggest UI task. Mitigate via option C stepping stone.
2. **Release notes vs source drift**: docs mention `WDSP_SetCalAndMasks` that does
   not exist; confirm every API against the 2.00 source during implementation.
3. **EQ/CFC**: adopting 2.00's spline engine changes on-screen curves and could
   regress saved user profiles (profile files store F/G/Q arrays).
4. **Wisdom**: rebuilt on first run (5+ min prompt - already handled by C#).
5. **Data files**: `FDnoiseIQ.c`/`zetaHat.c` must not be clobbered by the swap.
6. **vcxproj hygiene**: existing duplicated entries can cause double-compile
   warnings; clean while editing.
7. **64-bit analyzer** rework may expose display regressions; budget regression
   testing on the spectrum/panadapter.
