# SESSION HANDOFF - Thetis WDSP 2.00 Conversion

Created: 2026-08-12  Updated: 2026-08-13
Status: PHASE 1 (pure C swap) COMPLETE. PHASE 2 (C# compile & boot fix)
        COMPLETE and BUILD-VERIFIED (full solution 0 errors; boot fixes in).
        Next: PHASE 3 - FUNCTIONAL REGRESSION pass (hands-on radio testing).

## How to resume

1. Read WDSP2_CONVERSION_PLAN.md (repo root) - the authoritative plan.
2. Branch `wdsp2-conversion` has ALL Phase 1 + Phase 2 work in the working tree
   (NOT committed - no commit was made).
3. Next step: PHASE 3 (plan Section 10): RX/TX audio, ANF/ANR/EMNR, NR3/NR4,
   CBL, NB, AGC, EQ, CFC, spectrum/analyzer/panadapter, meters, TXAFMD,
   PureSignal PSV3 (calibration + advanced controls + info[6] highlight),
   phase rotator (Auto FC/Reset/asymmetry), and first-run wisdom rebuild UX.
   Compare behaviour against the pre-upgrade (1.29) build using saved profiles.
   Recommended regression checklist is kept with the Phase 3 notes below.

## Phase 1 completion record (2026-08-13)

All Phase 1 steps done on branch `wdsp2-conversion`:
- Copied fork wdsp folder -> our `Project Files\Source\wdsp`, EXCLUDING
  wdsp.vcxproj / wdsp.vcxproj.filters (both still ours, untouched).
- Restored OUR Thetis-only files from git HEAD: rnnr.c/h, sbnr.c/h, iqc.c/h,
  FDnoiseIQ.c/h, zetaHat.c/h, wdsp.rc.
- Updated OUR wdsp.vcxproj + .filters: added the 8 new 2.00 modules
  (extrapolate, nurbs, nurbs_fit, nurbs_spline, phrot, reshb, snoop [.c only,
  no .h], wbfm) to ClCompile/ClInclude/Filter. snoop has NO header (matches fork).
- Built: `MSBuild wdsp.vcxproj -p:Configuration=Release -p:Platform=x64`
  -> 0 errors, 0 warnings (only benign MSB8012 TargetPath/OutputFile note).
  Output: `Project Files\Source\bin\x64\Release\wdsp.dll`.
- Export check: dumpbin /exports -> **558 exports, byte-identical to the fork's
  proven `thetis-enhanced\Project Files\bin\x64\Release\wdsp.dll`**. No missing
  and no extra symbols.
- version.c returns 200; wisdom.c writes `wdspWisdom01` (verified in source).
- CALCULATED DATA NOTE: FDnoiseIQ/zetaHat in the fork == stock 1.29 == stock
  2.00 == OUR git HEAD (hash-verified) - the earlier "gotcha" that the fork
  dropped Thetis NR data is now MOOT; nothing was lost. We restored ours anyway.
- SetPSPtol / SetPSMapMode: still exported by BOTH the fork DLL and ours (they
  remain in calcc.c as simple setters). Per the resolved sub-decision, the C#
  side simply never calls them (delete the DllImports / UI controls in Phase 2).
  They are harmless extra exports - no need to remove from the C code.

### Phase 2 completion record (2026-08-13 - resumed after 4 interrupted sessions)

All 8 Phase 2 checklist items done and CROSS-VERIFIED against the reference
fork (every modified Console file compared: line-identical or semantically
equivalent):

1. Versions.cs:23 `_WDSP_VERSION = 2000`; console.cs gate (`GetWDSPVersion()*10`
   == 2000, version.c returns 200) verified unchanged.
2. radio.cs CreateDSP(): checks `wdspWisdom01`; renames legacy `wdspWisdom00`
   -> 01 if 01 missing, else deletes it; impulse-cache invalidation intact.
3. dsp.cs: SetTXAEQProfile/SetRXAEQProfile 5-arg with `double* Q`;
   SetTXACFCOMPprofile 7-arg (G, E, Qg, Eq); GetTXACFCOMPDisplayCompression
   3-arg (ch, comp_values, int* ready); GetTXACFCOMPGainAndMask import removed;
   added SetTXAPHROTAutoMode/SetTXAPHROTAutoReset/GetTXAPHROTAsymmetry.
   WBFM unwired (matches fork).
4. eqform.cs / frmCFCConfig.cs / setup.cs CFC calls: NO changes needed - our
   base already passed `Q`/`(double*)0` conditionally since 1.29; verified
   equivalent to fork (null == (double*)0).
5. PSForm.cs + designer: puresignal class with the 2.00 advanced set
   (SetPSStabilize, SetPSEMAAlpha, SetPSPinAlpha, SetPSPinMode, SetPSIntsAndSpi,
   SetPSDCBEnable, SetPSDCBCap, SetPSEQEnable, SetPSOutlierSigma,
   ResetPSAdvancedParams); SetPSPtol/SetPSMapMode fully removed (imports,
   calls, chkPSRelaxPtol/chkPSMap controls - absent in both our designer and
   the fork); GetPSDisp 12-arg; red highlight on lblPSInfo6 when info[6]==2.
   PSForm layout byte-matches fork (ClientSize 560x303, controls at x=434+).
6. AmpView.cs: 12-arg GetPSDisp call with xm_cor/ym_cor/xa_cor/ya_cor/
   nsamps_out/cpts_out/phs_ref_deg_out buffers; bucket guards
   (ints<1 -> 16, spi<1 -> 256). Byte-identical GetPSDisp block vs fork.
7. setup.cs: InitPhaseRotatorControls() (Auto FC checkbox, Reset button,
   status/asymmetry IN-OUT/FC labels) + chkPHROTAuto_CheckedChanged,
   btnPHROTReset_Click, timerPhRot_Tick - all byte-identical to fork (only a
   comment line differs). CFCPhaseRotatorAuto wired into all 4 TXProfile paths
   (diff, changed-compare, save, load).
8. setup.designer.cs: timerPhRot (create + config Enabled/Interval=250/Tick +
   field); grpPhRot resized 124x121 -> 210x130 (matches fork - the old size
   CLIPPED the new controls); hidden PS controls (udPSTargetFeedbackLevel,
   udPSOutlierSigma, chkPSOutlierEnable) at (-100,-100) Visible=false.
9. database.cs: VerifyTXProfileColumns() adds CFCPhaseRotatorAuto to TXProfile
   + TXProfileDef and (OUR ADDITION, fork lacks this) BACKFILLS DBNull -> false
   so pre-existing profile rows do not throw on a direct cast; default rows all
   set CFCPhaseRotatorAuto=false (21 rows, verified count).
10. clsHardwareSpecific.cs: PSTargetFeedbackLevel (22 for
    ANAN7000D/ANAN8000D/ANVELINAPRO3, else 152), PSOutlierEnableDefault,
    PSOutlierSigmaDefault (5.0 / 2.5) - values match fork.

### Session fixes (2026-08-13) - bugs found and fixed tonight

A. STARTUP CRASH (reported by user): System.InvalidCastException in
   Thetis.Setup.loadTXProfile. Cause: migrated CFCPhaseRotatorAuto column was
   DBNull on pre-existing profile rows; `(bool)dr[...]` threw. Fixed in BOTH
   places: database.cs backfills DBNull -> false in VerifyTXProfileColumns
   (runs at DB load, before any profile load), and setup.cs:9682 uses the
   DBNull-safe DB.ConvertFromDBVal<bool>. Verified: startup-crash path fixed.

B. VISUAL: grpPhRot (DSP > CFC tab) was 124x121 so the new Phase Rotator
   controls were clipped at the edges. Resized to 210x130 (fork size).
   Confirmed no overlap with pnlCFC/pnlCFC_legacy on the tab.

C. wdsp.vcxproj POST-BUILD COPY NEVER RAN (caught during this session's
   verification): this toolchain (VS2026 = VC v180 Microsoft.CppCommon.targets)
   executes post-build events from the `PostBuildEvent` ITEM
   (`%(PostBuildEvent.Command)`), NOT the `$(PostBuildEvent)` property (the
   property form only works in C# Microsoft.Common.targets). Original
   property-based entry was silently ignored. Fixed to:
   ```
   <ItemDefinitionGroup>
     <PostBuildEvent>
       <Command>pushd "$(ProjectDir)"
   if not exist "$(ProjectDir)..\..\bin\$(HPSDR_PLATFORM)\$(Configuration)" mkdir "$(ProjectDir)..\..\bin\$(HPSDR_PLATFORM)\$(Configuration)"
   xcopy /Y /D "$(SolutionDir)..\bin\$(HPSDR_PLATFORM)\$(Configuration)\wdsp.dll" "$(ProjectDir)..\..\bin\$(HPSDR_PLATFORM)\$(Configuration)\wdsp.dll"
   popd</Command>
     </PostBuildEvent>
   </ItemDefinitionGroup>
   ```
   - `pushd "$(ProjectDir)"` normalises CWD so the empty-SolutionDir relative
     path resolves for STANDALONE builds (linker output -> Source\bin\x64\Release).
   - In FULL-SOLUTION builds source == dest (both Project Files\bin\x64\Release);
     `xcopy /Y /D` onto itself returns "0 File(s) copied" / exit 0 (verified) so
     it is a safe no-op. Do NOT add /I (dest is a filename, not a directory).
   - VERIFIED BOTH WAYS: standalone Rebuild -> DLL copied to bin\x64\Release;
     full solution build (Thetis_VS2026.sln) -> 0 errors, no self-copy error.
    - Thetis.csproj post-build (fftw/rnnoise etc.) is unaffected (C# project,
      property form, lines 1224-1234).

D. FULL-SOLUTION BUILD + bin\x64\Release CONSOLIDATION (2026-08-13): built the
   entire `Thetis_VS2026.sln` (Release|x64, exit 0, no errors). Output mapping:
   - Direct to `Project Files\bin\x64\Release\`: Thetis.exe, VstHostBridge.dll,
     VstAudioHost.exe, VstPluginScanner.exe.
   - wdsp.dll -> bin\x64\Release via wdsp post-build (item C); ChannelMaster.dll
     and cmASIO.dll already copy themselves there (their own post-builds).
   - Midi2Cat.dll / RawInput.dll built to `Source\Midi2Cat|RawInput\bin\x64\Release\`
     (NOT bin root). Added matching C# PostBuildEvent to BOTH csproj files
     (property form, before </Project>):
     `if not exist "$(ProjectDir)..\..\bin\x64\$(Configuration)" mkdir ...`
     + `copy /Y "$(TargetPath)"` + `copy /Y "$(TargetDir)$(TargetName).pdb"` into
     `$(ProjectDir)..\..\bin\x64\$(Configuration)\`. VERIFIED: fresh DLL+PDB in
     bin\x64\Release after rebuild.
   - NOT needed: portaudio.dll (console P/Invokes PA19.dll, already in bin;
     "portaudio.dll" text in console.cs is legacy message wording). Thetis.Tests
     builds to its own net48 output. Installer (wixproj) not in build config.
   - Final state: all runtime files present & current in bin\x64\Release.

### SDR-VST3 standalone/isolation pass (2026-08-13)

Audited SDR-VST3 vs a real Thetis base install (shared state = interference).
Result: no shared writable state remains. All uncommitted on wdsp2-conversion.

FIXED (state-sharing conflicts -> SDR-VST3-specific):
- ASIO driver config registry: `HKCU\SOFTWARE\OpenHPSDR\Thetis-x64` ->
  `SDR-VST3-x64` in BOTH the console writer (clsCMASIOConfig.cs:74) and the
  cmASIO.dll reader (cmASIO\hostsample.cpp, 5 call sites: getASIODriverString,
  getASIOBlockNum, getASIOBaseInputChannel, getASIOBaseOutputChannel,
  getASIOInputMode).
- Startup log: clsProgressLog.cs:82 `HKCU\Software\OpenHPSDR\Thetis-x64` ->
  `SDR-VST3-x64`.
- Firewall runtime rules (Firewall.cs:79-82): "Thetis Allow IN/OUT TCP/UDP" ->
  "SDR-VST3 Allow ...". Old names collided: findRule-by-name in
  addApplicationRule would delete the OTHER product's rule. Installer rules
  were already SDR-VST3-named.
- Splash timing: splash.cs:580 APPLICATION_NAME "Thetis" -> "SDR-VST3"
  (`HKCU\Software\OpenHPSDR\SDR-VST3`).
- Default recording folder: `%USERPROFILE%\Music\Thetis` -> `Music\SDR-VST3`
  (clsAudioRecordPlayback.cs:255+610, setup.cs:36738, Memory\MemoryForm.cs:1385;
  ensureFolderExists auto-creates it).

VERIFIED ALREADY ISOLATED (no change needed):
- App data: `%APPDATA%\OpenHPSDR\SDR-VST3-x64\` (console.cs:692-696, 1499-1503).
- Wisdom: `WDSPwisdom(app_data_path)` writes
  `%APPDATA%\OpenHPSDR\SDR-VST3-x64\wdspWisdom01`; radio.cs:108
  Path.GetDirectoryName of app_data_path resolves back to the SDR-VST3-x64
  folder itself (trailing-slash semantics), so check+write agree and stay
  inside the isolated folder.
- Installer: UpgradeCodes are NEW unique GUIDs (x64 159a9e90-... / x86
  3efd9e6a-...; rebrand commit changed them from stock Thetis DEC025E2/
  CE4756C9), install folder Program Files\OpenHPSDR\SDR-VST3, HKLM keys
  Software\OpenHPSDR\SDR-VST3-x64, firewall rule names SDR-VST3-x64.
- Single-instance mutex `Global\SDR-VST3_e5a2cba2-...` (clsSingleInstance.cs).
- wdsp.dll / ChannelMaster.dll / cmASIO.dll / PA19.dll all load from own dir.
- VST3 plugin suite installed to Common Files\VST3 (shared standard VST3 dir)
  but components Permanent (survive uninstall; other hosts unaffected).

LEFTOVER (cosmetic/benign, deliberately kept): VstHostBridge named objects
vst_host_bridge.cpp:725-727,1615-1617 (`Local\ThetisVstHost*` + pipe; unique per
PID, stock Thetis has no VST host so nothing to collide with); TCI protocol name
"Thetis"; User-Agent strings; N1MM IDs; -datapath help text; AssemblyInfo title.

BUILD: full solution exit 0 after changes; Thetis.exe + cmASIO.dll rebuilt.

### Phase 3 regression checklist (next session)

Run the same config on the pre-upgrade build first (or rely on saved profiles),
then the 2.00 build; note any difference. Builds are in
`Project Files\bin\x64\Release\` (Thetis.exe 11415552 bytes, wdsp.dll 5596160
bytes, both 2026-08-13):
- [ ] Boot + first-run FFTW wisdom rebuild prompt (new wdspWisdom01; 5+ min)
- [ ] RX audio path (bands, modes, volume, filter width) - no drops/artifacts
- [ ] TX audio path + TXAFMD
- [ ] ANF / ANR / EMNR / NR3 (RNNR) / NR4 (SBNR) behaviour
- [ ] Carrier blanker (CBL) on/off + position (pre/post AGC)
- [ ] NB / AGC (wcpAGC) - 2.00 reworked upstream behaviour
- [ ] EQ form: parametric-Q curves match, profile save/load round-trip
- [ ] CFC form: curves, ready-flag polling, 7-arg profile round-trip
- [ ] Spectrum / analyzer / panadapter (64-bit analyzer rework risk area)
- [ ] Meters
- [ ] PureSignal PSV3: calibration, advanced controls, info[6]==2 red highlight
- [ ] Phase Rotator: Auto FC, Reset (338 Hz), IN/OUT asymmetry + FC labels
- [ ] Export-list: dumpbin /exports wdsp.dll vs fork (already byte-identical
      in Phase 1; re-check only if C source changes)

## Reference implementation (NEW - this is the game changer)

We found a PROVEN, working WDSP 2.00 conversion of Thetis:
- Repo: https://github.com/eu2av/OpenHPSDR-Thetis-Enhanced (Yurij-eu2av)
- Cloned to: C:\Users\W4YNY\AppData\Local\Temp\opencode\thetis-enhanced
- Base: Thetis 2.10.x (VS2026) - SAME lineage as our repo (identical
  Versions.cs constants: _CMASTER_VERSION=1040, _WDSP_VERSION=1290,
  _PORTAUDIO_VERSION=1970).
- Their WDSP integration is documented in CHANGELOG_Yurij-eu2av_EN.md (2026-07-07
  "WDSP 2.00 integration completed" and 2026-07-07 "PureSignal advanced controls
  restored" + 2026-07-08 phrot). All other changelog entries (waterfall/DPI/
  det-cal/voltage/APF/db-upgrade/skins) are NOT wdsp-related - ignore.
- Build status they report: wdsp.vcxproj x64 Release 0 errors, full solution 0
  errors, dumpbin /exports all present, Thetis.exe boots and runs.

## What their fork does (verified against final code, not just changelog)

C side (Project Files\Source\wdsp):
- Their wdsp folder == stock 2.00 except 24 modified files + 7 fork-only
  (rnnr/sbnr/wdsp.rc/vcxproj/filters). Our thetis wdsp folder is a strict
  SUBSET of theirs - the only files they add are the 8 new 2.00 modules
  (extrapolate, nurbs, nurbs_fit, nurbs_spline, phrot, reshb, snoop, wbfm).
- The 24 modified files are exactly the Thetis mods re-applied onto 2.00:
  amd.c, anf.c, anr.c, emnr.c, ssql.c (RXAbp1Check 8-arg call sites),
  fmsq.c (eq_impulse Q signature), TXA.c/.h, calcc.c/.h (PS3 wrappers),
  eq.c/.h + cfcomp.c/.h (dual-path Q engine), cblock.c/.h (2-arg xcbl +
  SetRXACBLPosition), analyzer.c/.h (SetPixelRef etc), RXA.c/.h (rnnr/sbnr/
  cbl wiring), snb.c, comm.h, iqc.c/.h (their own iqc).
- rnnr.c/h and sbnr.c/h in the fork are byte-identical to OUR Thetis versions
  (only a 2-line comment marker differs) - so OUR rnnr/sbnr work with their
  RXA.c unchanged. Our current thetis RXA.c already has the 8-arg RXAbp1Check
  (lines 903-935), confirming the mod set matches.
- THEIR version.c returns 200; THEIR wisdom.c writes "wdspWisdom01".
- IMPORTANT GOTCHA: the fork ships STOCK 2.00 versions of FDnoiseIQ.c/h and
  zetaHat.c/h. OURS differs (Thetis-trained NR data). MUST restore OUR versions
  when porting - the fork silently dropped the Thetis NR data.
- iqc.c: ours vs fork vs stock all differ; keep OUR Thetis iqc (dormant, no
  C# calls).

C# side (Project Files\Source\Console) - the ONLY wdsp-related changes:
- Versions.cs:23 -> _WDSP_VERSION = 2000.
- console.cs checkVersions(): unchanged structure; gate passes because
  GetWDSPVersion()*10 == 2000.
- radio.cs CreateDSP(): checks wdspWisdom01; renames legacy wdspWisdom00 -> 01
  if 01 missing, else deletes it.
- dsp.cs: EQ/CFC P/Invokes get optional Q params - SetTXAEQProfile/SetRXAEQProfile
  (ch, nfreqs, F, G, Q); SetTXACFCOMPprofile (ch, nfreqs, F, G, E, Qg, Qe).
  GetTXACFCOMPDisplayCompression is 3-arg (ch, comp_values, int* ready) - NOT
  the 2.00 5-arg firstFlag form. GetTXACFCOMPGainAndMask import REMOVED.
  Added SetTXAPHROTAutoMode / SetTXAPHROTAutoReset / GetTXAPHROTAsymmetry.
  WBFM: NOT wired at all (no imports, no UI) - compiled-but-unwired.
- PSForm.cs: puresignal class holds PS P/Invokes. Advanced 2.00 set:
  SetPSStabilize, SetPSEMAAlpha, SetPSPinAlpha, SetPSPinMode, SetPSIntsAndSpi,
  SetPSDCBEnable, SetPSDCBCap, SetPSEQEnable, SetPSOutlierSigma,
  ResetPSAdvancedParams. GetPSDisp is 12-arg (channel + 11 ptrs).
  SetPSPtol and SetPSMapMode REMOVED entirely (UI controls chkPSRelaxPtol and
  chkPSMap deleted) - superseded by SetPSOutlierSigma / SetPSEQEnable.
  Red highlight on lblPSInfo6 when info[6]==2 (severe over-drive).
- AmpView.cs: GetPSDisp 12-arg call site; bucket config from PSForm Ints/Spi.
- eqform.cs / frmCFCConfig.cs / setup.cs: pass Q array pointer when parametric
  mode enabled, (double*)0 otherwise. frmCFCConfig + setup timers poll
  GetTXACFCOMPDisplayCompression with int ready.
- Phase Rotator extras (Auto FC / Reset / IN-OUT asymmetry / status) created
  programmatically in setup.cs InitPhaseRotatorControls(); CFCPhaseRotatorAuto
  column added to TXProfile via VerifyTXProfileColumns + default rows.

## Revised strategy (vs original plan)

ORIGINAL: wholesale 2.00 swap + hand re-apply thetis mods (Sections 4-5),
C# changes per Sections 6-7. Build-prove each step ourselves.

REVISED: Use the fork's wdsp folder as the build-proven base.
- Phase 1 (C): copy fork wdsp folder -> our wdsp; restore OUR thetis-only
  files: rnnr.c/h, sbnr.c/h, iqc.c/h, FDnoiseIQ.c/h, zetaHat.c/h, wdsp.rc
  (calculus/fastmath.h identical ours==fork - keep either). Add the 8 new
  modules to OUR wdsp.vcxproj (ClCompile + ClInclude). Keep our lib config
  (fftw, rnnoise, specbleach already present). Build + export-list diff
  vs (2.00 exports + thetis-only exports MINUS {SetPSPtol, SetPSMapMode}).
- Phase 2 (C#): apply the fork's wdsp C# changes (list above) to OUR Console
  files - do NOT copy their whole files (our base differs: VST additions).
  Their Yurij_eu2av marker comments locate every edit.
- Phases 3-6 unchanged (regression, PSV3 UI validation, optional features,
  release validation).

## 1 OPEN SUB-DECISION (ask before implementation)

PureSignal: the fork's FINAL state drops SetPSPtol/SetPSMapMode (deleted UI
controls, superseded by SetPSOutlierSigma + SetPSEQEnable). The 2026-07-07
changelog describes an EARLIER state where all 5 were restored as wrappers
(ptol->outlier_sigma=1.5+ptol*1.875; map->eq_enable). Options:
  (A) Follow proven final state: 3 wrappers (SetPSStabilize/PinMode/IntsAndSpi)
      + direct 2.00 controls; delete ptol/map UI. [RECOMMENDED - matches the
      reference exactly, less to maintain]
  (B) Keep all 5 wrappers incl. ptol/map mapped to outlier_sigma/eq_enable,
      preserving current PSForm UI exactly.

**RESOLVED 2026-08-13: OPTION A - drop SetPSPtol/SetPSMapMode.**

All other prior decisions are now RESOLVED by the reference:
- EQ/CFC engine: dual-path Q (Thetis Q engine kept, active when Q!=NULL;
  2.00 NURBS/linear when NULL). Proven, shipped.
- WBFM: compiled-but-unwired (their C# has zero WBFM references). Matches
  our recommended option.
- PSV3: no need for "option C stepping stone" - fork proves the wrapper +
  direct-control approach boots cleanly on the first try.

## Working files this session

- C:\Users\W4YNY\AppData\Local\Temp\opencode\thetis-enhanced (cloned fork)
- C:\Users\W4YNY\AppData\Local\Temp\opencode\wdsp (1.29/1.30/2.00 stock trees)
- CHANGELOG_Yurij-eu2av_EN.md inside the fork clone

## Verified facts (unchanged from last session)

- WDSP source: Project Files\Source\wdsp (built by wdsp.vcxproj, v145/x64).
- Version gate: Versions.cs:23 + console.cs:44705-44748.
- Wisdom: thetis wdspWisdom00 vs 2.00 wdspWisdom01; C# radio.cs:105-156.
- Hard breaks / signature changes: per plan Sections 6.2-6.3, now with
  reference answers (see above).
