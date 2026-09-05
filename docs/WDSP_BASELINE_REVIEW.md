# WDSP upstream and port baseline

Recorded 2026-09-04, before native portability edits.

## Authoritative reference

Use [TAPR/OpenHPSDR-wdsp](https://github.com/TAPR/OpenHPSDR-wdsp) as the
authoritative WDSP algorithm/API reference. The inspected revision is
`584e8aca5ba1c4c6bc66fc0cc164ce567c8ba1e3` (2026-07-06, “Release Version 2.00”).
The release source is under `wdsp 2.00/Source`; its accompanying reference manual
is `wdsp 2.00/WDSP_Guide, Rev 2.00.pdf`. TAPR's release notes date 2.00 to
2026-07-01 and identify PureSignal 3.0 among its additions.

The project's `Project Files/Source/wdsp/version.c` and TAPR's 2.00 `version.c`
are identical: `GetWDSPVersion()` returns `200`, representing **2.00**. However,
that version value does not establish source, ABI or feature equivalence.

## Local differences to preserve and qualify

Compared the local source at discovery checkpoint `77792260` (native files still
from the adopted `3518930b` baseline) against the pinned TAPR release:

- Of TAPR's 151 `.c`/`.h` files, 127 are byte-identical and 24 differ; none are
  missing locally.
- Four additional local source/header files implement the NR3/NR4 integration:
  `rnnr.c`, `rnnr.h`, `sbnr.c`, `sbnr.h`. Their RNNoise/libspecbleach dependencies
  still need source-built native targets.
- The local analyzer adds pixel-reference tracking. Its `GetPixels` takes a
  fifth `double*` output argument, unlike the four-argument TAPR implementation.
  New interop must match the **local** signature.
- Local EQ and CFC code adds Q-factor controls; RX/TX setup and processing have
  corresponding changes. These are not merely build-system differences.
- **PureSignal is not TAPR's complete 3.0 implementation.** Local `iqc.c` and
  `iqc.h` are byte-identical to TAPR's **1.29** versions. Local `calcc` retains
  older cubic-spline calibration structures with additional adaptations, instead
  of the NURBS-based 2.00 calibration structures. The source explicitly describes
  its compatibility mapping of the newer AmpView call to WDSP 1.x coefficients.
- Local `SetPSPinAlpha`, `SetPSDCBEnable`, `SetPSDCBCap` and `SetPSEQEnable` are
  no-op exports. Their presence must not be treated as implemented PS3 controls.

The 24 differing shared files are:

```text
RXA.c RXA.h TXA.c TXA.h amd.c analyzer.c analyzer.h anf.c anr.c
calcc.c calcc.h cblock.c cblock.h cfcomp.c cfcomp.h comm.h
emnr.c eq.c eq.h fmsq.c iqc.c iqc.h snb.c ssql.c
```

These are static comparisons, not runtime or RF validation. The file-count
comparison excludes project/resource files and dependencies outside the WDSP
source directory.

## Porting decision

1. Build the existing project source in place, identifying it as a **modified
   WDSP 2.00-derived baseline**. Do not substitute stock TAPR binaries or infer
   compatibility from `GetWDSPVersion()` alone.
2. Use TAPR's source/manual to understand the algorithms and upstream contracts;
   check local declarations and call sites for every interop binding.
3. Consult cross-platform implementations only as secondary portability
   references. The initially inspected `g0orx/wdsp` revision
   `49084f50c583a73644e03bcb56443fa9deb327de` reports **1.18**; it is not an
   appropriate replacement DSP baseline. No source from it has been imported.
4. Keep adoption of TAPR's full PS3 implementation separate from OS portability.
   That would require coordinated native/managed API, display, settings and RF
   validation work, not an incidental source refresh during M2.

The TAPR and secondary reference clones are under ignored `artifacts/external/`.
Neither has been merged or added as a production build dependency. The discovery
checkpoint is committed. The comparison above predates portability edits; the
subsequent native build and synthetic DSP work is recorded in [M2 results](M2_NATIVE_RESULTS.md).
